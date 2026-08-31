using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.Direct3D11;
using WinForms = System.Windows.Forms;

namespace NdiMerger.Sources;

/// <summary>
/// Browser source on dedicated threads:
/// STA (WebView2 + CapturePreview JPEG) → upload worker (alpha + D3D) → render only swaps SRV.
/// CapturePreview with GPU enabled stays fast; PrintWindow+PW_RENDERFULLCONTENT / --disable-gpu
/// caused either engine stalls or ~1 fps JPEG encodes.
/// </summary>
public sealed class BrowserVideoSource : IVideoSource
{
    private const int CaptureIntervalMs = 33; // ~30 fps

    private readonly BrowserSourceSpec _spec;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _disposedEvent = new(false);
    private readonly ManualResetEventSlim _uploadWake = new(false);
    private readonly object _cpuLock = new();
    private readonly object _gpuPublishLock = new();

    private Thread? _staThread;
    private Thread? _uploadThread;
    private WinForms.Form? _hostForm;
    private WebView2? _webView;
    private WinForms.Timer? _captureTimer;
    private MemoryStream? _previewStream;
    private Bitmap? _decodeBitmap;
    private GpuDevice? _gpu;

    private byte[]? _cpuPending;
    private byte[]? _cpuUpload;
    private int _cpuW;
    private int _cpuH;
    private bool _hasCpuPending;

    private readonly ID3D11Texture2D?[] _textures = new ID3D11Texture2D?[2];
    private readonly ID3D11ShaderResourceView?[] _srvs = new ID3D11ShaderResourceView?[2];
    private int _displayIndex;
    private int _readyIndex;
    private bool _hasGpuReady;
    private bool _newFrame;

    private volatile bool _connected;
    private volatile bool _exit;
    private volatile bool _disposed;
    private volatile bool _capturing;
    private volatile bool _pageReady;
    private bool _timerPeriodRaised;
    private string? _initError;

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

    public void Start(GpuDevice gpu)
    {
        _gpu = gpu;
        _exit = false;

        _uploadThread = new Thread(UploadLoop)
        {
            IsBackground = true,
            Name = $"BrowserUpload-{_spec.Id}",
            Priority = ThreadPriority.AboveNormal
        };
        _uploadThread.Start();

        _staThread = new Thread(StaMain)
        {
            IsBackground = true,
            Name = $"Browser-{_spec.Id}"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        if (!_ready.Wait(15000))
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

    private void SignalClose()
    {
        _exit = true;
        _uploadWake.Set();
        try
        {
            if (_hostForm is { IsDisposed: false })
                _hostForm.BeginInvoke(new Action(() => { try { _hostForm.Close(); } catch { } }));
        }
        catch { /* ignore */ }
    }

    private void SubmitCpuFrame(IntPtr data, int width, int height, int stride)
    {
        if (data == IntPtr.Zero || width <= 0 || height <= 0) return;

        int packedStride = width * 4;
        int len = packedStride * height;
        lock (_cpuLock)
        {
            if (_cpuPending is null || _cpuPending.Length != len)
                _cpuPending = new byte[len];

            if (stride == packedStride)
                Marshal.Copy(data, _cpuPending, 0, len);
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data + y * stride, _cpuPending, y * packedStride, packedStride);
            }

            _cpuW = width;
            _cpuH = height;
            _hasCpuPending = true;
        }

        _uploadWake.Set();
    }

    private void UploadLoop()
    {
        while (!_exit)
        {
            _uploadWake.Wait(100);
            if (_exit) break;

            byte[]? frame;
            int w, h;
            lock (_cpuLock)
            {
                if (!_hasCpuPending || _cpuPending is null || _gpu is null)
                    continue;

                w = _cpuW;
                h = _cpuH;
                frame = _cpuPending;
                var recycled = _cpuUpload;
                _cpuPending = recycled is not null && recycled.Length == frame.Length
                    ? recycled
                    : new byte[frame.Length];
                _cpuUpload = frame;
                _hasCpuPending = false;
            }

            int pixelBytes = w * h * 4;
            for (int i = 3; i < pixelBytes; i += 4)
                frame[i] = 255;

            if (_gpu is null) continue;

            int target;
            lock (_gpuPublishLock)
                target = 1 - _displayIndex;

            try
            {
                lock (_gpu.ContextLock)
                {
                    EnsureSlot(target, w, h);
                    unsafe
                    {
                        fixed (byte* ptr = frame)
                        {
                            _gpu.Context.UpdateSubresource(
                                _textures[target]!, 0, null, (IntPtr)ptr, (uint)(w * 4), (uint)pixelBytes);
                        }
                    }
                }

                lock (_gpuPublishLock)
                {
                    _readyIndex = target;
                    _hasGpuReady = true;
                }
            }
            catch
            {
                // keep uploading
            }
        }
    }

    private void EnsureSlot(int slot, int w, int h)
    {
        var tex = _textures[slot];
        if (tex is not null)
        {
            var desc = tex.Description;
            if (desc.Width == (uint)w && desc.Height == (uint)h)
                return;
        }

        _srvs[slot]?.Dispose();
        _textures[slot]?.Dispose();
        _textures[slot] = _gpu!.CreateTexture(w, h);
        _srvs[slot] = _gpu.Device.CreateShaderResourceView(_textures[slot]);
    }

    private void StaMain()
    {
        try
        {
            if (timeBeginPeriod(1) == 0)
                _timerPeriodRaised = true;

            WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.DpiUnaware);
            WinForms.Application.EnableVisualStyles();

            // Keep a few pixels on-screen so Chromium does not throttle the tab.
            const int onScreen = 8;
            _hostForm = new HiddenHostForm
            {
                FormBorderStyle = WinForms.FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = WinForms.FormStartPosition.Manual,
                Location = new Point(-Math.Max(0, _spec.Width - onScreen), 0),
                ClientSize = new Size(_spec.Width, _spec.Height),
                AutoScaleMode = WinForms.AutoScaleMode.None,
                Opacity = 1,
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
                    var userData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "NdiMergerLPM",
                        "WebView2",
                        _spec.Id);

                    Directory.CreateDirectory(userData);

                    // GPU stays ON so CapturePreview is fast. Avoid PrintWindow(PW_RENDERFULLCONTENT)
                    // which sync-flushes the shared NVIDIA adapter and stalls the 8K engine.
                    var options = new CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments = string.Join(' ',
                            "--disable-backgrounding-occluded-windows",
                            "--disable-renderer-backgrounding",
                            "--disable-background-timer-throttling",
                            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling",
                            "--autoplay-policy=no-user-gesture-required")
                    };

                    var env = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: userData,
                        options);

                    await _webView.EnsureCoreWebView2Async(env);
                    _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                    _webView.CoreWebView2.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess)
                            _pageReady = true;
                    };

                    _webView.CoreWebView2.Navigate(_spec.Url);

                    _previewStream = new MemoryStream(capacity: Math.Max(64 * 1024, _spec.Width * _spec.Height / 4));
                    _decodeBitmap = new Bitmap(_spec.Width, _spec.Height, PixelFormat.Format32bppArgb);

                    _captureTimer = new WinForms.Timer { Interval = CaptureIntervalMs };
                    _captureTimer.Tick += (_, _) => _ = CaptureFrameAsync();
                    _captureTimer.Start();

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
            CleanupUi();
            if (_timerPeriodRaised)
            {
                timeEndPeriod(1);
                _timerPeriodRaised = false;
            }
            _disposedEvent.Set();
        }
    }

    private async Task CaptureFrameAsync()
    {
        if (_exit || _capturing || _webView?.CoreWebView2 is null || _previewStream is null || _decodeBitmap is null)
            return;

        _capturing = true;
        try
        {
            _previewStream.SetLength(0);
            await _webView.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Jpeg,
                _previewStream);

            _previewStream.Position = 0;
            using var img = Image.FromStream(_previewStream, useEmbeddedColorManagement: false, validateImageData: false);

            using (var g = Graphics.FromImage(_decodeBitmap))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                g.DrawImage(img, 0, 0, _spec.Width, _spec.Height);
            }

            var rect = new Rectangle(0, 0, _spec.Width, _spec.Height);
            var data = _decodeBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (!_pageReady && IsMostlyBlack(data.Scan0, _spec.Width, _spec.Height, data.Stride))
                    return;

                SubmitCpuFrame(data.Scan0, _spec.Width, _spec.Height, data.Stride);
            }
            finally
            {
                _decodeBitmap.UnlockBits(data);
            }
        }
        catch
        {
            // ignore transient capture errors
        }
        finally
        {
            _capturing = false;
        }
    }

    private static unsafe bool IsMostlyBlack(IntPtr scan0, int width, int height, int stride)
    {
        byte* basePtr = (byte*)scan0;
        int dark = 0;
        int samples = 0;
        int stepX = Math.Max(1, width / 16);
        int stepY = Math.Max(1, height / 16);
        for (int y = 0; y < height; y += stepY)
        {
            byte* row = basePtr + y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                byte* p = row + x * 4;
                samples++;
                if (p[0] < 8 && p[1] < 8 && p[2] < 8)
                    dark++;
            }
        }

        return samples > 0 && dark * 100 / samples > 95;
    }

    private void CleanupUi()
    {
        try { _captureTimer?.Stop(); } catch { /* ignore */ }
        _captureTimer?.Dispose();
        _captureTimer = null;

        try { _previewStream?.Dispose(); } catch { /* ignore */ }
        _previewStream = null;
        _decodeBitmap?.Dispose();
        _decodeBitmap = null;

        try { _webView?.Dispose(); } catch { /* ignore */ }
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
        _uploadWake.Set();

        try
        {
            if (_hostForm is { IsDisposed: false })
            {
                if (_hostForm.InvokeRequired)
                    _hostForm.BeginInvoke(new Action(() =>
                    {
                        try { _hostForm.Close(); } catch { /* ignore */ }
                    }));
                else
                    _hostForm.Close();
            }
        }
        catch { /* ignore */ }

        _disposedEvent.Wait(3000);
        _uploadThread?.Join(2000);
        _staThread = null;
        _uploadThread = null;

        if (_gpu is not null)
        {
            lock (_gpu.ContextLock)
            {
                for (int i = 0; i < 2; i++)
                {
                    _srvs[i]?.Dispose();
                    _srvs[i] = null;
                    _textures[i]?.Dispose();
                    _textures[i] = null;
                }
            }
        }

        try { _ready.Dispose(); } catch { /* ignore */ }
        try { _disposedEvent.Dispose(); } catch { /* ignore */ }
        try { _uploadWake.Dispose(); } catch { /* ignore */ }
    }

    private sealed class HiddenHostForm : WinForms.Form
    {
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolwindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolwindow | WsExNoActivate;
                return cp;
            }
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
