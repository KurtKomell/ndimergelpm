using System.Runtime.InteropServices;
using DirectShowLib;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;

namespace NdiMerger.Sources;

public static class CaptureSourceCatalog
{
    public static IReadOnlyList<DiscoveredSource> List()
    {
        var list = new List<DiscoveredSource>();
        foreach (var device in DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice))
        {
            list.Add(new DiscoveredSource
            {
                Key = device.DevicePath,
                DisplayName = device.Name,
                Kind = SourceKind.Capture
            });
        }
        return list;
    }
}

/// <summary>
/// DirectShow capture (webcams / HDMI capture cards) via Sample Grabber → BGRA.
/// </summary>
public sealed class CaptureVideoSource : BgraUploadSource
{
    private readonly string _devicePath;
    private readonly string _displayName;
    private IFilterGraph2? _graph;
    private IMediaControl? _mediaControl;
    private SampleGrabberCallback? _callback;
    private volatile bool _connected;
    private Thread? _pumpThread;
    private volatile bool _exit;
    private bool _flipVertical;

    public override string Key => _devicePath;
    public override string DisplayName => _displayName;
    public override SourceKind Kind => SourceKind.Capture;
    public override bool IsConnected => _connected;

    public CaptureVideoSource(string devicePath, string displayName)
    {
        _devicePath = devicePath;
        _displayName = displayName;
    }

    public override void Start(GpuDevice gpu)
    {
        base.Start(gpu);
        BuildGraph();
        _exit = false;
        _pumpThread = new Thread(Pump) { IsBackground = true, Name = $"Capture-{_displayName}" };
        _pumpThread.Start();
    }

    private void BuildGraph()
    {
        _graph = (IFilterGraph2)new FilterGraph();
        var captureGraph = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
        captureGraph.SetFiltergraph(_graph);

        DsDevice? device = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
            .FirstOrDefault(d => d.DevicePath == _devicePath || d.Name == _displayName);
        if (device is null)
            throw new InvalidOperationException($"Capture device not found: {_displayName}");

        _graph.AddSourceFilterForMoniker(device.Mon, null, device.Name, out var sourceFilter);

        var sampleGrabberFilter = (IBaseFilter)new SampleGrabber();
        var sampleGrabber = (ISampleGrabber)sampleGrabberFilter;
        var mediaType = new AMMediaType
        {
            majorType = MediaType.Video,
            subType = MediaSubType.RGB32,
            formatType = FormatType.VideoInfo
        };
        sampleGrabber.SetMediaType(mediaType);
        DsUtils.FreeAMMediaType(mediaType);

        _graph.AddFilter(sampleGrabberFilter, "SampleGrabber");
        _callback = new SampleGrabberCallback(OnFrame);
        sampleGrabber.SetCallback(_callback, 1);
        sampleGrabber.SetBufferSamples(false);
        sampleGrabber.SetOneShot(false);

        var nullRenderer = (IBaseFilter)new NullRenderer();
        _graph.AddFilter(nullRenderer, "NullRenderer");

        var hr = captureGraph.RenderStream(
            PinCategory.Capture, MediaType.Video, sourceFilter, sampleGrabberFilter, nullRenderer);
        if (hr < 0)
            throw new InvalidOperationException($"Failed to render capture stream for '{_displayName}': 0x{hr:X8}");

        var connected = new AMMediaType();
        sampleGrabber.GetConnectedMediaType(connected);
        try
        {
            var vih = Marshal.PtrToStructure<VideoInfoHeader>(connected.formatPtr);
            int w = vih.BmiHeader.Width;
            int biHeight = vih.BmiHeader.Height;
            int h = Math.Abs(biHeight);
            _flipVertical = biHeight > 0;
            _callback.SetSize(w, h);
        }
        finally
        {
            DsUtils.FreeAMMediaType(connected);
        }

        _mediaControl = (IMediaControl)_graph;
        _mediaControl.Run();
        _connected = true;

        Marshal.ReleaseComObject(captureGraph);
    }

    private void OnFrame(IntPtr data, int length, int width, int height)
    {
        if (width <= 0 || height <= 0 || length <= 0) return;

        int expected = width * height * 4;
        int srcStride = length / Math.Max(1, height);
        var packed = new byte[expected];

        if (_flipVertical)
        {
            for (int y = 0; y < height; y++)
            {
                int srcY = height - 1 - y;
                Marshal.Copy(data + srcY * srcStride, packed, y * width * 4, width * 4);
            }
        }
        else if (srcStride == width * 4)
        {
            Marshal.Copy(data, packed, 0, Math.Min(length, expected));
        }
        else
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(data + y * srcStride, packed, y * width * 4, width * 4);
        }

        SubmitFrame(packed, width, height, width * 4);
    }

    private void Pump()
    {
        while (!_exit)
            Thread.Sleep(50);
    }

    public override void Dispose()
    {
        _exit = true;
        _pumpThread?.Join(1000);
        try
        {
            _mediaControl?.Stop();
        }
        catch { /* ignore */ }

        if (_graph is not null)
            Marshal.ReleaseComObject(_graph);
        _graph = null;
        _mediaControl = null;
        _connected = false;
        base.Dispose();
    }

    private sealed class SampleGrabberCallback : ISampleGrabberCB
    {
        private readonly Action<IntPtr, int, int, int> _onFrame;
        private int _width;
        private int _height;

        public SampleGrabberCallback(Action<IntPtr, int, int, int> onFrame) => _onFrame = onFrame;

        public int SampleCB(double sampleTime, IMediaSample sample) => 0;

        public int BufferCB(double sampleTime, IntPtr buffer, int bufferLen)
        {
            if (_width <= 0 || _height <= 0)
                GuessSize(bufferLen, out _width, out _height);

            _onFrame(buffer, bufferLen, _width, _height);
            return 0;
        }

        public void SetSize(int w, int h)
        {
            _width = w;
            _height = h;
        }

        private static void GuessSize(int bufferLen, out int w, out int h)
        {
            int pixels = bufferLen / 4;
            (int W, int H)[] common =
            [
                (1920, 1080), (1280, 720), (1920, 1200), (3840, 2160),
                (800, 600), (640, 480), (1024, 768), (1600, 1200)
            ];
            foreach (var c in common)
            {
                if (c.W * c.H == pixels)
                {
                    w = c.W;
                    h = c.H;
                    return;
                }
            }

            w = (int)Math.Sqrt(pixels * 16.0 / 9.0);
            h = pixels / Math.Max(1, w);
        }
    }
}
