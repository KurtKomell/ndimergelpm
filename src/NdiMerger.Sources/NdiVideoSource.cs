using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using NewTek;
using NewTek.NDI;

namespace NdiMerger.Sources;

public static class NdiBootstrap
{
    private static int _init;

    public static bool EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _init, 1) == 1)
            return true;

        try
        {
            if (!NDIlib.initialize())
            {
                _init = 0;
                return false;
            }
            return true;
        }
        catch
        {
            _init = 0;
            return false;
        }
    }
}

public sealed class NdiSourceCatalog : IDisposable
{
    private readonly Finder _finder;

    public NdiSourceCatalog()
    {
        NdiBootstrap.EnsureInitialized();
        _finder = new Finder(true);
    }

    public IReadOnlyList<DiscoveredSource> List()
    {
        return _finder.Sources
            .Select(s => new DiscoveredSource
            {
                Key = s.Name,
                DisplayName = s.Name,
                Kind = SourceKind.Ndi
            })
            .ToList();
    }

    public void Dispose() => _finder.Dispose();
}

public sealed class NdiVideoSource : BgraUploadSource
{
    private readonly string _sourceName;
    private Thread? _thread;
    private volatile bool _exit;
    private IntPtr _recv = IntPtr.Zero;
    private volatile bool _connected;

    public override string Key => _sourceName;
    public override string DisplayName => _sourceName;
    public override SourceKind Kind => SourceKind.Ndi;
    public override bool IsConnected => _connected;

    public NdiVideoSource(string sourceName)
    {
        _sourceName = sourceName;
    }

    public override void Start(GpuDevice gpu)
    {
        base.Start(gpu);
        NdiBootstrap.EnsureInitialized();
        _exit = false;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"NDI-{_sourceName}" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        try
        {
            var sourceNamePtr = UTF.StringToUtf8(_sourceName);
            var source = new NDIlib.source_t { p_ndi_name = sourceNamePtr };
            var desc = new NDIlib.recv_create_v3_t
            {
                source_to_connect_to = source,
                color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
                bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
                allow_video_fields = false,
                p_ndi_recv_name = UTF.StringToUtf8("NdiMergerLPM")
            };

            _recv = NDIlib.recv_create_v3(ref desc);
            Marshal.FreeHGlobal(sourceNamePtr);
            Marshal.FreeHGlobal(desc.p_ndi_recv_name);

            if (_recv == IntPtr.Zero)
                return;

            _connected = true;

            while (!_exit)
            {
                var video = new NDIlib.video_frame_v2_t();
                var audio = new NDIlib.audio_frame_v2_t();
                var meta = new NDIlib.metadata_frame_t();
                var type = NDIlib.recv_capture_v2(_recv, ref video, ref audio, ref meta, 500);

                switch (type)
                {
                    case NDIlib.frame_type_e.frame_type_video when video.p_data != IntPtr.Zero:
                    {
                        int w = video.xres;
                        int h = video.yres;
                        int stride = video.line_stride_in_bytes;
                        var buffer = new byte[stride * h];
                        Marshal.Copy(video.p_data, buffer, 0, buffer.Length);
                        SubmitFrame(buffer, w, h, stride);
                        NDIlib.recv_free_video_v2(_recv, ref video);
                        break;
                    }
                    case NDIlib.frame_type_e.frame_type_audio:
                        NDIlib.recv_free_audio_v2(_recv, ref audio);
                        break;
                    case NDIlib.frame_type_e.frame_type_metadata:
                        NDIlib.recv_free_metadata(_recv, ref meta);
                        break;
                }
            }
        }
        catch
        {
            _connected = false;
        }
        finally
        {
            if (_recv != IntPtr.Zero)
            {
                NDIlib.recv_destroy(_recv);
                _recv = IntPtr.Zero;
            }
            _connected = false;
        }
    }

    public override void Dispose()
    {
        _exit = true;
        _thread?.Join(2000);
        base.Dispose();
    }
}
