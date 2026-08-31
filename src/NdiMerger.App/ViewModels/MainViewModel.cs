using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NdiMerger.App;
using NdiMerger.Core;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using NdiMerger.Output;
using NdiMerger.Sources;
using Vortice.Direct3D11;

namespace NdiMerger.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly object _renderLock = new();
    private readonly SourceRuntimeHub _hub = new();
    private Thread? _renderThread;
    private volatile bool _renderExit;
    private volatile bool _disposed;
    private volatile bool _previewDirty;
    private double _renderFps;
    private int _previewFrameCounter;
    private NdiSourceCatalog? _ndiCatalog;
    private SpoutSourceCatalog? _spoutCatalog;
    private GpuDevice? _gpu;
    private PixelMapCompositor? _compositor;
    private NdiOutputSender? _ndiOut;
    private PixelMapDefinition _pixelMap = new();
    private byte[]? _previewBuffer;
    private byte[]? _previewUiBuffer;
    private byte[]? _previewPresentBuffer;
    private WriteableBitmap? _previewBitmap;
    private DateTime _lastFpsTime = DateTime.UtcNow;
    private int _frameCounter;
    private volatile string? _pendingStatus;
    private CompositionLayer? _dragLayer;
    private Point _dragStartMouse;
    private float _dragStartX;
    private float _dragStartY;
    private bool _isDragging;
    private AppSession? _loadedSession;

    public AppSession? LoadedSession => _loadedSession;

    public ObservableCollection<DiscoveredSource> AvailableSources { get; } = [];
    public ObservableCollection<CompositionLayer> Layers { get; } = [];
    public ObservableCollection<LayerGroup> Groups { get; } = [];
    public ObservableCollection<LayerTreeNode> LayerTree { get; } = [];
    public ObservableCollection<ZoneDefinition> Zones { get; } = [];
    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];

    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private CompositionLayer? _selectedLayer;
    [ObservableProperty] private LayerTreeNode? _selectedTreeNode;
    [ObservableProperty] private DiscoveredSource? _selectedSource;
    [ObservableProperty] private ZoneDefinition? _selectedZone;
    [ObservableProperty] private ScaleMode _selectedScaleMode = ScaleMode.Native;
    [ObservableProperty] private string _outputName = "MAM-Pixelmap";
    [ObservableProperty] private bool _ndiSending = true;
    [ObservableProperty] private bool _showBackgroundInPreview = true;
    [ObservableProperty] private bool _showBackgroundInOutput = false;
    [ObservableProperty] private bool _showLayerOverlays = true;
    [ObservableProperty] private string _selectedSourceHud = "";
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private double _fps;
    [ObservableProperty] private string _canvasInfo = "";
    [ObservableProperty] private int _canvasWidth;
    [ObservableProperty] private int _canvasHeight;
    [ObservableProperty] private double _previewZoom = 1.0;
    [ObservableProperty] private double _carouselDurationSeconds = 1.0;
    [ObservableProperty] private WallCarouselDirection _carouselDirection = WallCarouselDirection.WestToOst;
    [ObservableProperty] private bool _carouselBusy;
    [ObservableProperty] private bool _floorReflectionEnabled = true;
    [ObservableProperty] private double _floorReflectionOpacity = 0.45;
    [ObservableProperty] private double _floorReflectionBlur = 8;
    [ObservableProperty] private double _floorReflectionLength = 0.4;
    [ObservableProperty] private double _floorReflectionAngle = 0.35;
    [ObservableProperty] private double _floorReflectionFadeStart = 0;
    [ObservableProperty] private GroupChoice? _selectedGroupChoice;

    public Array ScaleModes { get; } = Enum.GetValues(typeof(ScaleMode));
    public IReadOnlyList<CarouselDirectionChoice> CarouselDirections =>
        WallCarousel.DirectionChoices;

    private readonly List<WallMotionTrack> _wallTracks = [];
    private IReadOnlyList<WallCommitSpec> _wallCommits = [];
    private DateTime _wallAnimStart;
    private double _wallAnimDuration = 1.0;
    private int _wallCycleIndex;
    private const int WallCyclesPerRound = 3;
    private volatile bool _wallFinishQueued;
    private WallCarouselDirection _wallRoundDirection;

    partial void OnCarouselBusyChanged(bool value) =>
        PushWallsCommand.NotifyCanExecuteChanged();

    public MainViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => UiTick();
    }

    public void Initialize(string assetsDir)
    {
        try
        {
            _loadedSession = SessionStore.TryLoad();
            var savedLayout = _loadedSession?.Layout;

            if (savedLayout is not null)
            {
                OutputName = savedLayout.OutputName;
                ShowBackgroundInOutput = savedLayout.ShowBackgroundInOutput;
                ShowBackgroundInPreview = savedLayout.ShowBackgroundInPreview;
                ShowLayerOverlays = savedLayout.ShowLayerOverlays;
                NdiSending = savedLayout.NdiSending;
                SelectedScaleMode = savedLayout.SelectedScaleMode;
                CarouselDurationSeconds = Math.Clamp(savedLayout.CarouselDurationSeconds, 0.3, 5.0);
                CarouselDirection = savedLayout.CarouselDirection;
                ApplyFloorReflectionSettings(savedLayout);
            }

            var mapPath = Path.Combine(assetsDir, "pixelmap.json");
            _pixelMap = PixelMapLoader.LoadFromFile(mapPath);
            Zones.Clear();
            foreach (var z in _pixelMap.Zones)
                Zones.Add(z);

            CanvasInfo = $"{_pixelMap.Name}  {_pixelMap.CanvasWidth}×{_pixelMap.CanvasHeight}";
            CanvasWidth = _pixelMap.CanvasWidth;
            CanvasHeight = _pixelMap.CanvasHeight;
            NdiFrameSpec.ValidateCanvasSize(_pixelMap.CanvasWidth, _pixelMap.CanvasHeight);

            _gpu = new GpuDevice();
            StatusText = $"GPU: {_gpu.AdapterName}";
            _hub.AttachGpu(_gpu);
            _compositor = new PixelMapCompositor(_gpu, _pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
            _previewBuffer = new byte[_compositor.PreviewWidth * _compositor.PreviewHeight * 4];
            _previewUiBuffer = new byte[_previewBuffer.Length];
            _previewPresentBuffer = new byte[_previewBuffer.Length];
            _previewBitmap = new WriteableBitmap(
                _compositor.PreviewWidth, _compositor.PreviewHeight, 96, 96,
                PixelFormats.Bgra32, null);
            PreviewImage = _previewBitmap;

            var bgName = _pixelMap.BackgroundImage ?? "pixelmap.png";
            var bgPath = Path.Combine(assetsDir, bgName);
            if (!File.Exists(bgPath))
            {
                // prefer PNG over legacy JPG
                var png = Path.Combine(assetsDir, "pixelmap.png");
                var jpg = Path.Combine(assetsDir, "pixelmap.jpg");
                bgPath = File.Exists(png) ? png : jpg;
            }
            if (File.Exists(bgPath))
            {
                try
                {
                    _compositor.LoadBackgroundFile(bgPath);
                    if (_compositor.BackgroundWidth != _pixelMap.CanvasWidth ||
                        _compositor.BackgroundHeight != _pixelMap.CanvasHeight)
                    {
                        StatusText =
                            $"WARN: Template {_compositor.BackgroundWidth}×{_compositor.BackgroundHeight} ≠ canvas {_pixelMap.CanvasWidth}×{_pixelMap.CanvasHeight}";
                    }
                    else
                    {
                        StatusText = $"Template OK {_compositor.BackgroundWidth}×{_compositor.BackgroundHeight} (1:1)";
                    }
                }
                catch (Exception ex) { StatusText = $"Background load warning: {ex.Message}"; }
            }

            _ndiOut = new NdiOutputSender(_gpu, OutputName);
            try
            {
                _ndiOut.Initialize(_pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
            }
            catch (Exception ex)
            {
                NdiSending = false;
                StatusText = $"NDI init failed: {ex.Message}";
            }

            try { _ndiCatalog = new NdiSourceCatalog(); }
            catch (Exception ex)
            {
                if (!StatusText.StartsWith("NDI init failed"))
                    StatusText = $"NDI finder: {ex.Message}";
            }

            try { _spoutCatalog = new SpoutSourceCatalog(); }
            catch { /* optional */ }

            RefreshSources();
            if (savedLayout is not null)
            {
                SelectedZone = string.IsNullOrEmpty(savedLayout.SelectedZoneId)
                    ? null
                    : Zones.FirstOrDefault(z => z.Id == savedLayout.SelectedZoneId);
                ApplyLayers(savedLayout);
            }

            _timer.Start();
            _renderExit = false;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "NdiMerger-Render",
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
            if (!StatusText.StartsWith("NDI init failed") && !StatusText.StartsWith("WARN:"))
                StatusText = $"Ready | NDI {NdiFrameSpec.Width}×{NdiFrameSpec.Height} BGRA | {NdiFrameSpec.FormatBufferSizeMb()}";
        }
        catch (Exception ex)
        {
            StatusText = $"Init failed: {ex.Message}";
            MessageBox.Show(ex.ToString(), "NdiMergerLPM");
        }
    }

    [RelayCommand]
    private void RefreshSources()
    {
        AvailableSources.Clear();
        foreach (var s in VideoSourceFactory.DiscoverAll(_ndiCatalog, _spoutCatalog))
            AvailableSources.Add(s);
        StatusText = $"Sources: {AvailableSources.Count}";
    }

    [RelayCommand]
    private void AddSelectedSource()
    {
        if (SelectedSource is null || _gpu is null) return;
        var src = SelectedSource;
        AddLayerFromSource(src.Kind, src.Key, src.DisplayName, src.Width, src.Height);
    }

    [RelayCommand]
    private void AddBrowserSource()
    {
        if (_gpu is null) return;

        var dialog = new BrowserSourceDialog
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var spec = BrowserSourceSpec.Create(dialog.Url, dialog.WidthPx, dialog.HeightPx);
            AddLayerFromSource(SourceKind.Browser, spec.Key, spec.DisplayName, spec.Width, spec.Height);
        }
        catch (Exception ex)
        {
            StatusText = $"Browser source failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Browser Source", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddLayerFromSource(SourceKind kind, string key, string displayName, int discoveredW = 0, int discoveredH = 0)
    {
        IVideoSource runtime;
        try
        {
            runtime = _hub.GetOrCreate(kind, key, displayName);
            for (int i = 0; i < 30 && runtime.Width <= 0; i++)
            {
                runtime.Update();
                if (runtime.Width <= 0)
                    Thread.Sleep(16);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Source failed ({displayName}): {ex.Message}";
            return;
        }

        var (nativeW, nativeH) = ResolveNativeSize(kind, key, displayName, runtime, discoveredW, discoveredH);

        var layer = new CompositionLayer
        {
            Name = displayName,
            SourceKind = kind,
            SourceKey = key,
            ZIndex = Layers.Count,
            NativeWidth = nativeW,
            NativeHeight = nativeH,
            X = 100,
            Y = 100
        };

        if (SelectedZone is not null)
            layer.ApplyZone(SelectedZone, SelectedScaleMode);

        Layers.Add(layer);
        SelectedLayer = layer;
        RebuildLayerTree();
    }

    [RelayCommand]
    private void CopySelectedLayer()
    {
        if (SelectedLayer is null || _gpu is null) return;
        var src = SelectedLayer;

        try
        {
            _hub.GetOrCreate(src.SourceKind, src.SourceKey, src.Name);
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed ({src.Name}): {ex.Message}";
            return;
        }

        var copy = new CompositionLayer
        {
            Name = src.Name.EndsWith(" (copy)", StringComparison.Ordinal)
                ? src.Name
                : src.Name + " (copy)",
            SourceKind = src.SourceKind,
            SourceKey = src.SourceKey,
            X = src.X + 40,
            Y = src.Y + 40,
            Scale = src.Scale,
            RotationDegrees = src.RotationDegrees,
            Opacity = src.Opacity,
            ScaleMode = ScaleMode.Native,
            ZoneId = null,
            GroupId = src.GroupId,
            Visible = src.Visible,
            ParentGroupVisible = src.ParentGroupVisible,
            ZIndex = Layers.Count,
            NativeWidth = src.NativeWidth,
            NativeHeight = src.NativeHeight,
            BlackKeyEnabled = src.BlackKeyEnabled,
            BlackKeyThreshold = src.BlackKeyThreshold
        };

        Layers.Add(copy);
        SelectedLayer = copy;
        RebuildLayerTree();
        StatusText = $"Copied {src.Name}";
    }

    [RelayCommand]
    private void AddLayerGroup()
    {
        var members = GetMarkedLayers();
        if (members.Count == 0 && SelectedLayer is not null)
            members = [SelectedLayer];

        var group = new LayerGroup
        {
            Name = $"Group {Groups.Count + 1}",
            SortOrder = Groups.Count,
            Visible = true,
            IsExpanded = true
        };
        group.PropertyChanged += OnGroupPropertyChanged;
        Groups.Add(group);

        foreach (var layer in members)
        {
            layer.GroupId = group.Id;
            layer.ParentGroupVisible = group.Visible;
            layer.MarkedForGroup = false;
        }

        RebuildLayerTree();
        StatusText = members.Count > 0
            ? $"Created {group.Name} ({members.Count} layer{(members.Count == 1 ? "" : "s")})"
            : $"Created {group.Name} — mark layers with the left checkbox, then Group again";
    }

    [RelayCommand]
    private void AssignMarkedToGroup()
    {
        LayerGroup? target = null;
        if (SelectedTreeNode is { IsGroup: true, Group: not null })
            target = SelectedTreeNode.Group;
        else if (SelectedGroupChoice?.Id is Guid gid)
            target = Groups.FirstOrDefault(g => g.Id == gid);

        if (target is null)
        {
            StatusText = "Select a group (or choose Gruppe), mark layers, then Assign.";
            return;
        }

        var members = GetMarkedLayers();
        if (members.Count == 0 && SelectedLayer is not null)
            members = [SelectedLayer];

        if (members.Count == 0)
        {
            StatusText = "Mark layers with the left checkbox first.";
            return;
        }

        foreach (var layer in members)
        {
            layer.GroupId = target.Id;
            layer.ParentGroupVisible = target.Visible;
            layer.MarkedForGroup = false;
        }

        RebuildLayerTree();
        StatusText = $"Assigned {members.Count} layer(s) to {target.Name}";
    }

    [RelayCommand]
    private void ClearLayerMarks()
    {
        foreach (var layer in Layers)
            layer.MarkedForGroup = false;
        foreach (var node in LayerTree)
        {
            if (node.IsGroup)
                node.Marked = false;
        }
    }

    private List<CompositionLayer> GetMarkedLayers() =>
        Layers.Where(l => !WallCarousel.IsMovingCopy(l) && l.MarkedForGroup).ToList();

    [RelayCommand]
    private void RemoveSelectedLayer()
    {
        if (SelectedLayer is null) return;
        Layers.Remove(SelectedLayer);
        SelectedLayer = null;
        Reindex();
        RebuildLayerTree();
    }

    [RelayCommand]
    private void DeleteSelectedGroup()
    {
        LayerGroup? group = null;
        if (SelectedTreeNode is { IsGroup: true, Group: not null })
            group = SelectedTreeNode.Group;
        else if (SelectedLayer?.GroupId is Guid gid)
            group = Groups.FirstOrDefault(g => g.Id == gid);

        if (group is null) return;

        foreach (var layer in Layers.Where(l => l.GroupId == group.Id))
        {
            layer.GroupId = null;
            layer.ParentGroupVisible = true;
        }

        group.PropertyChanged -= OnGroupPropertyChanged;
        Groups.Remove(group);
        ReindexGroups();
        RebuildLayerTree();
        StatusText = $"Removed group {group.Name}";
    }

    [RelayCommand]
    private void LayerUp()
    {
        if (SelectedLayer is null) return;
        int i = Layers.IndexOf(SelectedLayer);
        if (i < Layers.Count - 1)
        {
            Layers.Move(i, i + 1);
            Reindex();
            RebuildLayerTree();
        }
    }

    [RelayCommand]
    private void LayerDown()
    {
        if (SelectedLayer is null) return;
        int i = Layers.IndexOf(SelectedLayer);
        if (i > 0)
        {
            Layers.Move(i, i - 1);
            Reindex();
            RebuildLayerTree();
        }
    }

    [RelayCommand]
    private void SnapToZone()
    {
        if (SelectedLayer is null || SelectedZone is null) return;
        SelectedLayer.ApplyZone(SelectedZone, SelectedScaleMode);
        OnPropertyChanged(nameof(SelectedLayer));
    }

    [RelayCommand(CanExecute = nameof(CanPushWalls))]
    private void PushWalls()
    {
        if (CarouselBusy)
            return;

        if (Zones.Count == 0)
        {
            StatusText = "Pixelmap/Zonen noch nicht geladen.";
            return;
        }

        try
        {
            CarouselBusy = true;
            _wallCycleIndex = 0;
            _wallRoundDirection = CarouselDirection;
            _wallFinishQueued = false;
            StatusText = "Volle Runde startet (3× synchron)…";
            BeginSimultaneousCycle();
        }
        catch (Exception ex)
        {
            AbortWallCarousel($"Wand-Runde fehlgeschlagen: {ex.Message}");
            MessageBox.Show(ex.ToString(), "Wand-Karussell");
        }
    }

    private bool CanPushWalls() => !CarouselBusy;

    private void AbortWallCarousel(string message)
    {
        CleanupMovingCopies();
        _wallTracks.Clear();
        _wallCommits = [];
        _wallCycleIndex = 0;
        _wallFinishQueued = false;
        CarouselBusy = false;
        StatusText = message;
    }

    private void BeginSimultaneousCycle()
    {
        var zoneList = Zones.ToList();
        var layerList = Layers.ToList();
        var missing = WallCarousel.DescribeMissingWallLayers(zoneList, layerList);
        if (missing is not null)
        {
            AbortWallCarousel($"Fehlende Wand-Layer: {missing} — je einen Layer auf West/Ost/Süd snappen.");
            MessageBox.Show(
                $"Für die volle Runde fehlen Layer auf: {missing}\n\n" +
                "Bitte je einen Spout/NDI-Layer auf West, Ost und Süd snappen (Zone snap).",
                "Wand-Karussell",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var cycle = WallCarousel.TryBuildSimultaneousCycle(zoneList, layerList, _wallRoundDirection);
        if (cycle is null)
        {
            AbortWallCarousel("Wand-Runde konnte nicht gestartet werden.");
            return;
        }

        _wallTracks.Clear();
        _wallTracks.AddRange(cycle.Tracks);
        _wallCommits = cycle.Commits;

        lock (_renderLock)
        {
            foreach (var commit in cycle.Commits)
                commit.Original.Opacity = 0f;

            InsertMovingCopiesAboveSources(cycle.MovingCopies);
        }

        Reindex();
        _wallAnimDuration = Math.Clamp(CarouselDurationSeconds, 0.3, 5.0);
        _wallAnimStart = DateTime.UtcNow;
        _wallFinishQueued = false;
        int step = _wallCycleIndex + 1;
        StatusText = $"Runde Schritt {step}/{WallCyclesPerRound} — 3 Wände gleichzeitig…";
    }

    private void AdvanceWallCarouselLocked()
    {
        if (!CarouselBusy || _wallTracks.Count == 0)
            return;

        double t = Math.Min(1.0, (DateTime.UtcNow - _wallAnimStart).TotalSeconds / _wallAnimDuration);
        float u = SmoothStep((float)t);

        foreach (var track in _wallTracks)
        {
            var layer = track.Layer;
            layer.X = Lerp(track.StartX, track.EndX, u);
            layer.Y = Lerp(track.StartY, track.EndY, u);
            layer.Scale = Lerp(track.StartScale, track.EndScale, u);
            layer.RotationDegrees = track.Rotation;
        }
    }

    private bool IsWallCycleComplete()
    {
        if (!CarouselBusy || _wallTracks.Count == 0)
            return false;

        double t = (DateTime.UtcNow - _wallAnimStart).TotalSeconds / _wallAnimDuration;
        return t >= 1.0;
    }

    private void TickWallCarousel()
    {
        if (!CarouselBusy || _wallTracks.Count == 0)
            return;

        lock (_renderLock)
            AdvanceWallCarouselLocked();

        if (!IsWallCycleComplete() || _wallFinishQueued)
            return;

        _wallFinishQueued = true;
        FinishWallCycle();
    }

    private void FinishWallCycle()
    {
        _wallFinishQueued = false;

        try
        {
            lock (_renderLock)
            {
                foreach (var commit in _wallCommits)
                {
                    var o = commit.Original;
                    o.ZoneId = commit.TargetZone.Id;
                    o.X = commit.EndX;
                    o.Y = commit.EndY;
                    o.Scale = commit.EndScale;
                    o.RotationDegrees = commit.EndRotation;
                    o.ScaleMode = commit.EndScaleMode;
                    o.Opacity = commit.SavedOpacity;
                    o.Visible = true;
                }
            }

            CleanupMovingCopies();
            _wallTracks.Clear();
            _wallCommits = [];

            _wallCycleIndex++;
            if (_wallCycleIndex < WallCyclesPerRound)
            {
                BeginSimultaneousCycle();
                return;
            }

            CarouselBusy = false;
            StatusText = "Volle Runde fertig — wieder am Ausgangspunkt";
        }
        catch (Exception ex)
        {
            AbortWallCarousel($"Wand-Runde abgebrochen: {ex.Message}");
            MessageBox.Show(ex.ToString(), "Wand-Karussell");
        }
    }

    private void CleanupMovingCopies()
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            if (WallCarousel.IsMovingCopy(Layers[i]))
                Layers.RemoveAt(i);
        }

        // Restore any originals left invisible if a hop aborted.
        foreach (var layer in Layers)
        {
            if (!WallCarousel.IsMovingCopy(layer) &&
                WallCarousel.ResolveGroup(layer.ZoneId) is not null &&
                layer.Opacity <= 0.01f)
            {
                layer.Opacity = 1f;
            }
        }

        Reindex();
        RebuildLayerTree();
    }

    /// <summary>
    /// Places each moving copy directly above its source layer, preserving Layers order.
    /// </summary>
    private void InsertMovingCopiesAboveSources(IReadOnlyList<WallMovingCopy> movingCopies)
    {
        var groups = movingCopies
            .GroupBy(m => m.InsertAbove)
            .Select(g => (Anchor: g.Key, Copies: g.Select(m => m.Copy).ToList()))
            .OrderByDescending(g => Layers.IndexOf(g.Anchor))
            .ToList();

        foreach (var (anchor, copies) in groups)
        {
            int idx = Layers.IndexOf(anchor);
            if (idx < 0)
            {
                foreach (var copy in copies)
                    Layers.Add(copy);
                continue;
            }

            for (int i = 0; i < copies.Count; i++)
                Layers.Insert(idx + 1 + i, copies[i]);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    [RelayCommand]
    private void SaveLayout()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Layout JSON|*.json",
            FileName = "layout.json"
        };
        if (dlg.ShowDialog() != true) return;
        var doc = BuildLayoutDocument();
        File.WriteAllText(dlg.FileName, LayoutSerializer.Save(doc));
        StatusText = $"Saved {dlg.FileName}";
    }

    [RelayCommand]
    private void LoadLayout()
    {
        var dlg = new OpenFileDialog { Filter = "Layout JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        var doc = LayoutSerializer.Load(File.ReadAllText(dlg.FileName));
        ApplySettings(doc);
        ApplyLayers(doc);
        StatusText = $"Loaded {dlg.FileName}";
    }

    private LayoutDocument BuildLayoutDocument()
    {
        string? selectedLayerKey = SelectedLayer is not null
            ? LayoutSerializer.LayerKey(SelectedLayer.SourceKind, SelectedLayer.SourceKey)
            : null;

        return LayoutSerializer.FromLayers(
            Layers.Where(l => !WallCarousel.IsMovingCopy(l)),
            Groups,
            OutputName,
            ShowBackgroundInOutput,
            ShowBackgroundInPreview,
            ShowLayerOverlays,
            NdiSending,
            SelectedScaleMode,
            SelectedZone?.Id,
            selectedLayerKey,
            CarouselDurationSeconds,
            CarouselDirection,
            FloorReflectionEnabled,
            (float)FloorReflectionOpacity,
            (float)FloorReflectionBlur,
            (float)FloorReflectionLength,
            (float)FloorReflectionAngle,
            (float)FloorReflectionFadeStart);
    }

    private void ApplySettings(LayoutDocument doc)
    {
        OutputName = doc.OutputName;
        ShowBackgroundInOutput = doc.ShowBackgroundInOutput;
        ShowBackgroundInPreview = doc.ShowBackgroundInPreview;
        ShowLayerOverlays = doc.ShowLayerOverlays;
        NdiSending = doc.NdiSending;
        SelectedScaleMode = doc.SelectedScaleMode;
        CarouselDurationSeconds = Math.Clamp(doc.CarouselDurationSeconds, 0.3, 5.0);
        CarouselDirection = doc.CarouselDirection;
        ApplyFloorReflectionSettings(doc);
        if (Zones.Count > 0)
        {
            SelectedZone = string.IsNullOrEmpty(doc.SelectedZoneId)
                ? null
                : Zones.FirstOrDefault(z => z.Id == doc.SelectedZoneId);
        }
    }

    private void ApplyFloorReflectionSettings(LayoutDocument doc)
    {
        FloorReflectionEnabled = doc.FloorReflectionEnabled ?? true;
        FloorReflectionOpacity = Math.Clamp(doc.FloorReflectionOpacity ?? 0.45f, 0f, 1f);
        FloorReflectionBlur = Math.Clamp(doc.FloorReflectionBlur ?? 8f, 0f, 24f);
        FloorReflectionLength = Math.Clamp(doc.FloorReflectionLength ?? 0.4f, 0.05f, 1f);
        FloorReflectionAngle = Math.Clamp(doc.FloorReflectionAngle ?? 0.35f, 0f, 1f);
        FloorReflectionFadeStart = Math.Clamp(doc.FloorReflectionFadeStart ?? 0f, 0f, 1f);
    }

    private void ApplyLayers(LayoutDocument doc)
    {
        foreach (var g in Groups)
            g.PropertyChanged -= OnGroupPropertyChanged;
        Groups.Clear();

        foreach (var ge in doc.Groups.OrderBy(g => g.SortOrder))
        {
            if (!Guid.TryParse(ge.Id, out var gid))
                gid = Guid.NewGuid();
            var group = new LayerGroup
            {
                Id = gid,
                Name = string.IsNullOrWhiteSpace(ge.Name) ? "Group" : ge.Name,
                Visible = ge.Visible,
                IsExpanded = ge.IsExpanded,
                SortOrder = ge.SortOrder
            };
            group.PropertyChanged += OnGroupPropertyChanged;
            Groups.Add(group);
        }

        Layers.Clear();
        foreach (var e in doc.Layers.OrderBy(l => l.ZIndex))
        {
            IVideoSource? runtime = null;
            try
            {
                runtime = _hub.GetOrCreate(e.SourceKind, e.SourceKey, e.Name);
                for (int i = 0; i < 10 && runtime.Width <= 0; i++)
                    runtime.Update();
            }
            catch { /* source may be offline */ }

            var resolved = ResolveNativeSize(e.SourceKind, e.SourceKey, e.Name, runtime);
            int nativeW = resolved.Width > 0 ? resolved.Width : Math.Max(e.NativeWidth, 1920);
            int nativeH = resolved.Height > 0 ? resolved.Height : Math.Max(e.NativeHeight, 1080);

            Guid? groupId = null;
            if (!string.IsNullOrEmpty(e.GroupId) && Guid.TryParse(e.GroupId, out var parsed))
                groupId = parsed;

            bool parentVisible = true;
            if (groupId is Guid gid)
            {
                var group = Groups.FirstOrDefault(g => g.Id == gid);
                if (group is null)
                    groupId = null;
                else
                    parentVisible = group.Visible;
            }

            Layers.Add(new CompositionLayer
            {
                Name = e.Name,
                SourceKind = e.SourceKind,
                SourceKey = e.SourceKey,
                X = e.X,
                Y = e.Y,
                Scale = e.Scale,
                RotationDegrees = e.RotationDegrees,
                Opacity = e.Opacity,
                ScaleMode = e.ScaleMode,
                ZoneId = e.ZoneId,
                GroupId = groupId,
                ParentGroupVisible = parentVisible,
                Visible = e.Visible,
                ZIndex = e.ZIndex,
                NativeWidth = nativeW,
                NativeHeight = nativeH,
                BlackKeyEnabled = e.BlackKeyEnabled,
                BlackKeyThreshold = e.BlackKeyThreshold > 0 ? e.BlackKeyThreshold : 0.08f
            });
        }

        if (!string.IsNullOrEmpty(doc.SelectedLayerKey))
        {
            SelectedLayer = Layers.FirstOrDefault(l =>
                LayoutSerializer.LayerKey(l.SourceKind, l.SourceKey) == doc.SelectedLayerKey);
        }

        RebuildLayerTree();
    }

    private static (int Width, int Height) ResolveNativeSize(
        SourceKind kind,
        string key,
        string displayName,
        IVideoSource? runtime,
        int discoveredWidth = 0,
        int discoveredHeight = 0)
    {
        if (runtime is { Width: > 0, Height: > 0 })
            return (runtime.Width, runtime.Height);

        if (discoveredWidth > 0 && discoveredHeight > 0)
            return (discoveredWidth, discoveredHeight);

        if (kind == SourceKind.Spout && SpoutSourceCatalog.TryGetSenderSize(key, out var sw, out var sh))
            return (sw, sh);

        if (kind == SourceKind.Solid)
            return (SolidColorVideoSource.DefaultWidth, SolidColorVideoSource.DefaultHeight);

        if (kind == SourceKind.Browser && BrowserSourceSpec.TryParse(key, out var browser))
            return (browser.Width, browser.Height);

        if (TryParseSizeFromDisplayName(displayName, out var pw, out var ph))
            return (pw, ph);

        return (1920, 1080);
    }

    private static bool TryParseSizeFromDisplayName(string displayName, out int width, out int height)
    {
        width = 0;
        height = 0;
        var match = Regex.Match(displayName, @"\((\d+)x(\d+)\)\s*$");
        if (!match.Success) return false;
        width = int.Parse(match.Groups[1].Value);
        height = int.Parse(match.Groups[2].Value);
        return width > 0 && height > 0;
    }

    public void SaveSession(Window? window = null)
    {
        var session = new AppSession { Layout = BuildLayoutDocument() };
        if (window is not null)
        {
            session.WindowLeft = window.Left;
            session.WindowTop = window.Top;
            session.WindowWidth = window.Width;
            session.WindowHeight = window.Height;
            session.WindowState = (int)window.WindowState;
        }

        SessionStore.Save(session);
    }

    private void Reindex()
    {
        for (int i = 0; i < Layers.Count; i++)
            Layers[i].ZIndex = i;
    }

    private void ReindexGroups()
    {
        for (int i = 0; i < Groups.Count; i++)
            Groups[i].SortOrder = i;
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not LayerGroup group) return;
        if (e.PropertyName == nameof(LayerGroup.Visible))
            SyncParentGroupVisible(group);
        if (e.PropertyName is nameof(LayerGroup.Name) or nameof(LayerGroup.Visible))
            RefreshGroupChoices();
    }

    private void SyncParentGroupVisible(LayerGroup group)
    {
        foreach (var layer in Layers.Where(l => l.GroupId == group.Id))
            layer.ParentGroupVisible = group.Visible;
    }

    private void RebuildLayerTree()
    {
        LayerTree.Clear();
        RefreshGroupChoices();

        foreach (var group in Groups.OrderBy(g => g.SortOrder))
        {
            var node = new LayerTreeNode(group);
            foreach (var layer in Layers
                         .Where(l => !WallCarousel.IsMovingCopy(l) && l.GroupId == group.Id)
                         .OrderBy(l => l.ZIndex))
            {
                node.Children.Add(new LayerTreeNode(layer));
            }

            LayerTree.Add(node);
        }

        foreach (var layer in Layers
                     .Where(l => !WallCarousel.IsMovingCopy(l) && l.GroupId is null)
                     .OrderBy(l => l.ZIndex))
        {
            LayerTree.Add(new LayerTreeNode(layer));
        }

        SyncSelectedTreeNode();
    }

    private void RefreshGroupChoices()
    {
        var previousId = SelectedGroupChoice?.Id;
        _suppressGroupChoiceAssign = true;
        try
        {
            GroupChoices.Clear();
            GroupChoices.Add(new GroupChoice(null, "(none)"));
            foreach (var g in Groups.OrderBy(g => g.SortOrder))
                GroupChoices.Add(new GroupChoice(g.Id, g.Name));

            SelectedGroupChoice = GroupChoices.FirstOrDefault(c => c.Id == previousId)
                                  ?? GroupChoices.FirstOrDefault();
        }
        finally
        {
            _suppressGroupChoiceAssign = false;
        }
    }

    private void SyncSelectedTreeNode()
    {
        if (SelectedLayer is null)
        {
            SelectedTreeNode = null;
            return;
        }

        foreach (var node in LayerTree)
        {
            if (!node.IsGroup && ReferenceEquals(node.Layer, SelectedLayer))
            {
                SelectedTreeNode = node;
                return;
            }

            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child.Layer, SelectedLayer))
                {
                    SelectedTreeNode = child;
                    return;
                }
            }
        }
    }

    partial void OnSelectedTreeNodeChanged(LayerTreeNode? value)
    {
        if (value is { IsGroup: false, Layer: not null })
        {
            if (!ReferenceEquals(SelectedLayer, value.Layer))
                SelectedLayer = value.Layer;
        }
    }

    private CompositionLayer? _hudLayer;
    private bool _suppressGroupChoiceAssign;

    partial void OnSelectedLayerChanged(CompositionLayer? value)
    {
        if (_hudLayer is not null)
            _hudLayer.PropertyChanged -= OnSelectedLayerPropertyChanged;
        _hudLayer = value;
        if (_hudLayer is not null)
            _hudLayer.PropertyChanged += OnSelectedLayerPropertyChanged;

        UpdateSelectedSourceHud();
        SyncSelectedTreeNode();
        SyncGroupChoiceFromLayer();
    }

    private void OnSelectedLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompositionLayer.Name)
            or nameof(CompositionLayer.NativeWidth)
            or nameof(CompositionLayer.NativeHeight))
        {
            UpdateSelectedSourceHud();
        }
    }

    private void UpdateSelectedSourceHud()
    {
        if (SelectedLayer is null)
        {
            SelectedSourceHud = "";
            return;
        }

        SelectedSourceHud = $"{SelectedLayer.Name}\n{SelectedLayer.NativeWidth}×{SelectedLayer.NativeHeight}";
    }

    private void SyncGroupChoiceFromLayer()
    {
        _suppressGroupChoiceAssign = true;
        try
        {
            if (SelectedLayer is null)
            {
                SelectedGroupChoice = GroupChoices.FirstOrDefault();
                return;
            }

            SelectedGroupChoice = GroupChoices.FirstOrDefault(c => c.Id == SelectedLayer.GroupId)
                                  ?? GroupChoices.FirstOrDefault();
        }
        finally
        {
            _suppressGroupChoiceAssign = false;
        }
    }

    partial void OnSelectedGroupChoiceChanged(GroupChoice? value)
    {
        if (_suppressGroupChoiceAssign) return;
        if (SelectedLayer is null || value is null) return;
        if (SelectedLayer.GroupId == value.Id) return;

        SelectedLayer.GroupId = value.Id;
        if (value.Id is Guid gid)
        {
            var group = Groups.FirstOrDefault(g => g.Id == gid);
            SelectedLayer.ParentGroupVisible = group?.Visible ?? true;
        }
        else
        {
            SelectedLayer.ParentGroupVisible = true;
        }

        RebuildLayerTree();
    }

    private void UiTick()
    {
        TickWallCarousel();

        // Apply status on the UI thread without BeginInvoke storms from the render loop.
        var status = _pendingStatus;
        if (status is not null && !CarouselBusy)
        {
            _pendingStatus = null;
            if (status == "fps")
            {
                var conns = _ndiOut?.ConnectionCount ?? 0;
                var ndiRes = _ndiOut is { IsActive: true }
                    ? $"{_ndiOut.OutputWidth}×{_ndiOut.OutputHeight}"
                    : "off";
                var bufMb = _ndiOut?.BufferSizeBytes > 0
                    ? $"{_ndiOut.BufferSizeBytes / (1024.0 * 1024.0):0} MB/frame"
                    : NdiFrameSpec.FormatBufferSizeMb();
                var ndiBw = _ndiOut is { IsActive: true } && _ndiOut.SendBytesPerSecond > 0
                    ? NdiFrameSpec.FormatBandwidthGb(_ndiOut.SendBytesPerSecond)
                    : "—";
                StatusText =
                    $"FPS {_renderFps:0.0} | NDI {ndiRes} {ndiBw} | {bufMb} | viewers={conns} | Layers={Layers.Count}";
            }
            else
            {
                StatusText = status;
            }
        }

        if (_previewDirty && _previewBitmap is not null && _previewUiBuffer is not null &&
            _previewPresentBuffer is not null && _compositor is not null)
        {
            int w = _compositor.PreviewWidth;
            int h = _compositor.PreviewHeight;
            int bytes = w * h * 4;
            bool present = false;
            // Hold the render lock only for a tiny memcpy — never during WritePixels (WPF).
            lock (_renderLock)
            {
                if (_previewDirty)
                {
                    Buffer.BlockCopy(_previewUiBuffer, 0, _previewPresentBuffer, 0, bytes);
                    _previewDirty = false;
                    present = true;
                }
            }

            if (present)
                _previewBitmap.WritePixels(new Int32Rect(0, 0, w, h), _previewPresentBuffer, w * 4, 0);
        }

        Fps = _renderFps;
    }

    private void RenderLoop()
    {
        // Cap at 60 Hz when compose is cheap so we do not starve WebView2 / DWM on the GPU
        // without showing as "100% load". If a frame already takes longer (NDI readback), do not wait.
        const double targetFrameMs = 1000.0 / 60.0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (!_renderExit)
        {
            double startMs = watch.Elapsed.TotalMilliseconds;
            try
            {
                RenderFrame();
            }
            catch when (_renderExit)
            {
                break;
            }
            catch
            {
                // keep loop alive; errors surface via StatusText on next successful frame
            }

            double remaining = targetFrameMs - (watch.Elapsed.TotalMilliseconds - startMs);
            if (remaining >= 1.0)
                Thread.Sleep((int)remaining);
        }
    }

    private void RenderFrame()
    {
        if (_renderExit || _compositor is null || _gpu is null || _previewBuffer is null)
            return;

        try
        {
            _hub.UpdateAll();

            lock (_renderLock)
            {
                if (_renderExit)
                    return;

                foreach (var layer in Layers)
                {
                    var src = _hub.TryGet(layer.SourceKind, layer.SourceKey);
                    if (src is null) continue;
                    if (src.Width > 0 && src.Height > 0 &&
                        (layer.NativeWidth != src.Width || layer.NativeHeight != src.Height))
                    {
                        layer.NativeWidth = src.Width;
                        layer.NativeHeight = src.Height;
                        if (!CarouselBusy && layer.ZoneId is not null)
                        {
                            var group = WallCarousel.ResolveGroup(layer.ZoneId);
                            ZoneDefinition? zone = group is not null
                                ? WallCarousel.BuildTargetZone(group.Value, Zones)
                                : PixelMapLoader.FindZone(_pixelMap, layer.ZoneId);
                            if (zone is not null)
                                layer.ApplyZone(zone, layer.ScaleMode, applyZoneRotation: false);
                        }
                    }
                }

                AdvanceWallCarouselLocked();

                ID3D11ShaderResourceView? Resolve(CompositionLayer layer)
                {
                    var src = _hub.TryGet(layer.SourceKind, layer.SourceKey);
                    return src?.GetSrv();
                }

                var reflection = new FloorReflectionSettings
                {
                    Enabled = FloorReflectionEnabled,
                    Opacity = (float)FloorReflectionOpacity,
                    BlurPixels = (float)FloorReflectionBlur,
                    Length = (float)FloorReflectionLength,
                    Angle = (float)FloorReflectionAngle,
                    FadeStart = (float)FloorReflectionFadeStart
                };
                _compositor.Render(
                    Layers, Resolve,
                    ShowBackgroundInPreview, ShowBackgroundInOutput,
                    Zones, reflection);
            }

            if (!_renderExit && NdiSending && _ndiOut is not null && _ndiOut.IsActive)
                _ndiOut.SendFrame(_compositor.CanvasTexture);

            bool readPreview = !NdiSending || (++_previewFrameCounter % 4 == 0);
            if (!_renderExit && readPreview && _previewUiBuffer is not null &&
                _compositor.TryReadPreviewBgra(_previewBuffer, out _))
            {
                lock (_renderLock)
                {
                    Buffer.BlockCopy(_previewBuffer, 0, _previewUiBuffer, 0, _previewBuffer.Length);
                }
                _previewDirty = true;
            }

            _frameCounter++;
            var now = DateTime.UtcNow;
            var dt = (now - _lastFpsTime).TotalSeconds;
            if (dt >= 1.0)
            {
                _renderFps = _frameCounter / dt;
                _frameCounter = 0;
                _lastFpsTime = now;
                // Status string is assembled on the UI thread (UiTick) to avoid NDI/WPF work here.
                if (!CarouselBusy)
                    _pendingStatus = "fps";
            }
        }
        catch (Exception ex)
        {
            if (!_renderExit && !_disposed)
                _pendingStatus = $"Frame error: {ex.Message}";
        }
    }

    public void RequestShutdown()
    {
        if (_disposed)
            return;

        _renderExit = true;
        _timer.Stop();
        _ndiOut?.BeginShutdown();
    }

    public void OnPreviewMouseDown(Point canvasPixel, bool startDrag)
    {
        CompositionLayer? hit = null;
        foreach (var layer in Layers.OrderByDescending(l => l.ZIndex))
        {
            if (!layer.IsEffectivelyVisible) continue;
            var (bx, by, bw, bh) = layer.GetMapBounds();
            if (bw <= 0 || bh <= 0) continue;
            if (canvasPixel.X >= bx && canvasPixel.X <= bx + bw &&
                canvasPixel.Y >= by && canvasPixel.Y <= by + bh)
            {
                hit = layer;
                break;
            }
        }

        if (hit is not null)
        {
            SelectedLayer = hit;
            if (startDrag)
            {
                _dragLayer = hit;
                _dragStartMouse = canvasPixel;
                _dragStartX = hit.X;
                _dragStartY = hit.Y;
                _isDragging = true;
            }
        }
    }

    public void OnPreviewMouseMove(Point canvasPixel)
    {
        if (!_isDragging || _dragLayer is null) return;
        lock (_renderLock)
        {
            _dragLayer.X = _dragStartX + (float)(canvasPixel.X - _dragStartMouse.X);
            _dragLayer.Y = _dragStartY + (float)(canvasPixel.Y - _dragStartMouse.Y);
            _dragLayer.ZoneId = null;
            _dragLayer.ScaleMode = ScaleMode.Native;
        }
    }

    public void OnPreviewMouseUp()
    {
        _isDragging = false;
        _dragLayer = null;
    }

    partial void OnOutputNameChanged(string value)
    {
        if (_gpu is null || _pixelMap.CanvasWidth <= 0) return;
        _ndiOut?.Dispose();
        _ndiOut = new NdiOutputSender(_gpu, value);
        try
        {
            _ndiOut.Initialize(_pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
        }
        catch (Exception ex)
        {
            NdiSending = false;
            StatusText = $"NDI init failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        RequestShutdown();
        _renderThread?.Join(TimeSpan.FromSeconds(5));
        _ndiOut?.Dispose();
        _compositor?.Dispose();
        _hub.Dispose();
        _ndiCatalog?.Dispose();
        _spoutCatalog?.Dispose();
        _gpu?.Dispose();
    }
}

public sealed class GroupChoice
{
    public GroupChoice(Guid? id, string name)
    {
        Id = id;
        Name = name;
    }

    public Guid? Id { get; }
    public string Name { get; }
}
