using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
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
using NdiMerger.Tracking;
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
    private double _renderFps;
    private NdiSourceCatalog? _ndiCatalog;
    private SpoutSourceCatalog? _spoutCatalog;
    private GpuDevice? _gpu;
    private PixelMapCompositor? _compositor;
    private NdiOutputSender? _ndiOut;
    private PixelMapDefinition _pixelMap = new();
    private WriteableBitmap? _previewBitmap;
    private int _lastPreviewGen = -1;
    private int _previewPresentQueued;
    private long _lastPreviewEmitQpc;
    private long _lastFadeQpc;
    private int _previewEmitCounter;
    private double _previewFps;
    private DateTime _lastFpsTime = DateTime.UtcNow;
    private int _frameCounter;
    private volatile string? _pendingStatus;
    private volatile string? _pendingLidarStatus;
    private volatile bool _pendingLidarConnected;
    private volatile float _pendingLidarRpm;
    private volatile int _pendingLidarPointsPerRev;
    private CompositionLayer? _dragLayer;
    private Point _dragStartMouse;
    private float _dragStartX;
    private float _dragStartY;
    private bool _isDragging;
    private AppSession? _loadedSession;
    private LidarTrackingService? _lidar;

    public AppSession? LoadedSession => _loadedSession;

    public ObservableCollection<DiscoveredSource> AvailableSources { get; } = [];
    public ObservableCollection<CompositionLayer> Layers { get; } = [];
    public ObservableCollection<LayerGroup> Groups { get; } = [];
    public ObservableCollection<LayerTreeNode> LayerTree { get; } = [];
    public ObservableCollection<ZoneDefinition> Zones { get; } = [];
    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];
    public ObservableCollection<string> ComPorts { get; } = [];
    public ObservableCollection<NdiAdapterChoice> NdiAdapters { get; } = [];
    public ObservableCollection<NdiZoneStreamItem> NdiZoneStreams { get; } = [];

    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private CompositionLayer? _selectedLayer;
    [ObservableProperty] private LayerTreeNode? _selectedTreeNode;
    [ObservableProperty] private DiscoveredSource? _selectedSource;
    [ObservableProperty] private ZoneDefinition? _selectedZone;
    [ObservableProperty] private ScaleMode _selectedScaleMode = ScaleMode.Native;
    [ObservableProperty] private string _outputName = "MAM-Pixelmap";
    [ObservableProperty] private bool _ndiSending = true;
    [ObservableProperty] private NdiAdapterChoice? _selectedNdiAdapter;
    [ObservableProperty] private string _ndiSendingNicText = "Sendet: …";
    private int _nicProbeTick;
    private bool _ndiAdapterReady;
    private bool _suppressNdiStreamRecreate;
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
    [ObservableProperty] private double _fadeDurationSeconds = 2.0;
    [ObservableProperty] private WallCarouselDirection _carouselDirection = WallCarouselDirection.WestToOst;
    [ObservableProperty] private bool _carouselBusy;
    [ObservableProperty] private LayerGroup? _selectedGroup;
    [ObservableProperty] private bool _floorReflectionEnabled = true;
    [ObservableProperty] private double _floorReflectionOpacity = 0.45;
    [ObservableProperty] private double _floorReflectionBlur = 8;
    [ObservableProperty] private double _floorReflectionLength = 0.4;
    [ObservableProperty] private double _floorReflectionAngle = 0.35;
    [ObservableProperty] private double _floorReflectionFadeStart = 0;
    [ObservableProperty] private GroupChoice? _selectedGroupChoice;

    [ObservableProperty] private bool _lidarEnabled = true;
    [ObservableProperty] private string? _selectedComPort;
    [ObservableProperty] private bool _lidarConnected;
    [ObservableProperty] private string _lidarStatusText = "LiDAR getrennt";
    [ObservableProperty] private double _lidarRpm;
    [ObservableProperty] private int _lidarPointsPerRev;
    [ObservableProperty] private bool _lidarShowDebugPoints = true;
    [ObservableProperty] private bool _placingLidarSensor;
    [ObservableProperty] private double _lidarSensorX;
    [ObservableProperty] private double _lidarSensorY;
    [ObservableProperty] private double _lidarRotation;
    [ObservableProperty] private bool _lidarMirrorX;
    [ObservableProperty] private double _lidarPixelsPerMeter = 478;
    [ObservableProperty] private double _lidarFloorWidthM = 12;
    [ObservableProperty] private double _lidarFloorLengthM = 12;
    [ObservableProperty] private double _lidarMinDistanceMm = 300;
    [ObservableProperty] private double _lidarMaxDistanceMm = 12000;
    [ObservableProperty] private double _lidarForegroundMarginMm = 200;
    [ObservableProperty] private double _lidarClusterGapMm = 250;
    [ObservableProperty] private int _lidarMinClusterPoints = 3;
    [ObservableProperty] private double _lidarMinPersonWidthMm = 70;
    [ObservableProperty] private double _lidarMaxPersonWidthMm = 900;
    [ObservableProperty] private double _lidarMinMovementMm = 300;
    [ObservableProperty] private int _lidarMinHits = 2;
    [ObservableProperty] private int _lidarHoldMs = 500;
    [ObservableProperty] private double _lidarSmoothing = 0.35;
    [ObservableProperty] private double _lidarMaxJumpPx = 120;
    [ObservableProperty] private int _lidarMinQuality = 10;
    [ObservableProperty] private double _lidarBlobRadiusM = 0.45;
    [ObservableProperty] private double _lidarBlobSoftness = 0.65;
    [ObservableProperty] private double _lidarBlobOpacity = 0.9;
    [ObservableProperty] private double _lidarBlobBlur = 1.5;
    [ObservableProperty] private double _lidarBlobColorR = 0.2;
    [ObservableProperty] private double _lidarBlobColorG = 0.5;
    [ObservableProperty] private double _lidarBlobColorB = 1.0;
    [ObservableProperty] private double _lidarTrailLength = 1.5;
    [ObservableProperty] private double _lidarTrailStrength = 0.5;

    public Array ScaleModes { get; } = Enum.GetValues(typeof(ScaleMode));
    public IReadOnlyList<CarouselDirectionChoice> CarouselDirections =>
        WallCarousel.DirectionChoices;

    private CompositionLayer? _roundWest;
    private CompositionLayer? _roundOst;
    private CompositionLayer? _roundSud;
    private readonly List<WallMotionTrack> _wallTracks = [];
    private IReadOnlyList<WallCommitSpec> _wallCommits = [];
    private DateTime _wallAnimStart;
    private DateTime _wallRoundStart;
    private double _wallAnimDuration = 1.0;
    private int _wallCycleIndex;
    private const int WallCyclesPerRound = 3;
    private volatile bool _wallFinishQueued;
    private WallCarouselDirection _wallRoundDirection;

    partial void OnCarouselBusyChanged(bool value)
    {
        PushWallsCommand.NotifyCanExecuteChanged();
    }

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
            SelectNdiAdapter(savedLayout?.NdiAdapterId, savedLayout?.NdiAdapterIp, recreateSender: false);

            if (savedLayout is not null)
            {
                OutputName = savedLayout.OutputName;
                ShowBackgroundInOutput = savedLayout.ShowBackgroundInOutput;
                ShowBackgroundInPreview = savedLayout.ShowBackgroundInPreview;
                ShowLayerOverlays = savedLayout.ShowLayerOverlays;
                NdiSending = savedLayout.NdiSending;
                SelectedScaleMode = savedLayout.SelectedScaleMode;
                CarouselDurationSeconds = Math.Clamp(savedLayout.CarouselDurationSeconds, 0.3, 5.0);
                FadeDurationSeconds = Math.Clamp(savedLayout.FadeDurationSeconds, 0.0, 10.0);
                CarouselDirection = savedLayout.CarouselDirection;
                ApplyFloorReflectionSettings(savedLayout);
                ApplyLidarSettings(savedLayout.Lidar);
            }

            var mapPath = Path.Combine(assetsDir, "pixelmap.json");
            _pixelMap = PixelMapLoader.LoadFromFile(mapPath);
            Zones.Clear();
            foreach (var z in _pixelMap.Zones)
                Zones.Add(z);

            var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
            if (savedLayout?.Lidar is null && floor is not null)
                ApplyLidarSettings(LidarSettings.CreateDefaultForFloor(floor));

            _lidar = new LidarTrackingService();
            SyncLidarServiceSettings(floor);
            RefreshComPorts();
            if (savedLayout?.Lidar?.AutoConnect == true && !string.IsNullOrEmpty(savedLayout.Lidar.ComPort))
            {
                try { ConnectLidarInternal(savedLayout.Lidar.ComPort); }
                catch { /* optional auto-connect */ }
            }

            CanvasInfo = $"{_pixelMap.Name}  {_pixelMap.CanvasWidth}×{_pixelMap.CanvasHeight}";
            CanvasWidth = _pixelMap.CanvasWidth;
            CanvasHeight = _pixelMap.CanvasHeight;
            NdiFrameSpec.ValidateCanvasSize(_pixelMap.CanvasWidth, _pixelMap.CanvasHeight);

            _gpu = new GpuDevice();
            StatusText = $"GPU: {_gpu.AdapterName}";
            _hub.AttachGpu(_gpu);
            _compositor = new PixelMapCompositor(_gpu, _pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
            _compositor.PreviewFrameReady += OnPreviewFrameReady;
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

            InitNdiZoneStreams(savedLayout?.EnabledNdiZoneIds);
            RecreateNdiSender(OutputName);

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
                StatusText = $"Ready | NDI {NdiFrameSpec.Width}×{NdiFrameSpec.Height} UYVY SpeedHQ {NdiFrameSpec.TargetFps}fps 10GbE | {NdiFrameSpec.FormatBufferSizeMb()}";
        }
        catch (Exception ex)
        {
            StatusText = $"Init failed: {ex.Message}";
            MessageBox.Show(ex.ToString(), "NdiMergerLPM");
        }
    }

    [RelayCommand]
    private void RefreshComPorts()
    {
        ComPorts.Clear();
        foreach (var port in LidarTrackingService.ListPorts())
            ComPorts.Add(port);
    }

    [RelayCommand]
    private void RefreshNdiAdapters()
    {
        SelectNdiAdapter(SelectedNdiAdapter?.Id, SelectedNdiAdapter?.Ipv4, recreateSender: false);
    }

    [RelayCommand]
    private void BindNdiAdapter()
    {
        if (SelectedNdiAdapter is null)
            return;
        ApplyNdiAdapterToAllSenders(SelectedNdiAdapter);
    }

    [RelayCommand]
    private void ConnectLidar()
    {
        if (string.IsNullOrWhiteSpace(SelectedComPort))
        {
            LidarStatusText = "Kein COM-Port gewählt.";
            return;
        }

        try
        {
            ConnectLidarInternal(SelectedComPort);
        }
        catch (Exception ex)
        {
            LidarConnected = false;
            LidarStatusText = $"Verbindung fehlgeschlagen: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DisconnectLidar()
    {
        _lidar?.Disconnect();
        LidarConnected = false;
        LidarRpm = 0;
        LidarPointsPerRev = 0;
        LidarStatusText = "LiDAR getrennt";
    }

    [RelayCommand]
    private void ResetLidarBackground() => _lidar?.ResetBackground();

    [RelayCommand]
    private void TogglePlaceLidarSensor() => PlacingLidarSensor = !PlacingLidarSensor;

    private void ConnectLidarInternal(string port)
    {
        _lidar ??= new LidarTrackingService();
        var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        SyncLidarServiceSettings(floor);
        _lidar.Disconnect();
        _lidar.Connect(port);
        LidarConnected = true;
        LidarStatusText = $"Verbunden: {port}";
    }

    private void SyncLidarServiceSettings(ZoneDefinition? floor)
    {
        _lidar?.ApplySettings(BuildLidarSettings(), floor);
    }

    private LidarSettings BuildLidarSettings()
    {
        return new LidarSettings
        {
            Enabled = LidarEnabled,
            ComPort = SelectedComPort,
            AutoConnect = !string.IsNullOrEmpty(SelectedComPort),
            SensorXPx = (float)LidarSensorX,
            SensorYPx = (float)LidarSensorY,
            RotationDegrees = (float)LidarRotation,
            MirrorX = LidarMirrorX,
            PixelsPerMeter = (float)LidarPixelsPerMeter,
            FloorWidthMeters = (float)LidarFloorWidthM,
            FloorLengthMeters = (float)LidarFloorLengthM,
            MinDistanceMm = (float)LidarMinDistanceMm,
            MaxDistanceMm = (float)LidarMaxDistanceMm,
            ShowDebugPoints = LidarShowDebugPoints,
            ForegroundMarginMm = (float)LidarForegroundMarginMm,
            ClusterGapMm = (float)LidarClusterGapMm,
            MinClusterPoints = LidarMinClusterPoints,
            MinPersonWidthMm = (float)LidarMinPersonWidthMm,
            MaxPersonWidthMm = (float)LidarMaxPersonWidthMm,
            MinMovementMm = (float)LidarMinMovementMm,
            MinHits = LidarMinHits,
            HoldMs = LidarHoldMs,
            SmoothingAlpha = (float)LidarSmoothing,
            MaxJumpPx = (float)LidarMaxJumpPx,
            MinQuality = LidarMinQuality,
            BlobRadiusMeters = (float)LidarBlobRadiusM,
            BlobSoftness = (float)LidarBlobSoftness,
            BlobOpacity = (float)LidarBlobOpacity,
            BlobBlurPixels = (float)LidarBlobBlur,
            BlobColorR = (float)LidarBlobColorR,
            BlobColorG = (float)LidarBlobColorG,
            BlobColorB = (float)LidarBlobColorB,
            TrailLengthSeconds = (float)LidarTrailLength,
            TrailStrength = (float)LidarTrailStrength
        };
    }

    private void ApplyLidarSettings(LidarSettings? settings)
    {
        if (settings is null)
            return;

        LidarEnabled = settings.Enabled;
        SelectedComPort = settings.ComPort;
        LidarSensorX = settings.SensorXPx;
        LidarSensorY = settings.SensorYPx;
        LidarRotation = settings.RotationDegrees;
        LidarMirrorX = settings.MirrorX;
        LidarPixelsPerMeter = settings.PixelsPerMeter;
        LidarFloorWidthM = settings.FloorWidthMeters;
        LidarFloorLengthM = settings.FloorLengthMeters > 0.1f
            ? settings.FloorLengthMeters
            : DeriveFloorLengthMeters(settings.PixelsPerMeter);
        LidarMinDistanceMm = settings.MinDistanceMm;
        LidarMaxDistanceMm = settings.MaxDistanceMm;
        LidarShowDebugPoints = settings.ShowDebugPoints;
        LidarForegroundMarginMm = settings.ForegroundMarginMm;
        LidarClusterGapMm = settings.ClusterGapMm;
        LidarMinClusterPoints = settings.MinClusterPoints;
        LidarMinPersonWidthMm = settings.MinPersonWidthMm;
        LidarMaxPersonWidthMm = settings.MaxPersonWidthMm;
        LidarMinMovementMm = settings.MinMovementMm;
        LidarMinHits = settings.MinHits;
        LidarHoldMs = settings.HoldMs;
        LidarSmoothing = settings.SmoothingAlpha;
        LidarMaxJumpPx = settings.MaxJumpPx;
        LidarMinQuality = settings.MinQuality;
        LidarBlobRadiusM = settings.BlobRadiusMeters;
        LidarBlobSoftness = settings.BlobSoftness;
        LidarBlobOpacity = settings.BlobOpacity;
        LidarBlobBlur = settings.BlobBlurPixels;
        LidarBlobColorR = settings.BlobColorR;
        LidarBlobColorG = settings.BlobColorG;
        LidarBlobColorB = settings.BlobColorB;
        LidarTrailLength = settings.TrailLengthSeconds;
        LidarTrailStrength = settings.TrailStrength;
    }

    partial void OnLidarFloorWidthMChanged(double value)
    {
        if (_updatingLidarFloorMeters)
            return;

        var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        if (floor is null || value <= 0.1)
            return;

        _updatingLidarFloorMeters = true;
        try
        {
            LidarPixelsPerMeter = floor.Width / value;
            LidarFloorLengthM = floor.Height / LidarPixelsPerMeter;
        }
        finally
        {
            _updatingLidarFloorMeters = false;
        }
    }

    partial void OnLidarFloorLengthMChanged(double value)
    {
        if (_updatingLidarFloorMeters)
            return;

        var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        if (floor is null || value <= 0.1)
            return;

        _updatingLidarFloorMeters = true;
        try
        {
            LidarPixelsPerMeter = floor.Height / value;
            LidarFloorWidthM = floor.Width / LidarPixelsPerMeter;
        }
        finally
        {
            _updatingLidarFloorMeters = false;
        }
    }

    private bool _updatingLidarFloorMeters;

    private double DeriveFloorLengthMeters(double pixelsPerMeter)
    {
        var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        if (floor is null || pixelsPerMeter <= 0.1)
            return LidarFloorLengthM;
        return floor.Height / pixelsPerMeter;
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
            CropX = 0,
            CropY = 0,
            CropW = nativeW,
            CropH = nativeH,
            X = 100,
            Y = 100
        };
        layer.SnapDrawOpacity();

        if (SelectedZone is not null)
            layer.ApplyZone(SelectedZone, SelectedScaleMode);

        Layers.Add(layer);
        SelectedLayer = layer;
        RebuildLayerTree();
        if (runtime is BrowserVideoSource browser)
            StatusText = $"{displayName} · capture={browser.CaptureMode}";
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
            ParentGroupDrawOpacity = src.ParentGroupDrawOpacity,
            ZIndex = Layers.Count,
            NativeWidth = src.NativeWidth,
            NativeHeight = src.NativeHeight,
            CropX = src.CropX,
            CropY = src.CropY,
            CropW = src.CropW,
            CropH = src.CropH,
            BlackKeyEnabled = src.BlackKeyEnabled,
            BlackKeyThreshold = src.BlackKeyThreshold
        };
        copy.SnapDrawOpacity();

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
            Opacity = 1f,
            IsExpanded = true
        };
        group.SnapDrawOpacity();
        group.PropertyChanged += OnGroupPropertyChanged;
        Groups.Add(group);

        foreach (var layer in members)
        {
            layer.GroupId = group.Id;
            layer.ParentGroupVisible = group.Visible;
            layer.ParentGroupDrawOpacity = group.DrawOpacity;
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
            layer.ParentGroupDrawOpacity = target.DrawOpacity;
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
            layer.ParentGroupDrawOpacity = 1f;
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
        var group = WallCarousel.ResolveGroup(SelectedZone.Id);
        var zone = group is not null
            ? WallCarousel.BuildTargetZone(group.Value, Zones)
            : SelectedZone;
        SelectedLayer.ApplyZone(zone, SelectedScaleMode);
        OnPropertyChanged(nameof(SelectedLayer));
    }

    [RelayCommand]
    private void ResetSelectedLayerCrop()
    {
        if (SelectedLayer is null) return;
        SelectedLayer.ResetCrop();
        RefitLayerIfZoned(SelectedLayer);
    }

    private void RefitLayerIfZoned(CompositionLayer? layer)
    {
        if (layer?.ZoneId is null || CarouselBusy)
            return;

        var group = WallCarousel.ResolveGroup(layer.ZoneId);
        ZoneDefinition? zone = group is not null
            ? WallCarousel.BuildTargetZone(group.Value, Zones)
            : PixelMapLoader.FindZone(_pixelMap, layer.ZoneId);
        if (zone is not null)
            layer.ApplyZone(zone, layer.ScaleMode, applyZoneRotation: false);
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
            _wallAnimDuration = Math.Clamp(CarouselDurationSeconds, 0.3, 5.0);
            _wallRoundStart = DateTime.UtcNow;
            ApplyFpsStatusText();
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
        _roundWest = null;
        _roundOst = null;
        _roundSud = null;
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

        var westLayer = WallCarousel.FindLayerForGroup(WallGroup.West, zoneList, layerList);
        var ostLayer = WallCarousel.FindLayerForGroup(WallGroup.Ost, zoneList, layerList);
        var sudLayer = WallCarousel.FindLayerForGroup(WallGroup.Sud, zoneList, layerList);
        if (westLayer is null || ostLayer is null || sudLayer is null ||
            westLayer == ostLayer || ostLayer == sudLayer || sudLayer == westLayer)
        {
            AbortWallCarousel("Wand-Runde konnte nicht gestartet werden.");
            return;
        }

        _roundWest = westLayer;
        _roundOst = ostLayer;
        _roundSud = sudLayer;
        var cycle = WallCarousel.BuildSimultaneousCycle(
            zoneList, westLayer, ostLayer, sudLayer, _wallRoundDirection);

        lock (_renderLock)
            ArmCycleLocked(cycle);

        Reindex();
        _wallAnimDuration = Math.Clamp(CarouselDurationSeconds, 0.3, 5.0);
        _wallAnimStart = DateTime.UtcNow;
        _wallFinishQueued = false;
        ApplyFpsStatusText();
    }

    private void ArmCycleLocked(WallSimultaneousCycle cycle)
    {
        _wallTracks.Clear();
        _wallTracks.AddRange(cycle.Tracks);
        _wallCommits = cycle.Commits;

        foreach (var commit in cycle.Commits)
            commit.Original.SetOpacityImmediate(0f);

        InsertMovingCopiesAboveSources(cycle.MovingCopies);
    }

    private void RotateRoundOccupants()
    {
        if (_roundWest is null || _roundOst is null || _roundSud is null)
            return;

        if (_wallRoundDirection == WallCarouselDirection.WestToOst)
            (_roundWest, _roundOst, _roundSud) = (_roundSud, _roundWest, _roundOst);
        else
            (_roundWest, _roundOst, _roundSud) = (_roundOst, _roundSud, _roundWest);
    }

    private void AdvanceWallCarouselLocked()
    {
        if (!CarouselBusy || _wallTracks.Count == 0)
            return;

        double t = Math.Min(1.0, (DateTime.UtcNow - _wallAnimStart).TotalSeconds / _wallAnimDuration);
        float u = (float)t;

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
            bool continueRound;
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
                    o.Visible = true;
                    o.SetOpacityImmediate(0f);
                }

                RemoveMovingCopies();
                _wallTracks.Clear();
                _wallCommits = [];
                _wallCycleIndex++;

                continueRound = _wallCycleIndex < WallCyclesPerRound;
                if (continueRound)
                {
                    RotateRoundOccupants();
                    if (_roundWest is null || _roundOst is null || _roundSud is null)
                    {
                        RestoreWallOpacitiesLocked();
                        continueRound = false;
                    }
                    else
                    {
                        var cycle = WallCarousel.BuildSimultaneousCycle(
                            Zones.ToList(), _roundWest, _roundOst, _roundSud, _wallRoundDirection);
                        ArmCycleLocked(cycle);
                        _wallAnimDuration = Math.Clamp(CarouselDurationSeconds, 0.3, 5.0);
                        _wallAnimStart = DateTime.UtcNow;
                    }
                }
                else
                {
                    RestoreWallOpacitiesLocked();
                }
            }

            Reindex();
            if (continueRound)
            {
                ApplyFpsStatusText();
                return;
            }

            RebuildLayerTree();
            CarouselBusy = false;
            ApplyFpsStatusText();
        }
        catch (Exception ex)
        {
            AbortWallCarousel($"Wand-Runde abgebrochen: {ex.Message}");
            MessageBox.Show(ex.ToString(), "Wand-Karussell");
        }
    }

    private void RestoreWallOpacitiesLocked()
    {
        foreach (var layer in Layers)
        {
            if (!WallCarousel.IsMovingCopy(layer) &&
                WallCarousel.ResolveGroup(layer.ZoneId) is not null &&
                layer.Opacity <= 0.01f)
            {
                layer.SetOpacityImmediate(1f);
            }
        }
    }

    private void RemoveMovingCopies()
    {
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            if (WallCarousel.IsMovingCopy(Layers[i]))
                Layers.RemoveAt(i);
        }
    }

    private void CleanupMovingCopies()
    {
        RemoveMovingCopies();
        RestoreWallOpacitiesLocked();
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
            FadeDurationSeconds,
            CarouselDirection,
            FloorReflectionEnabled,
            (float)FloorReflectionOpacity,
            (float)FloorReflectionBlur,
            (float)FloorReflectionLength,
            (float)FloorReflectionAngle,
            (float)FloorReflectionFadeStart,
            BuildLidarSettings(),
            SelectedNdiAdapter?.Id,
            SelectedNdiAdapter?.Ipv4,
            NdiZoneStreams.Where(s => s.Enabled).Select(s => s.ZoneId));
    }

    private void ApplySettings(LayoutDocument doc)
    {
        SelectNdiAdapter(doc.NdiAdapterId, doc.NdiAdapterIp, recreateSender: _gpu is not null);
        OutputName = doc.OutputName;
        ShowBackgroundInOutput = doc.ShowBackgroundInOutput;
        ShowBackgroundInPreview = doc.ShowBackgroundInPreview;
        ShowLayerOverlays = doc.ShowLayerOverlays;
        NdiSending = doc.NdiSending;
        SelectedScaleMode = doc.SelectedScaleMode;
        CarouselDurationSeconds = Math.Clamp(doc.CarouselDurationSeconds, 0.3, 5.0);
        FadeDurationSeconds = Math.Clamp(doc.FadeDurationSeconds, 0.0, 10.0);
        CarouselDirection = doc.CarouselDirection;
        ApplyFloorReflectionSettings(doc);
        ApplyLidarSettings(doc.Lidar);
        ApplyEnabledNdiZones(doc.EnabledNdiZoneIds);
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
                Opacity = Math.Clamp(ge.Opacity ?? 1f, 0f, 1f),
                IsExpanded = ge.IsExpanded,
                SortOrder = ge.SortOrder
            };
            group.SnapDrawOpacity();
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
            float parentDraw = 1f;
            if (groupId is Guid gid)
            {
                var group = Groups.FirstOrDefault(g => g.Id == gid);
                if (group is null)
                    groupId = null;
                else
                {
                    parentVisible = group.Visible;
                    parentDraw = group.DrawOpacity;
                }
            }

            var layer = new CompositionLayer
            {
                Name = e.Name,
                SourceKind = e.SourceKind,
                SourceKey = e.SourceKey,
                X = e.X,
                Y = e.Y,
                Scale = e.Scale,
                RotationDegrees = e.RotationDegrees,
                Opacity = Math.Clamp(e.Opacity, 0f, 1f),
                ScaleMode = e.ScaleMode,
                ZoneId = e.ZoneId,
                GroupId = groupId,
                ParentGroupVisible = parentVisible,
                ParentGroupDrawOpacity = parentDraw,
                Visible = e.Visible,
                ZIndex = e.ZIndex,
                NativeWidth = nativeW,
                NativeHeight = nativeH,
                CropX = e.CropX,
                CropY = e.CropY,
                CropW = e.CropW,
                CropH = e.CropH,
                BlackKeyEnabled = e.BlackKeyEnabled,
                BlackKeyThreshold = e.BlackKeyThreshold > 0 ? e.BlackKeyThreshold : 0.08f
            };
            layer.SyncCropToNativeSize(e.NativeWidth, e.NativeHeight);
            layer.SnapDrawOpacity();
            Layers.Add(layer);
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
        {
            layer.ParentGroupVisible = group.Visible;
            layer.ParentGroupDrawOpacity = group.DrawOpacity;
        }
    }

    private void TickFades(float dt)
    {
        double dur = FadeDurationSeconds;
        if (double.IsNaN(dur) || dur < 0) dur = 0;
        else if (dur > 10) dur = 10;
        float duration = (float)dur;

        var groups = Groups;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            float before = group.DrawOpacity;
            bool animating = group.TickFade(dt, duration);
            if (!animating && group.DrawOpacity == before)
                continue;

            // Push group fade to members only while the group opacity is moving.
            float draw = group.DrawOpacity;
            Guid id = group.Id;
            var layers = Layers;
            for (int li = 0; li < layers.Count; li++)
            {
                var layer = layers[li];
                if (layer.GroupId == id)
                    layer.ParentGroupDrawOpacity = draw;
            }
        }

        var allLayers = Layers;
        for (int i = 0; i < allLayers.Count; i++)
        {
            var layer = allLayers[i];
            if (layer.GroupId is null)
                layer.ParentGroupDrawOpacity = 1f;
            layer.TickFade(dt, duration);
        }
    }

    private float ComputeFadeDt()
    {
        long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt;
        if (_lastFadeQpc == 0)
            dt = 1f / 60f;
        else
            dt = (float)((qpc - _lastFadeQpc) / (double)System.Diagnostics.Stopwatch.Frequency);
        _lastFadeQpc = qpc;
        return Math.Clamp(dt, 0f, 0.1f);
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
        if (value is { IsGroup: true, Group: not null })
        {
            SelectedGroup = value.Group;
            return;
        }

        if (value is { IsGroup: false, Layer: not null })
        {
            if (!ReferenceEquals(SelectedLayer, value.Layer))
                SelectedLayer = value.Layer;
            SelectedGroup = value.Layer.GroupId is Guid gid
                ? Groups.FirstOrDefault(g => g.Id == gid)
                : null;
        }
        else
        {
            SelectedGroup = null;
        }
    }

    partial void OnSelectedGroupChanged(LayerGroup? value) =>
        OnPropertyChanged(nameof(HasSelectedGroup));

    public bool HasSelectedGroup => SelectedGroup is not null;

    partial void OnSelectedLayerChanged(CompositionLayer? value)
    {
        OnPropertyChanged(nameof(HasSelectedLayer));

        if (_hudLayer is not null)
            _hudLayer.PropertyChanged -= OnSelectedLayerPropertyChanged;
        _hudLayer = value;
        if (_hudLayer is not null)
            _hudLayer.PropertyChanged += OnSelectedLayerPropertyChanged;

        UpdateSelectedSourceHud();
        SyncSelectedTreeNode();
        SyncGroupChoiceFromLayer();
        if (value?.GroupId is Guid gid)
            SelectedGroup = Groups.FirstOrDefault(g => g.Id == gid);
        else if (SelectedTreeNode is not { IsGroup: true })
            SelectedGroup = null;
    }

    public bool HasSelectedLayer => SelectedLayer is not null;

    private CompositionLayer? _hudLayer;
    private bool _suppressGroupChoiceAssign;

    private void OnSelectedLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompositionLayer.Name)
            or nameof(CompositionLayer.NativeWidth)
            or nameof(CompositionLayer.NativeHeight)
            or nameof(CompositionLayer.CropX)
            or nameof(CompositionLayer.CropY)
            or nameof(CompositionLayer.CropW)
            or nameof(CompositionLayer.CropH))
        {
            UpdateSelectedSourceHud();
        }

        if (e.PropertyName is nameof(CompositionLayer.CropX)
            or nameof(CompositionLayer.CropY)
            or nameof(CompositionLayer.CropW)
            or nameof(CompositionLayer.CropH))
        {
            RefitLayerIfZoned(SelectedLayer);
        }
    }

    private void UpdateSelectedSourceHud()
    {
        if (SelectedLayer is null)
        {
            SelectedSourceHud = "";
            return;
        }

        var (cw, ch) = SelectedLayer.GetSourcePixelSize();
        SelectedSourceHud = SelectedLayer.HasCrop
            ? $"{SelectedLayer.Name}\n{SelectedLayer.NativeWidth}×{SelectedLayer.NativeHeight} → {cw}×{ch}"
            : $"{SelectedLayer.Name}\n{SelectedLayer.NativeWidth}×{SelectedLayer.NativeHeight}";
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
            SelectedLayer.ParentGroupDrawOpacity = group?.DrawOpacity ?? 1f;
            SelectedGroup = group;
        }
        else
        {
            SelectedLayer.ParentGroupVisible = true;
            SelectedLayer.ParentGroupDrawOpacity = 1f;
            SelectedGroup = null;
        }

        RebuildLayerTree();
    }

    private void UiTick()
    {
        TickWallCarousel();

        // Apply status on the UI thread without BeginInvoke storms from the render loop.
        var status = _pendingStatus;
        if (status is not null)
        {
            _pendingStatus = null;
            if (status == "fps")
                ApplyFpsStatusText();
            else if (!CarouselBusy)
                StatusText = status;
        }

        PresentPreview();

        if (++_nicProbeTick >= 15)
        {
            _nicProbeTick = 0;
            var sending = NdiSendPathProbe.Describe(CountNdiViewers(), NdiAdapterBinding.CurrentAllowedIpv4s);
            if (sending != NdiSendingNicText)
                NdiSendingNicText = sending;
        }

        Fps = _renderFps;

        var lidarStatus = _pendingLidarStatus;
        if (lidarStatus is not null)
        {
            _pendingLidarStatus = null;
            LidarStatusText = lidarStatus;
            LidarConnected = _pendingLidarConnected;
            LidarRpm = _pendingLidarRpm;
            LidarPointsPerRev = _pendingLidarPointsPerRev;
        }
    }

    private int CountNdiViewers()
    {
        var n = 0;
        if (_ndiOut is { IsActive: true })
            n += _ndiOut.ConnectionCount;
        foreach (var item in NdiZoneStreams)
        {
            if (item.Enabled && item.Stream.Sender is { IsActive: true } sender)
                n += sender.ConnectionCount;
        }
        return n;
    }

    private void ApplyFpsStatusText()
    {
        string head = CarouselBusy
            ? $"Karussell {_wallCycleIndex + 1}/{WallCyclesPerRound} | Compose {_renderFps:0.0} | Preview {_previewFps:0.0} | Layers={Layers.Count}"
            : $"Compose {_renderFps:0.0} | Preview {_previewFps:0.0} | Layers={Layers.Count}";
        StatusText = head + Environment.NewLine + FormatActiveNdiStatus();
    }

    private string FormatActiveNdiStatus()
    {
        var lines = new List<string>();
        double totalBw = 0;
        int totalViewers = 0;

        void AddSender(string label, NdiOutputSender sender)
        {
            var fps = sender.SendFps > 0 ? $"{sender.SendFps:0.0}fps" : "—";
            var bw = sender.SendBytesPerSecond > 0
                ? NdiFrameSpec.FormatBandwidthGb(sender.SendBytesPerSecond)
                : "—";
            var mb = sender.BufferSizeBytes > 0
                ? $"{sender.BufferSizeBytes / (1024.0 * 1024.0):0}MB"
                : "—";
            lines.Add(
                $"  {label} {sender.OutputWidth}×{sender.OutputHeight} {sender.PixelFormat} {fps} {bw} {mb}/f viewers={sender.ConnectionCount}");
            totalBw += sender.SendBytesPerSecond;
            totalViewers += sender.ConnectionCount;
        }

        if (_ndiOut is { IsActive: true })
            AddSender("Full", _ndiOut);

        foreach (var item in NdiZoneStreams)
        {
            if (!item.Enabled || item.Stream.Sender is not { IsActive: true } sender)
                continue;
            AddSender(item.Stream.NameSuffix, sender);
        }

        if (lines.Count == 0)
            return "NDI off";

        var nic = SelectedNdiAdapter is null || SelectedNdiAdapter.IsAutomatic
            ? "NIC auto"
            : SelectedNdiAdapter.DisplayName;
        var header = totalBw > 0
            ? $"NDI×{lines.Count} {nic} total {NdiFrameSpec.FormatBandwidthGb(totalBw)} viewers={totalViewers}"
            : $"NDI×{lines.Count} {nic} viewers={totalViewers}";
        return header + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private void OnPreviewFrameReady()
    {
        if (_disposed || _renderExit)
            return;
        if (Interlocked.Exchange(ref _previewPresentQueued, 1) == 1)
            return;
        var disp = Application.Current?.Dispatcher;
        if (disp is null)
        {
            Interlocked.Exchange(ref _previewPresentQueued, 0);
            return;
        }
        disp.BeginInvoke(DispatcherPriority.Render, PresentPreviewFromPack);
    }

    private void PresentPreviewFromPack()
    {
        Interlocked.Exchange(ref _previewPresentQueued, 0);
        PresentPreview();
    }

    private void PresentPreview()
    {
        if (_previewBitmap is null || _compositor is null || _gpu is null || _renderExit)
            return;

        int gen = _compositor.PreviewGeneration;
        if (gen == _lastPreviewGen || !_compositor.TryGetPublishedPreview(out var pixels))
            return;

        int w = _compositor.PreviewWidth;
        int h = _compositor.PreviewHeight;
        _previewBitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        _lastPreviewGen = gen;
    }

    private void RenderLoop()
    {
        // Match NDI SpeedHQ: compose and send both locked to NdiFrameSpec.TargetFps.
        // Preview stays ≤30 Hz.
        const double targetFrameMs = 1000.0 / NdiFrameSpec.TargetFps;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double nextMs = 0;

        bool periodRaised = timeBeginPeriod(1) == 0;
        try
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

                nextMs += targetFrameMs;
                double nowMs = watch.Elapsed.TotalMilliseconds;
                // Missed the slot: skip catch-up so we do not burst into the receiver.
                if (nowMs > nextMs + targetFrameMs)
                    nextMs = nowMs;
                else
                {
                    double remaining = nextMs - nowMs;
                    if (remaining >= 1.5)
                        Thread.Sleep((int)(remaining - 0.5));
                    while (!_renderExit && watch.Elapsed.TotalMilliseconds < nextMs)
                        Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            if (periodRaised)
                timeEndPeriod(1);
        }
    }

    private void RenderFrame()
    {
        if (_renderExit || _compositor is null || _gpu is null)
            return;

        try
        {
            _hub.UpdateAll();

            long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            double sincePreviewMs = _lastPreviewEmitQpc == 0
                ? 1e9
                : (qpc - _lastPreviewEmitQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            bool wantPreview = sincePreviewMs >= (1000.0 / 30.0);

            FloorBlobFrame blobFrame = FloorBlobFrame.Empty;
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
                        int prevW = layer.NativeWidth;
                        int prevH = layer.NativeHeight;
                        layer.NativeWidth = src.Width;
                        layer.NativeHeight = src.Height;
                        layer.SyncCropToNativeSize(prevW, prevH);
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
                TickFades(ComputeFadeDt());

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

                var floor = Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
                SyncLidarServiceSettings(floor);
                FloorBlobSettings? blobSettings = null;
                if (LidarEnabled)
                {
                    blobFrame = _lidar?.UpdateFrame() ?? FloorBlobFrame.Empty;
                    blobSettings = BuildLidarSettings().ToBlobSettings();
                }
                else
                {
                    // Drop blobs from compose/preview immediately; tracker also clears.
                    _lidar?.UpdateFrame();
                    blobFrame = FloorBlobFrame.Empty;
                }

                FloorWaveSettings wave = FloorWaveSettings.Disabled;
                if (CarouselBusy && _wallAnimDuration > 0.001)
                {
                    float roundSeconds = (float)(_wallAnimDuration * WallCyclesPerRound);
                    float roundT = (float)Math.Clamp(
                        (DateTime.UtcNow - _wallRoundStart).TotalSeconds / roundSeconds, 0.0, 1.0);
                    wave = new FloorWaveSettings
                    {
                        Enabled = true,
                        Progress = roundT,
                        Amplitude = 1f,
                        Wavelength = 0.20f,
                        Opacity = 1f
                    };
                }

                if (_lidar is not null)
                {
                    var status = _lidar.Status;
                    _pendingLidarConnected = status.IsConnected;
                    _pendingLidarRpm = (float)status.Rpm;
                    _pendingLidarPointsPerRev = status.PointsPerRevolution;
                    if (status.IsConnected)
                    {
                        var err = string.IsNullOrEmpty(status.LastError) ? "" : $" | {status.LastError}";
                        _pendingLidarStatus =
                            $"LiDAR {status.ComPort} | 0x{status.ScanDataType:X2} | {status.Rpm:0.#} RPM | {status.PointsPerRevolution} pts | {status.Health}{err}";
                    }
                }

                // 1) Output canvas (compose cadence matches NDI TargetFps).
                lock (_gpu.ContextLock)
                {
                    _compositor.Render(
                        Layers, Resolve,
                        ShowBackgroundInPreview, ShowBackgroundInOutput,
                        Zones, reflection, blobFrame, blobSettings, wave);
                }
            }

            // 2) NDI — full-frame and zones share the same NdiOutputSender profile.
            if (!_renderExit)
            {
                if (NdiSending && _ndiOut is { IsActive: true })
                {
                    lock (_gpu.ContextLock)
                        _ndiOut.SendFrame(_compositor.CanvasTexture);
                }
                SendEnabledZoneStreams(_compositor.CanvasTexture);
            }

            // 3) Preview after output, capped at 30 Hz.
            if (!_renderExit && wantPreview)
            {
                _lastPreviewEmitQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gpu.ContextLock)
                    _compositor.EmitPreview(ShowBackgroundInPreview, LidarEnabled ? blobFrame : null);
                _previewEmitCounter++;
            }

            _frameCounter++;
            var now = DateTime.UtcNow;
            var dt = (now - _lastFpsTime).TotalSeconds;
            if (dt >= 1.0)
            {
                _renderFps = _frameCounter / dt;
                _previewFps = _previewEmitCounter / dt;
                _frameCounter = 0;
                _previewEmitCounter = 0;
                _lastFpsTime = now;
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
        foreach (var item in NdiZoneStreams)
            item.Stream.BeginShutdown();
    }

    public void OnPreviewMouseDown(Point canvasPixel, bool startDrag)
    {
        if (PlacingLidarSensor)
        {
            LidarSensorX = canvasPixel.X;
            LidarSensorY = canvasPixel.Y;
            PlacingLidarSensor = false;
            SyncLidarServiceSettings(Zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        CompositionLayer? hit = null;
        foreach (var layer in Layers.OrderByDescending(l => l.ZIndex))
        {
            if (!layer.IsLogicallyVisible) continue;
            if (WallCarousel.LayerContainsMapPoint(layer, Zones, canvasPixel.X, canvasPixel.Y))
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
        RecreateNdiSender(value);
    }

    partial void OnNdiSendingChanged(bool value)
    {
        if (_gpu is null || _pixelMap.CanvasWidth <= 0 || _disposed || _suppressNdiStreamRecreate)
            return;

        if (!value)
            StopFullFrameNdiSender();
        else
            EnsureFullFrameNdiSender();
    }

    public void CommitNdiAdapterFromUi(NdiAdapterChoice? choice)
    {
        if (!_ndiAdapterReady || choice is null)
            return;

        if (SelectedNdiAdapter is not null && SelectedNdiAdapter.Equals(choice))
            return;

        SelectedNdiAdapter = choice;
    }

    partial void OnSelectedNdiAdapterChanged(NdiAdapterChoice? value)
    {
        if (!_ndiAdapterReady || value is null)
            return;

        ApplyNdiAdapterToAllSenders(value);
    }

    private void ApplyNdiAdapterToAllSenders(NdiAdapterChoice value)
    {
        NdiAdapterBinding.Apply(value);
        try { SaveSession(); } catch { /* persist NIC so next launch matches */ }
        RecreateNdiSender(OutputName);
        if (!NdiAdapterBinding.LastAccessManagerWriteOk)
        {
            StatusText = "NDI-Adapter: Config im Programmordner nicht geschrieben.";
            return;
        }

        StatusText = value.IsAutomatic
            ? "NDI-Config (Programm\\NDI): automatisch — alle Karten"
            : $"NDI-Config (Programm\\NDI): {value.DisplayName}";
    }

    private void SelectNdiAdapter(string? id, string? ipv4, bool recreateSender)
    {
        _ndiAdapterReady = false;
        NdiAdapters.Clear();
        foreach (var adapter in NdiAdapterBinding.ListAdapters())
            NdiAdapters.Add(adapter);

        SelectedNdiAdapter = NdiAdapterBinding.Resolve(id, ipv4, NdiAdapters);
        NdiAdapterBinding.Apply(SelectedNdiAdapter);
        _ndiAdapterReady = true;

        if (!NdiAdapterBinding.LastAccessManagerWriteOk)
            StatusText = "NDI-Adapter: Config im Programmordner nicht geschrieben.";
        else if (SelectedNdiAdapter.IsAutomatic)
            StatusText = "NDI-Config (Programm\\NDI): automatisch — alle Karten";
        else
            StatusText = $"NDI-Config (Programm\\NDI): {SelectedNdiAdapter.DisplayName}";

        if (recreateSender && _gpu is not null)
            RecreateNdiSender(OutputName);
    }

    private void RecreateNdiSender(string name)
    {
        if (_gpu is null || _pixelMap.CanvasWidth <= 0)
            return;

        SyncNdiEncodeBudget();

        if (NdiSending)
            EnsureFullFrameNdiSender(name);
        else
            StopFullFrameNdiSender();

        RecreateEnabledZoneStreams();
    }

    private void SyncNdiEncodeBudget()
    {
        long pixels = 0;
        foreach (var item in NdiZoneStreams)
        {
            if (!item.Enabled)
                continue;
            pixels += (long)item.Stream.CropWidth * item.Stream.CropHeight;
        }

        if (pixels > 0)
            NdiAdapterBinding.SetZoneBudgetPixels(pixels);
    }

    private void EnsureFullFrameNdiSender(string? name = null)
    {
        if (_gpu is null || _pixelMap.CanvasWidth <= 0)
            return;

        var ndiName = name ?? OutputName;
        StopFullFrameNdiSender();
        try
        {
            _ndiOut = NdiOutputSender.CreateFullFrame(
                _gpu, ndiName, _pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
            _ndiOut.Initialize(_pixelMap.CanvasWidth, _pixelMap.CanvasHeight);
        }
        catch (Exception ex)
        {
            StopFullFrameNdiSender();
            NdiSending = false;
            StatusText = $"NDI init failed: {ex.Message}";
        }
    }

    private void StopFullFrameNdiSender()
    {
        _ndiOut?.BeginShutdown();
        _ndiOut?.Dispose();
        _ndiOut = null;
    }

    private void RestartNdiStack()
    {
        StopAllZoneStreams();
        StopFullFrameNdiSender();
        _ndiCatalog?.Dispose();
        _ndiCatalog = null;

        try
        {
            if (!NdiBootstrap.Reinitialize())
                throw new InvalidOperationException("NDI library failed to initialize.");

            if (_gpu is null || _pixelMap.CanvasWidth <= 0)
                return;

            _hub.RestartNdiSources();
            SyncNdiEncodeBudget();
            if (NdiSending)
                EnsureFullFrameNdiSender();
            RecreateEnabledZoneStreams();
            try { _ndiCatalog = new NdiSourceCatalog(); }
            catch { /* finder optional */ }
            RefreshSources();
        }
        catch (Exception ex)
        {
            NdiSending = false;
            StopFullFrameNdiSender();
            StopAllZoneStreams();
            StatusText = $"NDI init failed: {ex.Message}";
        }
    }

    private void InitNdiZoneStreams(IReadOnlyList<string>? enabledIds)
    {
        if (_gpu is null)
            return;

        foreach (var item in NdiZoneStreams)
            item.Stream.Dispose();
        NdiZoneStreams.Clear();

        var enabled = new HashSet<string>(
            enabledIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        _suppressNdiStreamRecreate = true;
        try
        {
            foreach (var (zoneId, suffix, displayName) in NdiZoneStream.Catalog)
            {
                var stream = new NdiZoneStream(_gpu, zoneId, suffix, displayName);
                var zone = Zones.FirstOrDefault(z => z.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase));
                if (zone is null || !stream.TryBindZone(zone))
                    continue;

                var item = new NdiZoneStreamItem(stream, OnNdiZoneEnabledChanged);
                item.RefreshLabel();
                NdiZoneStreams.Add(item);
                if (enabled.Contains(zoneId))
                    item.Enabled = true;
            }
        }
        finally
        {
            _suppressNdiStreamRecreate = false;
        }
    }

    private void ApplyEnabledNdiZones(IReadOnlyList<string>? enabledIds)
    {
        if (NdiZoneStreams.Count == 0)
            return;

        var enabled = new HashSet<string>(
            enabledIds ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        _suppressNdiStreamRecreate = true;
        try
        {
            foreach (var item in NdiZoneStreams)
                item.Enabled = enabled.Contains(item.ZoneId);
        }
        finally
        {
            _suppressNdiStreamRecreate = false;
        }

        RecreateEnabledZoneStreams();
    }

    private void OnNdiZoneEnabledChanged(NdiZoneStreamItem item, bool enabled)
    {
        if (_gpu is null || _disposed || _suppressNdiStreamRecreate)
            return;

        RecreateEnabledZoneStreams();
    }

    private void RecreateEnabledZoneStreams()
    {
        if (_gpu is null)
            return;

        SyncNdiEncodeBudget();

        lock (_gpu.ContextLock)
        {
            foreach (var item in NdiZoneStreams)
            {
                item.Stream.Stop();
                if (!item.Enabled)
                    continue;

                try
                {
                    item.Stream.EnsureStarted(OutputName);
                }
                catch (Exception ex)
                {
                    item.Enabled = false;
                    StatusText = $"Zonen-NDI {item.Stream.DisplayName}: {ex.Message}";
                }
            }
        }
    }

    private void StopAllZoneStreams()
    {
        if (_gpu is null)
        {
            foreach (var item in NdiZoneStreams)
                item.Stream.Stop();
            return;
        }

        lock (_gpu.ContextLock)
        {
            foreach (var item in NdiZoneStreams)
                item.Stream.Stop();
        }
    }

    private void SendEnabledZoneStreams(ID3D11Texture2D canvas)
    {
        if (_gpu is null || NdiZoneStreams.Count == 0)
            return;

        lock (_gpu.ContextLock)
        {
            foreach (var item in NdiZoneStreams)
            {
                if (!item.Enabled || !item.Stream.IsActive)
                    continue;
                item.Stream.SendFromCanvas(canvas, CanvasWidth, CanvasHeight);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        RequestShutdown();
        _renderThread?.Join(TimeSpan.FromSeconds(5));
        if (_compositor is not null)
            _compositor.PreviewFrameReady -= OnPreviewFrameReady;
        _lidar?.Dispose();
        foreach (var item in NdiZoneStreams)
            item.Stream.Dispose();
        NdiZoneStreams.Clear();
        _ndiOut?.Dispose();
        _compositor?.Dispose();
        _hub.Dispose();
        _ndiCatalog?.Dispose();
        _spoutCatalog?.Dispose();
        _gpu?.Dispose();
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
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
