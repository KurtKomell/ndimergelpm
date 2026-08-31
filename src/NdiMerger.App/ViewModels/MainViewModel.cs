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
    private byte[]? _previewUiBuffer;
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
    private WriteableBitmap? _previewBitmap;
    private DateTime _lastFpsTime = DateTime.UtcNow;
    private int _frameCounter;
    private CompositionLayer? _dragLayer;
    private Point _dragStartMouse;
    private float _dragStartX;
    private float _dragStartY;
    private bool _isDragging;
    private AppSession? _loadedSession;

    public AppSession? LoadedSession => _loadedSession;

    public ObservableCollection<DiscoveredSource> AvailableSources { get; } = [];
    public ObservableCollection<CompositionLayer> Layers { get; } = [];
    public ObservableCollection<ZoneDefinition> Zones { get; } = [];

    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private CompositionLayer? _selectedLayer;
    [ObservableProperty] private DiscoveredSource? _selectedSource;
    [ObservableProperty] private ZoneDefinition? _selectedZone;
    [ObservableProperty] private ScaleMode _selectedScaleMode = ScaleMode.Native;
    [ObservableProperty] private string _outputName = "MAM-Pixelmap";
    [ObservableProperty] private bool _ndiSending = true;
    [ObservableProperty] private bool _showBackgroundInPreview = true;
    [ObservableProperty] private bool _showBackgroundInOutput = false;
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
        IVideoSource runtime;
        try
        {
            runtime = _hub.GetOrCreate(src.Kind, src.Key, src.DisplayName);
            for (int i = 0; i < 30 && runtime.Width <= 0; i++)
            {
                runtime.Update();
                if (runtime.Width <= 0)
                    Thread.Sleep(16);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Source failed ({src.DisplayName}): {ex.Message}";
            return;
        }

        var (nativeW, nativeH) = ResolveNativeSize(src.Kind, src.Key, src.DisplayName, runtime, src.Width, src.Height);

        var layer = new CompositionLayer
        {
            Name = src.DisplayName,
            SourceKind = src.Kind,
            SourceKey = src.Key,
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
    }

    [RelayCommand]
    private void RemoveSelectedLayer()
    {
        if (SelectedLayer is null) return;
        Layers.Remove(SelectedLayer);
        SelectedLayer = null;
        Reindex();
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
            OutputName,
            ShowBackgroundInOutput,
            ShowBackgroundInPreview,
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
                Visible = e.Visible,
                ZIndex = e.ZIndex,
                NativeWidth = nativeW,
                NativeHeight = nativeH
            });
        }

        if (!string.IsNullOrEmpty(doc.SelectedLayerKey))
        {
            SelectedLayer = Layers.FirstOrDefault(l =>
                LayoutSerializer.LayerKey(l.SourceKind, l.SourceKey) == doc.SelectedLayerKey);
        }
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

    private void UiTick()
    {
        TickWallCarousel();

        if (_previewDirty && _previewBitmap is not null && _previewUiBuffer is not null && _compositor is not null)
        {
            lock (_renderLock)
            {
                _previewBitmap.WritePixels(
                    new Int32Rect(0, 0, _compositor.PreviewWidth, _compositor.PreviewHeight),
                    _previewUiBuffer, _compositor.PreviewWidth * 4, 0);
                _previewDirty = false;
            }
        }

        Fps = _renderFps;
    }

    private void RenderLoop()
    {
        while (!_renderExit)
        {
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
                var status =
                    $"FPS {_renderFps:0.0} | NDI {ndiRes} {ndiBw} | {bufMb} | viewers={conns} | Layers={Layers.Count}";

                if (!_renderExit && !_disposed && !CarouselBusy)
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!_renderExit && !_disposed && !CarouselBusy)
                            StatusText = status;
                    });
            }
        }
        catch (Exception ex)
        {
            if (!_renderExit && !_disposed)
                Application.Current?.Dispatcher.BeginInvoke(() => StatusText = $"Frame error: {ex.Message}");
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
            if (!layer.Visible) continue;
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
