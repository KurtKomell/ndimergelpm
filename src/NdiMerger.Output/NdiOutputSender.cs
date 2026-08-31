using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NdiMerger.Core.Gpu;
using NewTek;
using NewTek.NDI;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NdiMerger.Output;

/// <summary>
/// Full-frame NDI sender targeting ~7 GB/s at 8038×5798.
/// GPU readback stays on the D3D11 thread; NDI async encode/send runs on a dedicated worker.
/// </summary>
public sealed class NdiOutputSender : IDisposable
{
    private const int StagingCount = 3;
    private const int CpuBufferCount = 4;
    private const int PackChunkRows = 128;

    private readonly GpuDevice _gpu;
    private readonly string _name;
    private readonly int _frameRate;
    private IntPtr _sender = IntPtr.Zero;
    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[StagingCount];
    private readonly IntPtr[] _cpuBuffers = new IntPtr[CpuBufferCount];
    private readonly ConcurrentQueue<int> _freeCpu = new();
    private BlockingCollection<int> _readyToSend = new(CpuBufferCount);
    private Thread? _sendThread;
    private int _width;
    private int _height;
    private long _bufferSizeBytes;
    private int _tightStride;
    private int _stagingPitch;
    private long _stagingBufferBytes;
    private bool _initialized;
    private bool _lastSendOk;
    private int _stagingIndex;
    private int _frameCounter;
    private int _lastSentCpu = -1;
    private long _bytesSentWindow;
    private DateTime _bandwidthWindowStart = DateTime.UtcNow;
    private volatile bool _shuttingDown;

    public string Name => _name;
    public bool IsActive => _sender != IntPtr.Zero;
    public int OutputWidth => _width;
    public int OutputHeight => _height;
    public long BufferSizeBytes => _bufferSizeBytes;
    public bool LastSendOk => _lastSendOk;
    /// <summary>Measured uncompressed payload throughput (tight BGRX bytes/sec).</summary>
    public double SendBytesPerSecond { get; private set; }
    public double SendGigabytesPerSecond => SendBytesPerSecond / 1_000_000_000.0;

    public int ConnectionCount
    {
        get
        {
            if (_sender == IntPtr.Zero) return 0;
            return NDIlib.send_get_no_connections(_sender, 0);
        }
    }

    public NdiOutputSender(GpuDevice gpu, string name, int frameRate = 60)
    {
        _gpu = gpu;
        _name = name;
        _frameRate = frameRate;
    }

    public void Initialize(int width, int height)
    {
        NdiFrameSpec.ValidateCanvasSize(width, height);
        EnsureSender(width, height);
    }

    private void EnsureSender(int width, int height)
    {
        NdiFrameSpec.ValidateCanvasSize(width, height);

        if (_initialized && _width == width && _height == height && _sender != IntPtr.Zero)
            return;

        StopWorkersAndReleaseResources(forShutdown: false);

        if (!NdiMerger.Sources.NdiBootstrap.EnsureInitialized())
            throw new InvalidOperationException("NDI library failed to initialize. Is Processing.NDI.Lib.x64.dll present?");

        var namePtr = UTF.StringToUtf8(_name);
        var desc = new NDIlib.send_create_t
        {
            p_ndi_name = namePtr,
            p_groups = IntPtr.Zero,
            clock_video = false,
            clock_audio = false
        };
        _sender = NDIlib.send_create(ref desc);
        Marshal.FreeHGlobal(namePtr);

        if (_sender == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create NDI sender.");

        _width = width;
        _height = height;
        _tightStride = width * NdiFrameSpec.BytesPerPixel;
        _bufferSizeBytes = (long)_tightStride * height;

        for (int i = 0; i < StagingCount; i++)
            _staging[i] = _gpu.CreateStagingTexture(width, height, Format.B8G8R8A8_UNorm);

        // Probe GPU staging pitch once so NDI can use pitched frames (no per-row pack).
        var probe = _gpu.Context.Map(_staging[0]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        _stagingPitch = (int)probe.RowPitch;
        _gpu.Context.Unmap(_staging[0]!, 0);
        _stagingBufferBytes = (long)_stagingPitch * height;

        while (_freeCpu.TryDequeue(out _)) { }
        for (int i = 0; i < CpuBufferCount; i++)
        {
            _cpuBuffers[i] = Marshal.AllocHGlobal((IntPtr)_stagingBufferBytes);
            _freeCpu.Enqueue(i);
        }

        _shuttingDown = false;
        _lastSentCpu = -1;
        _readyToSend = new BlockingCollection<int>(CpuBufferCount);

        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = "NdiMerger-NdiSend",
            Priority = ThreadPriority.Highest
        };
        _sendThread.Start();

        _initialized = true;
        _lastSendOk = false;
        _stagingIndex = 0;
        _frameCounter = 0;
        _bytesSentWindow = 0;
        _bandwidthWindowStart = DateTime.UtcNow;
        SendBytesPerSecond = 0;
    }

    public bool SendFrame(ID3D11Texture2D canvasTexture)
    {
        _lastSendOk = false;

        if (_shuttingDown || !_initialized || _sender == IntPtr.Zero)
            return false;

        var desc = canvasTexture.Description;
        if ((int)desc.Width != _width || (int)desc.Height != _height)
        {
            throw new InvalidOperationException(
                $"Canvas texture is {(int)desc.Width}×{(int)desc.Height}, expected {_width}×{_height}.");
        }

        int readIdx = (_stagingIndex + StagingCount - 1) % StagingCount;
        int writeIdx = _stagingIndex % StagingCount;
        var writeStaging = _staging[writeIdx];
        if (writeStaging is null)
            return false;

        if (_frameCounter > 0)
        {
            if (!_freeCpu.TryDequeue(out int cpuIdx))
            {
                // NDI back-pressured — skip GPU readback entirely this frame to keep Spout/composite smooth.
                return false;
            }

            var readStaging = _staging[readIdx];
            if (readStaging is null)
            {
                _freeCpu.Enqueue(cpuIdx);
                return false;
            }

            var mapFlags = _shuttingDown
                ? Vortice.Direct3D11.MapFlags.DoNotWait
                : Vortice.Direct3D11.MapFlags.None;

            try
            {
                var mapped = _gpu.Context.Map(readStaging, 0, MapMode.Read, mapFlags);
                try
                {
                    if (_shuttingDown)
                    {
                        _freeCpu.Enqueue(cpuIdx);
                        return false;
                    }

                    PackToBuffer(mapped.DataPointer, (int)mapped.RowPitch, _cpuBuffers[cpuIdx]);
                    if (!_readyToSend.IsAddingCompleted)
                        _readyToSend.Add(cpuIdx);
                    else
                        _freeCpu.Enqueue(cpuIdx);
                    _lastSendOk = true;
                }
                finally
                {
                    _gpu.Context.Unmap(readStaging, 0);
                }
            }
            catch (SharpGen.Runtime.SharpGenException ex) when (_shuttingDown && ex.HResult == unchecked((int)0x887A0007))
            {
                _freeCpu.Enqueue(cpuIdx);
                return false;
            }
            catch (InvalidOperationException)
            {
                _freeCpu.Enqueue(cpuIdx);
                return false;
            }
        }

        if (_shuttingDown)
            return false;

        _gpu.Context.CopyResource(writeStaging, canvasTexture);
        _stagingIndex = (_stagingIndex + 1) % StagingCount;
        _frameCounter++;
        return _lastSendOk || _frameCounter == 1;
    }

    private void SendLoop()
    {
        try
        {
            foreach (var cpuIdx in _readyToSend.GetConsumingEnumerable())
            {
                if (_shuttingDown || _sender == IntPtr.Zero)
                {
                    _freeCpu.Enqueue(cpuIdx);
                    continue;
                }

                var frame = new NDIlib.video_frame_v2_t
                {
                    xres = _width,
                    yres = _height,
                    // BGRX: opaque RGB — NDI skips alpha work and encodes faster than BGRA.
                    FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRX,
                    frame_rate_N = _frameRate,
                    frame_rate_D = 1,
                    picture_aspect_ratio = (float)_width / _height,
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    timecode = NDIlib.send_timecode_synthesize,
                    p_data = _cpuBuffers[cpuIdx],
                    line_stride_in_bytes = _stagingPitch,
                    p_metadata = IntPtr.Zero
                };

                NDIlib.send_send_video_async_v2(_sender, ref frame);

                if (_lastSentCpu >= 0)
                    _freeCpu.Enqueue(_lastSentCpu);
                _lastSentCpu = cpuIdx;

                _bytesSentWindow += _bufferSizeBytes;
                UpdateBandwidth();
            }
        }
        catch (InvalidOperationException)
        {
            // Collection completed during shutdown.
        }
    }

    private void UpdateBandwidth()
    {
        var now = DateTime.UtcNow;
        var dt = (now - _bandwidthWindowStart).TotalSeconds;
        if (dt < 1.0) return;

        SendBytesPerSecond = _bytesSentWindow / dt;
        _bytesSentWindow = 0;
        _bandwidthWindowStart = now;
    }

    private void PackToBuffer(IntPtr srcPtr, int srcStride, IntPtr dstPtr)
    {
        unsafe
        {
            byte* src = (byte*)srcPtr;
            byte* dst = (byte*)dstPtr;

            if (srcStride == _stagingPitch)
            {
                int chunks = (_height + PackChunkRows - 1) / PackChunkRows;
                Parallel.For(0, chunks, c =>
                {
                    int y0 = c * PackChunkRows;
                    int rows = Math.Min(PackChunkRows, _height - y0);
                    long bytes = (long)rows * srcStride;
                    long offset = (long)y0 * srcStride;
                    Buffer.MemoryCopy(src + offset, dst + offset, bytes, bytes);
                });
                return;
            }

            int copyBytes = Math.Min(srcStride, _tightStride);
            int dstStride = _stagingPitch;
            Parallel.For(0, _height, y =>
            {
                Buffer.MemoryCopy(
                    src + (long)y * srcStride,
                    dst + (long)y * dstStride,
                    dstStride,
                    copyBytes);
            });
        }
    }

    public void BeginShutdown()
    {
        _shuttingDown = true;
        try { _readyToSend.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }
    }

    private void StopWorkersAndReleaseResources(bool forShutdown)
    {
        _shuttingDown = true;
        try { _readyToSend.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }

        _sendThread?.Join(TimeSpan.FromSeconds(2));
        _sendThread = null;

        if (_sender != IntPtr.Zero)
        {
            if (!forShutdown)
            {
                try { FlushAsync(); }
                catch { /* best effort */ }
            }

            var sender = _sender;
            _sender = IntPtr.Zero;
            if (forShutdown)
            {
                var destroyTask = Task.Run(() => NDIlib.send_destroy(sender));
                destroyTask.Wait(TimeSpan.FromSeconds(2));
            }
            else
            {
                NDIlib.send_destroy(sender);
            }
        }

        for (int i = 0; i < StagingCount; i++)
        {
            _staging[i]?.Dispose();
            _staging[i] = null;
        }

        for (int i = 0; i < CpuBufferCount; i++)
        {
            if (_cpuBuffers[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_cpuBuffers[i]);
                _cpuBuffers[i] = IntPtr.Zero;
            }
        }

        while (_freeCpu.TryDequeue(out _)) { }
        _lastSentCpu = -1;
        _initialized = false;
        _bufferSizeBytes = 0;
        _width = 0;
        _height = 0;
        _frameCounter = 0;
        _stagingIndex = 0;
        _stagingPitch = 0;
        _stagingBufferBytes = 0;
        SendBytesPerSecond = 0;
    }

    private void FlushAsync()
    {
        var empty = new NDIlib.video_frame_v2_t();
        NDIlib.send_send_video_async_v2(_sender, ref empty);
    }

    public void Dispose()
    {
        BeginShutdown();
        StopWorkersAndReleaseResources(forShutdown: true);
        _readyToSend.Dispose();
    }
}
