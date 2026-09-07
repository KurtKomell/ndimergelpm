using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NdiMerger.Core.Gpu;
using NewTek;
using NewTek.NDI;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace NdiMerger.Output;

/// <summary>
/// Full-NDI sender for 10 GbE: GPU converts BGRA→UYVY (half the PCIe traffic, no CPU
/// color convert), then async SpeedHQ encode. No ready queue; clock_video off.
/// Same path for full-frame and zone crops.
/// </summary>
public sealed class NdiOutputSender : IDisposable
{
    // No ready queue. clock_video off. Keep one mapped frame in flight so async
    // encode can finish; do not flush a 0×0 frame after every send.
    private const int StagingCount = 12;
    private const int ReadyQueueCapacity = 1;
    private const long SynthesizeTimecode = long.MaxValue;

    private readonly GpuDevice _gpu;
    private readonly string _name;
    private readonly int _frameRate;
    private readonly int? _shqQuality;
    private IntPtr _sender = IntPtr.Zero;
    // Main-GPU shared targets (copy only on compose thread — never Map here).
    private readonly ID3D11Texture2D?[] _sharedMain = new ID3D11Texture2D?[StagingCount];
    // Dedicated readback device: open shared + staging Map off the compose context.
    private ID3D11Device? _rbDevice;
    private ID3D11DeviceContext? _rbContext;
    private readonly ID3D11Texture2D?[] _sharedRb = new ID3D11Texture2D?[StagingCount];
    private readonly ID3D11Texture2D?[] _rbStaging = new ID3D11Texture2D?[StagingCount];
    private readonly IntPtr[] _mappedPtr = new IntPtr[StagingCount];
    private readonly int[] _mappedPitch = new int[StagingCount];
    private readonly bool[] _stagingMapped = new bool[StagingCount];
    private readonly ConcurrentQueue<int> _freeStaging = new();
    private BlockingCollection<int> _readyToSend = new(ReadyQueueCapacity);
    private BlockingCollection<int> _packQueue = new(1);
    private Thread? _sendThread;
    private Thread? _packThread;
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
    private long _bytesSentWindow;
    private DateTime _bandwidthWindowStart = DateTime.UtcNow;
    private volatile bool _shuttingDown;
    private volatile int _connectionCount;
    private int _connPoll;
    private long _sendFpsWindowQpc;
    private int _sendCountWindow;
    private int _inFlightStaging = -1;

    private bool _useUyvy;
    private ID3D11Texture2D? _uyvyGpu;
    private ID3D11RenderTargetView? _uyvyRtv;
    private ID3D11ShaderResourceView? _canvasSrv;
    private IntPtr _canvasNative;
    private ID3D11VertexShader? _uyvyVs;
    private ID3D11PixelShader? _uyvyPs;
    private ID3D11BlendState? _opaqueBlend;
    private ID3D11RasterizerState? _rasterizer;

    public string Name => _name;
    public bool IsActive => _sender != IntPtr.Zero;
    /// <summary>True when the pack thread can take a new frame without dropping.</summary>
    public bool CanAcceptFrame =>
        !_shuttingDown && _initialized && _sender != IntPtr.Zero && _packQueue.Count == 0;
    public int OutputWidth => _width;
    public int OutputHeight => _height;
    public long BufferSizeBytes => _bufferSizeBytes;
    public bool LastSendOk => _lastSendOk;
    public string PixelFormat => _useUyvy ? "UYVY" : "BGRX";
    /// <summary>Uncompressed payload throughput (UYVY or BGRX bytes/sec into the NDI encoder).</summary>
    public double SendBytesPerSecond { get; private set; }
    public double SendGigabytesPerSecond => SendBytesPerSecond / 1_000_000_000.0;
    /// <summary>Actual send cadence from the last frame interval (not a fixed 60 Hz clock).</summary>
    public double SendFps { get; private set; }

    /// <summary>Cached on the send thread — never call into NDI from the UI (that stalls encode ~1 Hz).</summary>
    public int ConnectionCount => _connectionCount;

    /// <summary>
    /// Canonical Full-NDI send profile. clock_video off, async, TargetFps.
    /// <paramref name="shqQuality"/> null = shared zone 10 Gbps budget.
    /// </summary>
    public static NdiOutputSender CreateStandard(GpuDevice gpu, string name, int? shqQuality = null) =>
        new(gpu, name, frameRate: NdiFrameSpec.TargetFps, shqQuality);

    public static NdiOutputSender CreateFullFrame(GpuDevice gpu, string name, int width, int height) =>
        new(gpu, name, NdiFrameSpec.TargetFps,
            NdiMerger.Sources.NdiAdapterBinding.SpeedHqQualityPercent((long)width * height));

    public NdiOutputSender(GpuDevice gpu, string name, int frameRate = NdiFrameSpec.TargetFps, int? shqQuality = null)
    {
        _gpu = gpu;
        _name = name;
        _frameRate = frameRate;
        _shqQuality = shqQuality;
    }

    public void Initialize(int width, int height)
    {
        NdiFrameSpec.ValidateOutputSize(width, height);
        EnsureSender(width, height);
    }

    private void EnsureSender(int width, int height)
    {
        NdiFrameSpec.ValidateOutputSize(width, height);

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
        var configJson = NdiMerger.Sources.NdiAdapterBinding.SenderConfigJson(_shqQuality);
        var configPtr = UTF.StringToUtf8(configJson);
        _sender = SendCreateV2(ref desc, configPtr);
        if (_sender == IntPtr.Zero)
            _sender = NDIlib.send_create(ref desc);
        Marshal.FreeHGlobal(namePtr);
        Marshal.FreeHGlobal(configPtr);

        if (_sender == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create NDI sender.");

        RegisterConnectionMetadata();

        _width = width;
        _height = height;
        _useUyvy = TryCreateUyvyPipeline(width, height);

        int stagingWidth = _useUyvy ? width / 2 : width;
        _tightStride = _useUyvy
            ? NdiFrameSpec.UyvyStrideFor(width)
            : NdiFrameSpec.BgrxStrideFor(width);
        _bufferSizeBytes = _useUyvy
            ? NdiFrameSpec.UyvyBufferBytesFor(width, height)
            : NdiFrameSpec.BgrxBufferBytesFor(width, height);

        CreateReadbackPipeline(stagingWidth, height);

        while (_freeStaging.TryDequeue(out _)) { }
        for (int i = 0; i < StagingCount; i++)
        {
            _mappedPtr[i] = IntPtr.Zero;
            _mappedPitch[i] = 0;
            _stagingMapped[i] = false;
            _freeStaging.Enqueue(i);
        }

        _shuttingDown = false;
        _readyToSend = new BlockingCollection<int>(ReadyQueueCapacity);
        _packQueue = new BlockingCollection<int>(1);

        var threadSuffix = SanitizeThreadSuffix(_name);
        _sendThread = new Thread(SendLoop)
        {
            IsBackground = true,
            Name = $"NdiMerger-NdiSend-{threadSuffix}",
            Priority = ThreadPriority.Highest
        };
        _sendThread.Start();

        _packThread = new Thread(PackLoop)
        {
            IsBackground = true,
            Name = $"NdiMerger-NdiPack-{threadSuffix}",
            Priority = ThreadPriority.Highest
        };
        _packThread.Start();

        _initialized = true;
        _lastSendOk = false;
        _stagingIndex = 0;
        _frameCounter = 0;
        _bytesSentWindow = 0;
        _bandwidthWindowStart = DateTime.UtcNow;
        _connectionCount = 0;
        _connPoll = 0;
        _sendFpsWindowQpc = 0;
        _sendCountWindow = 0;
        _inFlightStaging = -1;
        SendFps = Math.Max(1, _frameRate);
        SendBytesPerSecond = 0;
    }

    private void CreateReadbackPipeline(int stagingWidth, int height)
    {
        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        var hr = D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var rbDevice,
            out _,
            out var rbContext);

        if (hr.Failure || rbDevice is null || rbContext is null)
            throw new InvalidOperationException($"NDI readback D3D device failed: {hr}");

        _rbDevice = rbDevice;
        _rbContext = rbContext;
        var mt = rbDevice.QueryInterfaceOrNull<ID3D11Multithread>();
        mt?.SetMultithreadProtected(true);
        mt?.Dispose();

        for (int i = 0; i < StagingCount; i++)
        {
            _sharedMain[i] = _gpu.CreateTexture(
                stagingWidth, height, Format.B8G8R8A8_UNorm,
                BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.Shared);

            using (var res = _sharedMain[i]!.QueryInterface<IDXGIResource>())
            {
                _sharedRb[i] = _rbDevice.OpenSharedResource<ID3D11Texture2D>(res.SharedHandle);
            }

            _rbStaging[i] = _rbDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)stagingWidth,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        var probe = _rbContext.Map(_rbStaging[0]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        _stagingPitch = (int)probe.RowPitch;
        _rbContext.Unmap(_rbStaging[0]!, 0);
        _stagingBufferBytes = (long)_stagingPitch * height;
    }

    private void DisposeReadbackPipeline()
    {
        for (int i = 0; i < StagingCount; i++)
        {
            _rbStaging[i]?.Dispose();
            _rbStaging[i] = null;
            _sharedRb[i]?.Dispose();
            _sharedRb[i] = null;
            _sharedMain[i]?.Dispose();
            _sharedMain[i] = null;
        }

        _rbContext?.Dispose();
        _rbContext = null;
        _rbDevice?.Dispose();
        _rbDevice = null;
    }

    private void RegisterConnectionMetadata()
    {
        try
        {
            var xml =
                "<ndi_product long_name=\"NdiMerger LPM\" short_name=\"NdiMergerLPM\" " +
                "manufacturer=\"NdiMerger\" model_name=\"MAM-Pixelmap\" />";
            var xmlPtr = UTF.StringToUtf8(xml);
            var meta = new NDIlib.metadata_frame_t { p_data = xmlPtr };
            NDIlib.send_add_connection_metadata(_sender, ref meta);
            Marshal.FreeHGlobal(xmlPtr);
        }
        catch
        {
            // Metadata is optional; send still works without it.
        }
    }

    private bool TryCreateUyvyPipeline(int width, int height)
    {
        try
        {
            const string hlsl = """
                Texture2D src : register(t0);

                void VSMain(uint id : SV_VertexID, out float4 pos : SV_Position)
                {
                    float2 uv = float2((id << 1) & 2, id & 2);
                    pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                }

                float3 RgbToYuv(float3 rgb)
                {
                    float y = 0.2126 * rgb.r + 0.7152 * rgb.g + 0.0722 * rgb.b;
                    float u = -0.114572 * rgb.r - 0.385428 * rgb.g + 0.5 * rgb.b;
                    float v = 0.5 * rgb.r - 0.454153 * rgb.g - 0.045847 * rgb.b;
                    y = 16.0 / 255.0 + y * (219.0 / 255.0);
                    u = 128.0 / 255.0 + u * (224.0 / 255.0);
                    v = 128.0 / 255.0 + v * (224.0 / 255.0);
                    return saturate(float3(y, u, v));
                }

                float4 PSMain(float4 pos : SV_Position) : SV_Target
                {
                    uint x = (uint)pos.x;
                    uint y = (uint)pos.y;
                    float4 c0 = src.Load(int3(x * 2, y, 0));
                    float4 c1 = src.Load(int3(x * 2 + 1, y, 0));
                    float3 yuv0 = RgbToYuv(c0.rgb);
                    float3 yuv1 = RgbToYuv(c1.rgb);
                    float u = 0.5 * (yuv0.y + yuv1.y);
                    float v = 0.5 * (yuv0.z + yuv1.z);
                    // DXGI B8G8R8A8 stores B,G,R,A. Shader (R,G,B,A)=(V,Y0,U,Y1) → memory UYVY.
                    return float4(v, yuv0.x, u, yuv1.x);
                }
                """;

            var vsBlob = Compiler.Compile(hlsl, "VSMain", "ndi_uyvy.hlsl", "vs_4_0");
            var psBlob = Compiler.Compile(hlsl, "PSMain", "ndi_uyvy.hlsl", "ps_4_0");
            _uyvyVs = _gpu.Device.CreateVertexShader(vsBlob.Span);
            _uyvyPs = _gpu.Device.CreatePixelShader(psBlob.Span);

            _uyvyGpu = _gpu.CreateTexture(width / 2, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget);
            _uyvyRtv = _gpu.Device.CreateRenderTargetView(_uyvyGpu);

            var blendDesc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = false,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
            _opaqueBlend = _gpu.Device.CreateBlendState(blendDesc);
            _rasterizer = _gpu.Device.CreateRasterizerState(new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                FrontCounterClockwise = false,
                DepthClipEnable = false,
                ScissorEnable = false
            });
            return true;
        }
        catch
        {
            DisposeUyvyPipeline();
            return false;
        }
    }

    public bool SendFrame(ID3D11Texture2D canvasTexture)
    {
        if (_shuttingDown || !_initialized || _sender == IntPtr.Zero || _rbContext is null)
            return false;

        var desc = canvasTexture.Description;
        if ((int)desc.Width != _width || (int)desc.Height != _height)
        {
            throw new InvalidOperationException(
                $"Canvas texture is {(int)desc.Width}×{(int)desc.Height}, expected {_width}×{_height}.");
        }

        int writeIdx = _stagingIndex % StagingCount;
        var shared = _sharedMain[writeIdx];
        if (shared is null)
            return false;

        // One pending pack slot. If pack is busy, skip — do not overwrite a
        // shared texture the pack thread may still be copying.
        if (_packQueue.Count >= 1)
            return false;

        ID3D11Texture2D gpuSrc = canvasTexture;
        if (_useUyvy)
        {
            if (!ConvertToUyvy(canvasTexture))
                return false;
            gpuSrc = _uyvyGpu!;
        }

        // Compose thread: GPU copy only — Map/Pack run on the dedicated readback device.
        _gpu.Context.CopyResource(shared, gpuSrc);

        try
        {
            if (!_packQueue.TryAdd(writeIdx))
                return false;
        }
        catch (InvalidOperationException)
        {
            // shutting down
            return false;
        }

        _stagingIndex = (_stagingIndex + 1) % StagingCount;
        _frameCounter++;
        return true;
    }

    private void PackLoop()
    {
        while (!_shuttingDown)
        {
            int sharedIdx;
            try
            {
                if (!_packQueue.TryTake(out sharedIdx, 50))
                    continue;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (_rbContext is null || _sharedRb[sharedIdx] is null)
                continue;

            if (!TryAcquireStaging(out int stagingIdx))
                continue;

            try
            {
                _rbContext.CopyResource(_rbStaging[stagingIdx]!, _sharedRb[sharedIdx]!);
                var mapped = _rbContext.Map(_rbStaging[stagingIdx]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                if (_shuttingDown)
                {
                    _rbContext.Unmap(_rbStaging[stagingIdx]!, 0);
                    _freeStaging.Enqueue(stagingIdx);
                    continue;
                }

                _mappedPtr[stagingIdx] = mapped.DataPointer;
                _mappedPitch[stagingIdx] = (int)mapped.RowPitch;
                _stagingMapped[stagingIdx] = true;

                // Edge clear skipped every frame — pack CPU was causing receiver dips.
                // Top/bottom are already black from the compositor clear.
                EnqueueLatestStaging(stagingIdx);
                _lastSendOk = true;
                stagingIdx = -1;
            }
            catch
            {
                if (stagingIdx >= 0)
                {
                    if (_stagingMapped[stagingIdx] && _rbStaging[stagingIdx] is not null)
                    {
                        try { _rbContext?.Unmap(_rbStaging[stagingIdx]!, 0); } catch { /* ignore */ }
                    }
                    _stagingMapped[stagingIdx] = false;
                    _mappedPtr[stagingIdx] = IntPtr.Zero;
                    _mappedPitch[stagingIdx] = 0;
                    _freeStaging.Enqueue(stagingIdx);
                }
            }
        }
    }

    private bool ConvertToUyvy(ID3D11Texture2D canvasTexture)
    {
        if (_uyvyGpu is null || _uyvyRtv is null || _uyvyVs is null || _uyvyPs is null)
            return false;

        if (_canvasNative != canvasTexture.NativePointer)
        {
            _canvasSrv?.Dispose();
            _canvasSrv = _gpu.Device.CreateShaderResourceView(canvasTexture);
            _canvasNative = canvasTexture.NativePointer;
        }

        if (_canvasSrv is null)
            return false;

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_uyvyRtv);
        // Fullscreen draw covers all pixels — skip Clear (saves a full-frame GPU pass).
        ctx.OMSetBlendState(_opaqueBlend);
        ctx.OMSetDepthStencilState(null);
        ctx.RSSetState(_rasterizer);
        ctx.RSSetViewport(new Viewport(0, 0, _width / 2, _height));
        ctx.IASetInputLayout(null!);
        ctx.IASetVertexBuffer(0, null!, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_uyvyVs);
        ctx.PSSetShader(_uyvyPs);
        ctx.PSSetShaderResource(0, _canvasSrv);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        return true;
    }

    private bool TryAcquireStaging(out int stagingIdx)
    {
        if (_freeStaging.TryDequeue(out stagingIdx))
            return true;

        // Never steal a queued send frame — that punches a hole in the NDI clock.
        stagingIdx = -1;
        return false;
    }

    private void UnmapStaging(int stagingIdx)
    {
        if (stagingIdx < 0 || stagingIdx >= StagingCount)
            return;
        if (_stagingMapped[stagingIdx] && _rbContext is not null && _rbStaging[stagingIdx] is not null)
        {
            try { _rbContext.Unmap(_rbStaging[stagingIdx]!, 0); }
            catch { /* ignore */ }
        }
        _stagingMapped[stagingIdx] = false;
        _mappedPtr[stagingIdx] = IntPtr.Zero;
        _mappedPitch[stagingIdx] = 0;
    }

    private void EnqueueLatestStaging(int stagingIdx)
    {
        if (_readyToSend.IsAddingCompleted)
        {
            UnmapStaging(stagingIdx);
            _freeStaging.Enqueue(stagingIdx);
            return;
        }

        try
        {
            if (!_readyToSend.TryAdd(stagingIdx))
            {
                DiscardStaleReadyFrames();
                if (!_readyToSend.TryAdd(stagingIdx))
                {
                    UnmapStaging(stagingIdx);
                    _freeStaging.Enqueue(stagingIdx);
                }
            }
        }
        catch (InvalidOperationException)
        {
            UnmapStaging(stagingIdx);
            _freeStaging.Enqueue(stagingIdx);
        }
    }

    private void DiscardStaleReadyFrames(int keep = 0)
    {
        while (_readyToSend.Count > keep && _readyToSend.TryTake(out int stale))
        {
            UnmapStaging(stale);
            _freeStaging.Enqueue(stale);
        }
    }

    private void SendLoop()
    {
        try
        {
            foreach (var stagingIdx in _readyToSend.GetConsumingEnumerable())
            {
                if (_shuttingDown || _sender == IntPtr.Zero)
                {
                    UnmapStaging(stagingIdx);
                    _freeStaging.Enqueue(stagingIdx);
                    continue;
                }

                StampSendTiming(out int rateN, out int rateD, out long timecode);

                var frame = new NDIlib.video_frame_v2_t
                {
                    xres = _width,
                    yres = _height,
                    FourCC = _useUyvy
                        ? NDIlib.FourCC_type_e.FourCC_type_UYVY
                        : NDIlib.FourCC_type_e.FourCC_type_BGRX,
                    frame_rate_N = rateN,
                    frame_rate_D = rateD,
                    picture_aspect_ratio = 0f,
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    timecode = timecode,
                    p_data = _mappedPtr[stagingIdx],
                    line_stride_in_bytes = _mappedPitch[stagingIdx],
                    p_metadata = IntPtr.Zero
                };

                NDIlib.send_send_video_async_v2(_sender, ref frame);

                // Previous mapped buffer is free once NDI has accepted the new frame.
                int prev = _inFlightStaging;
                _inFlightStaging = stagingIdx;
                if (prev >= 0)
                {
                    UnmapStaging(prev);
                    _freeStaging.Enqueue(prev);
                }

                _bytesSentWindow += _bufferSizeBytes;
                NoteSendFps();
                UpdateBandwidth();
                // Poll often so the first viewer is not joined mid-GOP for seconds.
                if ((_connPoll++ & 7) == 0)
                    PollConnectionsNow();
            }
        }
        catch (InvalidOperationException)
        {
            // Collection completed during shutdown.
        }
    }

    private void NoteSendFps()
    {
        long qpc = Stopwatch.GetTimestamp();
        if (_sendFpsWindowQpc == 0)
            _sendFpsWindowQpc = qpc;
        _sendCountWindow++;
        double sec = (qpc - _sendFpsWindowQpc) / (double)Stopwatch.Frequency;
        if (sec >= 1.0)
        {
            SendFps = _sendCountWindow / sec;
            _sendCountWindow = 0;
            _sendFpsWindowQpc = qpc;
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

    private void PollConnectionsNow()
    {
        if (_sender == IntPtr.Zero) return;
        _connectionCount = NDIlib.send_get_no_connections(_sender, 0);
    }

    /// <summary>
    /// Fixed TargetFps metadata. SendFps is a 1-second window average (see NoteSendFps).
    /// </summary>
    private void StampSendTiming(out int rateN, out int rateD, out long timecode)
    {
        rateN = Math.Max(1, _frameRate);
        rateD = 1;
        timecode = SynthesizeTimecode;
    }

    public void BeginShutdown()
    {
        _shuttingDown = true;
        try { _packQueue.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }
        try { _readyToSend.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }
    }

    private void StopWorkersAndReleaseResources(bool forShutdown)
    {
        _shuttingDown = true;
        try { _packQueue.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }
        try { _readyToSend.CompleteAdding(); }
        catch (InvalidOperationException) { /* already completed */ }

        _packThread?.Join(TimeSpan.FromSeconds(2));
        _packThread = null;
        _sendThread?.Join(TimeSpan.FromSeconds(2));
        _sendThread = null;

        if (_sender != IntPtr.Zero)
        {
            // One shutdown flush so the last in-flight p_data is finished before unmap.
            try { FlushAsync(); }
            catch { /* best effort */ }
            if (_inFlightStaging >= 0)
            {
                UnmapStaging(_inFlightStaging);
                _freeStaging.Enqueue(_inFlightStaging);
                _inFlightStaging = -1;
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

        DisposeReadbackPipeline();
        DisposeUyvyPipeline();

        while (_freeStaging.TryDequeue(out _)) { }
        for (int i = 0; i < StagingCount; i++)
        {
            UnmapStaging(i);
        }

        _initialized = false;
        _bufferSizeBytes = 0;
        _width = 0;
        _height = 0;
        _frameCounter = 0;
        _stagingIndex = 0;
        _stagingPitch = 0;
        _stagingBufferBytes = 0;
        _useUyvy = false;
        _connectionCount = 0;
        _inFlightStaging = -1;
        SendFps = 0;
        SendBytesPerSecond = 0;
    }

    private void DisposeUyvyPipeline()
    {
        _canvasSrv?.Dispose();
        _canvasSrv = null;
        _canvasNative = IntPtr.Zero;
        _uyvyRtv?.Dispose();
        _uyvyRtv = null;
        _uyvyGpu?.Dispose();
        _uyvyGpu = null;
        _uyvyVs?.Dispose();
        _uyvyVs = null;
        _uyvyPs?.Dispose();
        _uyvyPs = null;
        _opaqueBlend?.Dispose();
        _opaqueBlend = null;
        _rasterizer?.Dispose();
        _rasterizer = null;
    }

    private static string SanitizeThreadSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Out";

        Span<char> buf = stackalloc char[Math.Min(name.Length, 32)];
        int n = 0;
        foreach (var c in name)
        {
            if (n >= buf.Length)
                break;
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                buf[n++] = c;
            else if (c is ' ' or '.')
                buf[n++] = '-';
        }

        return n > 0 ? new string(buf[..n]) : "Out";
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

    [DllImport("Processing.NDI.Lib.x64.dll", EntryPoint = "NDIlib_send_create_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SendCreateV2(ref NDIlib.send_create_t createSettings, IntPtr configJson);
}
