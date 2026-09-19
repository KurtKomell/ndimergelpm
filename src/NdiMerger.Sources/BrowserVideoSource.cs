using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinForms = System.Windows.Forms;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace NdiMerger.Sources;

/// <summary>
/// Fast browser source: WebView2 on Intel when available; WGC capture on the main
/// (NVIDIA) adapter via DXGI shared textures — GPU path only, no JPEG.
/// </summary>
public sealed class BrowserVideoSource : IVideoSource
{
    private readonly BrowserSourceSpec _spec;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _disposedEvent = new(false);
    private readonly object _gpuPublishLock = new();

    private Thread? _staThread;
    private WinForms.Form? _hostForm;
    private WebView2? _webView;
    private GpuDevice? _mainGpu;

    private ID3D11Device? _capDevice;
    private ID3D11DeviceContext? _capContext;
    private IDirect3DDevice? _winrtDevice;
    private readonly ID3D11Texture2D?[] _capShared = new ID3D11Texture2D?[2];
    private readonly ID3D11Texture2D?[] _mainTextures = new ID3D11Texture2D?[2];
    private readonly ID3D11ShaderResourceView?[] _srvs = new ID3D11ShaderResourceView?[2];
    private IntPtr[] _sharedHandles = [IntPtr.Zero, IntPtr.Zero];

    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private volatile bool _useWgc;
    private BrowserGpuSelector.Selection _browserGpu;
    private string _webViewGpuArgs = string.Empty;
    private Direct3D11CaptureFrame? _heldFrame0;
    private Direct3D11CaptureFrame? _heldFrame1;

    private int _displayIndex;
    private int _readyIndex;
    private bool _hasGpuReady;
    private bool _newFrame;

    private volatile bool _connected;
    private volatile bool _exit;
    private volatile bool _disposed;
    private bool _timerPeriodRaised;
    private string? _initError;
    private string _captureMode = "init";

    public BrowserVideoSource(string key, string? displayName = null)
    {
        _spec = BrowserSourceSpec.Parse(key);
        Key = _spec.Key;
        DisplayName = displayName ?? _spec.DisplayName;
        Width = _spec.Width;
        Height = _spec.Height;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public SourceKind Kind => SourceKind.Browser;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsConnected => _connected;
    public bool HasNewFrame => _newFrame;
    public string CaptureMode => _captureMode;

    public void Start(GpuDevice gpu)
    {
        _mainGpu = gpu;
        _exit = false;
        CreateCaptureDevice(gpu);

        _staThread = new Thread(StaMain)
        {
            IsBackground = true,
            Name = $"Browser-{_spec.Id}",
            Priority = ThreadPriority.Highest
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        if (!_ready.Wait(25000))
        {
            var msg = _initError ?? "WebView2 did not become ready in time.";
            SignalClose();
            throw new InvalidOperationException(msg);
        }

        if (_initError is not null)
        {
            var err = _initError;
            SignalClose();
            throw new InvalidOperationException(err);
        }
    }

    public void Update()
    {
        _newFrame = false;
        lock (_gpuPublishLock)
        {
            if (!_hasGpuReady) return;
            _displayIndex = _readyIndex;
            _hasGpuReady = false;
            Width = _spec.Width;
            Height = _spec.Height;
            _newFrame = true;
        }
    }

    public ID3D11ShaderResourceView? GetSrv()
    {
        lock (_gpuPublishLock)
            return _srvs[_displayIndex];
    }

    private void CreateCaptureDevice(GpuDevice main)
    {
        using var mainDxgi = main.Device.QueryInterface<IDXGIDevice>();
        using var mainAdapter = mainDxgi.GetAdapter().QueryInterface<IDXGIAdapter1>();

        // Pin Chromium/WebView2 to Intel so decode stays off the NVIDIA compositor GPU.
        using var intelAdapter = BrowserGpuSelector.TryAcquireBrowserAdapter(out _browserGpu);
        if (intelAdapter is not null)
        {
            _webViewGpuArgs = BrowserGpuSelector.BuildWebViewGpuArguments(_browserGpu);
        }
        else
        {
            _browserGpu = new BrowserGpuSelector.Selection
            {
                Name = main.AdapterName,
                VendorId = main.VendorId,
                DeviceId = mainAdapter.Description1.DeviceId,
                Luid = mainAdapter.Description1.Luid,
                IsIntel = main.VendorId == BrowserGpuSelector.VendorIntel
            };
            _webViewGpuArgs = string.Empty;
        }

        var hr = D3D11.D3D11CreateDevice(
            mainAdapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device,
            out _,
            out var context);

        if (hr.Failure || device is null || context is null)
            throw new InvalidOperationException($"Browser capture D3D device failed: {hr}");

        _capDevice = device;
        _capContext = context;
        var mt = device.QueryInterfaceOrNull<ID3D11Multithread>();
        mt?.SetMultithreadProtected(true);
        mt?.Dispose();
    }

    private void EnsureTextureSlots(int w, int h)
    {
        if (_capDevice is null || _mainGpu is null) return;

        for (int slot = 0; slot < 2; slot++)
        {
            var existing = _capShared[slot];
            if (existing is not null)
            {
                var d = existing.Description;
                if (d.Width == (uint)w && d.Height == (uint)h)
                    continue;
            }

            DisposeSlot(slot);

            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared
            };

            _capShared[slot] = _capDevice.CreateTexture2D(desc);
            using (var res = _capShared[slot]!.QueryInterface<IDXGIResource>())
                _sharedHandles[slot] = res.SharedHandle;

            lock (_mainGpu.ContextLock)
            {
                _mainTextures[slot] = _mainGpu.Device.OpenSharedResource<ID3D11Texture2D>(_sharedHandles[slot]);
                _srvs[slot] = _mainGpu.Device.CreateShaderResourceView(_mainTextures[slot]);
            }
        }
    }

    private void DisposeSlot(int slot)
    {
        _srvs[slot]?.Dispose();
        _srvs[slot] = null;
        _mainTextures[slot]?.Dispose();
        _mainTextures[slot] = null;
        _capShared[slot]?.Dispose();
        _capShared[slot] = null;
        _sharedHandles[slot] = IntPtr.Zero;
    }

    private void PublishFromCaptureTexture(ID3D11Texture2D source, int srcW, int srcH)
    {
        if (_capContext is null || _exit) return;

        int copyW = Math.Min(srcW, _spec.Width);
        int copyH = Math.Min(srcH, _spec.Height);
        if (copyW <= 0 || copyH <= 0) return;

        int target;
        lock (_gpuPublishLock)
            target = 1 - _displayIndex;

        try
        {
            EnsureTextureSlots(_spec.Width, _spec.Height);
            var dest = _capShared[target];
            if (dest is null) return;

            var region = new Vortice.Mathematics.Box(0, 0, 0, copyW, copyH, 1);
            _capContext.CopySubresourceRegion(dest, 0, 0, 0, 0, source, 0, region);
            _capContext.Flush();

            lock (_gpuPublishLock)
            {
                _readyIndex = target;
                _hasGpuReady = true;
            }
        }
        catch
        {
            // drop frame
        }
    }

    private void SignalClose()
    {
        _exit = true;
        try
        {
            if (_hostForm is { IsDisposed: false })
                _hostForm.BeginInvoke(new Action(() => { try { _hostForm.Close(); } catch { } }));
        }
        catch { /* ignore */ }
    }

    private void StaMain()
    {
        try
        {
            if (timeBeginPeriod(1) == 0)
                _timerPeriodRaised = true;

            WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.DpiUnaware);
            WinForms.Application.EnableVisualStyles();

            // Fully on-screen: Chromium throttles video hard when mostly off-screen.
            var work = WinForms.Screen.PrimaryScreen?.WorkingArea
                       ?? new Rectangle(0, 0, 1920, 1080);
            int x = Math.Max(work.Left, work.Right - _spec.Width);
            int y = Math.Max(work.Top, work.Bottom - _spec.Height);

            _hostForm = new HiddenHostForm
            {
                FormBorderStyle = WinForms.FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = WinForms.FormStartPosition.Manual,
                Location = new Point(x, y),
                ClientSize = new Size(_spec.Width, _spec.Height),
                AutoScaleMode = WinForms.AutoScaleMode.None,
                Opacity = 1,
                TopMost = false,
                Text = $"NdiMerger Browser {_spec.Id}"
            };

            _webView = new WebView2
            {
                Dock = WinForms.DockStyle.Fill,
                DefaultBackgroundColor = Color.Black
            };
            _hostForm.Controls.Add(_webView);

            _hostForm.Shown += async (_, _) =>
            {
                try
                {
                    // Stay behind the app UI (not TopMost — no visible edge over the main window).
                    // Near-invisible layered + click-through keeps DWM composing for WGC.
                    _hostForm.TopMost = false;
                    MakeClickThroughNearlyInvisible(_hostForm.Handle);
                    var chromeW = _hostForm.Width - _hostForm.ClientSize.Width;
                    var chromeH = _hostForm.Height - _hostForm.ClientSize.Height;
                    SetWindowPos(_hostForm.Handle, HWND_BOTTOM, x, y,
                        _spec.Width + chromeW, _spec.Height + chromeH, SWP_NOACTIVATE);

                    var userData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "NdiMergerLPM",
                        "WebView2",
                        _spec.Id);
                    Directory.CreateDirectory(userData);

                    var gpuPin = string.IsNullOrWhiteSpace(_webViewGpuArgs)
                        ? string.Empty
                        : " " + _webViewGpuArgs;

                    var insecureOrigin = "";
                    try
                    {
                        var u = new Uri(_spec.Url);
                        if (u.Scheme == Uri.UriSchemeHttp && !string.IsNullOrEmpty(u.GetLeftPart(UriPartial.Authority)))
                            insecureOrigin = " --unsafely-treat-insecure-origin-as-secure=" + u.GetLeftPart(UriPartial.Authority);
                    }
                    catch { /* ignore */ }

                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = string.Join(' ',
                            "--disable-backgrounding-occluded-windows",
                            "--disable-renderer-backgrounding",
                            "--disable-background-timer-throttling",
                            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling",
                            "--disable-background-media-suspend",
                            "--autoplay-policy=no-user-gesture-required") + insecureOrigin + gpuPin
                    };

                    var env = await CoreWebView2Environment.CreateAsync(null, userData, options);
                    await _webView.EnsureCoreWebView2Async(env);
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    _webView.CoreWebView2.IsMuted = false;

                    _webView.CoreWebView2.PermissionRequested += (_, e) =>
                    {
                        if (e.PermissionKind is CoreWebView2PermissionKind.Microphone
                            or CoreWebView2PermissionKind.Autoplay)
                            e.State = CoreWebView2PermissionState.Allow;
                    };

                    try
                    {
                        var origin = new Uri(_spec.Url).GetLeftPart(UriPartial.Authority);
                        if (!string.IsNullOrEmpty(origin))
                        {
                            await _webView.CoreWebView2.Profile.SetPermissionStateAsync(
                                CoreWebView2PermissionKind.Microphone, origin, CoreWebView2PermissionState.Allow);
                            await _webView.CoreWebView2.Profile.SetPermissionStateAsync(
                                CoreWebView2PermissionKind.Autoplay, origin, CoreWebView2PermissionState.Allow);
                        }
                    }
                    catch { /* ignore */ }

                    _webView.CoreWebView2.NavigationCompleted += async (_, e) =>
                    {
                        if (!e.IsSuccess) return;
                        _webView.CoreWebView2.IsMuted = false;
                        try
                        {
                            await _webView.CoreWebView2.ExecuteScriptAsync("""
                                (function(){
                                  document.querySelectorAll('video,audio').forEach(v=>{
                                    try{v.playsInline=true;v.muted=false;v.play();}catch(e){}
                                  });
                                })();
                                """);
                        }
                        catch { /* ignore */ }
                    };

                    _webView.CoreWebView2.Navigate(_spec.Url);

                    for (int i = 0; i < 50 && _webView.Handle == IntPtr.Zero; i++)
                        await Task.Delay(20);

                    await Task.Delay(100);

                    if (!TryStartGraphicsCapture(_hostForm.Handle))
                    {
                        _initError = "GPU capture (WGC) failed — JPEG fallback is disabled.";
                        _ready.Set();
                        try { _hostForm?.Close(); } catch { /* ignore */ }
                        return;
                    }

                    _captureMode = $"WGC-shared ({BrowserGpuSelector.ModeLabel(_browserGpu)})";
                    _useWgc = true;
                    _connected = true;
                    _ready.Set();
                }
                catch (WebView2RuntimeNotFoundException)
                {
                    _initError =
                        "WebView2 Runtime missing. Install the Evergreen runtime from https://developer.microsoft.com/microsoft-edge/webview2/";
                    _ready.Set();
                    try { _hostForm?.Close(); } catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    _initError = $"Browser source failed: {ex.Message}";
                    _ready.Set();
                    try { _hostForm?.Close(); } catch { /* ignore */ }
                }
            };

            WinForms.Application.Run(_hostForm);
        }
        catch (Exception ex)
        {
            _initError = $"Browser STA host failed: {ex.Message}";
            _ready.Set();
        }
        finally
        {
            StopGraphicsCapture();
            CleanupUi();
            if (_timerPeriodRaised)
            {
                timeEndPeriod(1);
                _timerPeriodRaised = false;
            }
            _disposedEvent.Set();
        }
    }

    private bool TryStartGraphicsCapture(IntPtr hwnd)
    {
        if (_capDevice is null || hwnd == IntPtr.Zero)
            return false;

        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                return false;

            EnsureTextureSlots(_spec.Width, _spec.Height);
            _winrtDevice = GraphicsCaptureHelper.CreateDirect3DDevice(_capDevice);
            _captureItem = GraphicsCaptureHelper.CreateItemForWindow(hwnd);
            _captureItem.Closed += (_, _) =>
            {
                _useWgc = false;
                _captureMode = "WGC-closed";
            };

            var size = new Windows.Graphics.SizeInt32
            {
                Width = Math.Max(2, _spec.Width),
                Height = Math.Max(2, _spec.Height)
            };

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                3,
                size);
            _framePool.FrameArrived += OnCaptureFrameArrived;

            _captureSession = _framePool.CreateCaptureSession(_captureItem);
            _captureSession.IsCursorCaptureEnabled = false;
            GraphicsCaptureHelper.TryDisableBorder(_captureSession);
            try
            {
                var prop = _captureSession.GetType().GetProperty("MinUpdateInterval");
                if (prop is not null)
                    prop.SetValue(_captureSession, TimeSpan.FromMilliseconds(16));
            }
            catch { /* ignore */ }

            _captureSession.StartCapture();
            return true;
        }
        catch
        {
            StopGraphicsCapture();
            return false;
        }
    }

    private void OnCaptureFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_exit || !_useWgc) return;

        Direct3D11CaptureFrame? latest = null;
        try
        {
            while (true)
            {
                var next = sender.TryGetNextFrame();
                if (next is null) break;
                latest?.Dispose();
                latest = next;
            }

            if (latest is null) return;

            var contentSize = latest.ContentSize;
            int copyW = Math.Min(contentSize.Width, _spec.Width);
            int copyH = Math.Min(contentSize.Height, _spec.Height);

            using (var tex = GraphicsCaptureHelper.GetTexture2D(latest.Surface))
                PublishFromCaptureTexture(tex, copyW, copyH);

            var drop = _heldFrame1;
            _heldFrame1 = _heldFrame0;
            _heldFrame0 = latest;
            latest = null;
            try { drop?.Dispose(); } catch { /* ignore */ }
        }
        catch
        {
            try { latest?.Dispose(); } catch { /* ignore */ }
        }
    }

    private void ReleaseHeldFrames()
    {
        try { _heldFrame1?.Dispose(); } catch { /* ignore */ }
        try { _heldFrame0?.Dispose(); } catch { /* ignore */ }
        _heldFrame1 = null;
        _heldFrame0 = null;
    }

    private void StopGraphicsCapture()
    {
        try { _captureSession?.Dispose(); } catch { /* ignore */ }
        _captureSession = null;
        try
        {
            if (_framePool is not null)
                _framePool.FrameArrived -= OnCaptureFrameArrived;
        }
        catch { /* ignore */ }
        try { _framePool?.Dispose(); } catch { /* ignore */ }
        _framePool = null;
        _captureItem = null;
        ReleaseHeldFrames();
        try { _winrtDevice?.Dispose(); } catch { /* ignore */ }
        _winrtDevice = null;
        _useWgc = false;
    }

    private void CleanupUi()
    {
        try
        {
            if (_webView is not null)
            {
                _hostForm?.Controls.Remove(_webView);
                _webView.Dispose();
            }
        }
        catch { /* ignore */ }
        _webView = null;

        try
        {
            if (_hostForm is { IsDisposed: false })
                _hostForm.Dispose();
        }
        catch { /* ignore */ }
        _hostForm = null;
        _connected = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exit = true;
        _connected = false;
        _useWgc = false;

        try
        {
            if (_hostForm is { IsDisposed: false })
            {
                if (_hostForm.InvokeRequired)
                    _hostForm.BeginInvoke(new Action(() =>
                    {
                        try { StopGraphicsCapture(); } catch { }
                        try { _hostForm.Close(); } catch { }
                    }));
                else
                {
                    StopGraphicsCapture();
                    _hostForm.Close();
                }
            }
            else StopGraphicsCapture();
        }
        catch { /* ignore */ }

        _disposedEvent.Wait(3000);
        _staThread = null;

        for (int i = 0; i < 2; i++)
            DisposeSlot(i);

        _capContext?.Dispose();
        _capContext = null;
        _capDevice?.Dispose();
        _capDevice = null;

        try { _ready.Dispose(); } catch { /* ignore */ }
        try { _disposedEvent.Dispose(); } catch { /* ignore */ }
    }

    private static void MakeClickThroughNearlyInvisible(IntPtr hwnd)
    {
        const int GwlExstyle = -20;
        const int WsExLayered = 0x80000;
        const int WsExTransparent = 0x20;
        const int LwaAlpha = 0x2;
        int style = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, style | WsExLayered | WsExTransparent);
        // Alpha=1: still composited by DWM (needed for WGC), barely visible.
        SetLayeredWindowAttributes(hwnd, 0, 1, LwaAlpha);
    }

    private sealed class HiddenHostForm : WinForms.Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolwindow = 0x00000080;
                const int WsExNoactivate = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolwindow | WsExNoactivate;
                return cp;
            }
        }
    }

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
