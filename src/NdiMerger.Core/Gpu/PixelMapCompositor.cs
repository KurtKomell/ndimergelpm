using System.Numerics;
using System.Runtime.InteropServices;
using NdiMerger.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace NdiMerger.Core.Gpu;

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public Vector3 Position;
    public Vector2 TexCoord;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProjection;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LayerConstants
{
    public Matrix4x4 World;
    public Vector4 Color;
    public float UseTexAlpha;
    public float BlackKeyEnabled;
    public float BlackKeyThreshold;
    public float BlackKeySoftness;
    public float SourcePremultiplied;
    public float Pad0;
    public float Pad1;
    public float Pad2;
    public Vector2 SourceUvMin;
    public Vector2 SourceUvMax;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ReflectConstants
{
    public Matrix4x4 ViewProjection;
    public Vector4 Color;
    public Vector2 FloorPos;
    public Vector2 FloorSize;
    public Vector2 Vanishing;
    public Vector2 RtSize;
    public Vector2 MiterA;
    public Vector2 MiterB;
    public Vector2 FarA;
    public Vector2 FarB;
    public float LengthPx;
    public float FadeStart;
    public Vector2 SideFade;
    public float BlackKeyEnabled;
    public float BlackKeyThreshold;
    public float BlackKeySoftness;
    public float PadKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BlurConstants
{
    public Vector2 TexelSize;
    public Vector2 Direction;
    public float Radius;
    public float Pad0;
    public float Pad1;
    public float Pad2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BlobConstants
{
    public Matrix4x4 ViewProjection;
    public Vector4 Color;
    public Vector2 Center;
    public float Radius;
    /// <summary>0..1 pulse phase; negative disables the expanding ring (trails / debug).</summary>
    public float PulsePhase;
    public float RingWidth;
    public float Pad0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveConstants
{
    public Vector2 FloorPos;
    public Vector2 FloorSize;
    public Vector2 CanvasSize;
    public Vector2 StageV;
    public float Progress;
    public float Amplitude;
    public float Wavelength;
    public float Opacity;
    public Vector2 WestKinkA;
    public Vector2 WestKinkB;
    public float BlobCount;
    public float Pad0;
    public float Pad1;
    public float Pad2;
    public Vector4 Blob0;
    public Vector4 Blob1;
    public Vector4 Blob2;
    public Vector4 Blob3;
    public Vector4 Blob4;
    public Vector4 Blob5;
    public Vector4 Blob6;
    public Vector4 Blob7;
}

public sealed class PixelMapCompositor : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly ID3D11Texture2D _canvasTexture;
    private readonly ID3D11RenderTargetView _canvasRtv;
    private readonly ID3D11ShaderResourceView _canvasSrv;

    // 3D-only compose: same layout as the output canvas, but with Room3DRotationDegrees baked in.
    private ID3D11Texture2D? _roomCanvasTexture;
    private ID3D11RenderTargetView? _roomCanvasRtv;
    private ID3D11ShaderResourceView? _roomCanvasSrv;

    private readonly ID3D11Texture2D _previewTexture;
    private readonly ID3D11RenderTargetView _previewRtv;
    // Preview CPU readback uses a dedicated D3D device (same adapter). Compose never Maps.
    private const int PreviewSharedCount = 3;
    private readonly ID3D11Texture2D?[] _previewSharedMain = new ID3D11Texture2D?[PreviewSharedCount];
    private ID3D11Device? _previewRbDevice;
    private ID3D11DeviceContext? _previewRbContext;
    private readonly ID3D11Texture2D?[] _previewSharedRb = new ID3D11Texture2D?[PreviewSharedCount];
    private readonly ID3D11Texture2D?[] _previewRbStaging = new ID3D11Texture2D?[PreviewSharedCount];
    private readonly byte[][] _previewCpu = new byte[2][];
    private int _previewWrite;
    private int _previewCopies;
    private int _previewPublished = -1;
    private int _previewGeneration;
    private Thread? _previewPackThread;
    public event Action? PreviewFrameReady;
    private volatile bool _previewPackExit;
    private readonly AutoResetEvent _previewPackWake = new(false);

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _vb;
    private readonly ID3D11Buffer _frameCb;
    private readonly ID3D11Buffer _layerCb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11SamplerState _borderSampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11BlendState _maxBlend;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly ID3D11VertexShader _reflectVs;
    private readonly ID3D11VertexShader _blobVs;
    private readonly ID3D11PixelShader _reflectPs;
    private readonly ID3D11PixelShader _blurPs;
    private readonly ID3D11PixelShader _blobPs;
    private readonly ID3D11PixelShader _wavePs;
    private readonly ID3D11Buffer _reflectVb;
    private readonly ID3D11Buffer _reflectCb;
    private readonly ID3D11Buffer _blurCb;
    private readonly ID3D11Buffer _blobCb;
    private readonly ID3D11Buffer _waveCb;

    private ID3D11Texture2D? _reflectTex;
    private ID3D11RenderTargetView? _reflectRtv;
    private ID3D11ShaderResourceView? _reflectSrv;
    private ID3D11Texture2D? _blurTex;
    private ID3D11RenderTargetView? _blurRtv;
    private ID3D11ShaderResourceView? _blurSrv;
    private ID3D11Texture2D? _blobTex;
    private ID3D11RenderTargetView? _blobRtv;
    private ID3D11ShaderResourceView? _blobSrv;
    private ID3D11Texture2D? _waveTex;
    private ID3D11RenderTargetView? _waveRtv;
    private ID3D11ShaderResourceView? _waveSrv;
    private int _waveW;
    private int _waveH;
    private int _reflectW;
    private int _reflectH;

    private ID3D11Texture2D? _backgroundTexture;
    private ID3D11ShaderResourceView? _backgroundSrv;
    private int _backgroundWidth;
    private int _backgroundHeight;

    /// <summary>Legacy; pixelmap is now drawn under layers / under the canvas blit.</summary>
    public const float OverlayOpacity = 0.5f;

    public int CanvasWidth => _canvasWidth;
    public int CanvasHeight => _canvasHeight;
    public int BackgroundWidth => _backgroundWidth;
    public int BackgroundHeight => _backgroundHeight;
    private bool HasBackground => _backgroundSrv is not null && _backgroundWidth > 0 && _backgroundHeight > 0;
    public int PreviewWidth { get; }
    public int PreviewHeight { get; }
    public int PreviewGeneration => Volatile.Read(ref _previewGeneration);
    public ID3D11Texture2D CanvasTexture => _canvasTexture;
    public ID3D11ShaderResourceView CanvasSrv => _canvasSrv;
    /// <summary>3D-only canvas (Room3DRotation baked). Valid after <see cref="RenderRoomCanvas"/>.</summary>
    public ID3D11ShaderResourceView? RoomCanvasSrv => _roomCanvasSrv;
    /// <summary>Floor-resolution mirror RT after the last <see cref="Render"/> (null if not drawn).</summary>
    public ID3D11ShaderResourceView? FloorMirrorSrv { get; private set; }
    /// <summary>Floor-resolution LiDAR blob RT after the last <see cref="Render"/> (null if not drawn).</summary>
    public ID3D11ShaderResourceView? FloorBlobSrv { get; private set; }
    public GpuDevice Gpu => _gpu;

    public PixelMapCompositor(GpuDevice gpu, int canvasWidth, int canvasHeight, int previewMaxWidth = 1600)
    {
        _gpu = gpu;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;

        float scale = Math.Min(1f, previewMaxWidth / (float)canvasWidth);
        PreviewWidth = Math.Max(1, (int)(canvasWidth * scale));
        PreviewHeight = Math.Max(1, (int)(canvasHeight * scale));

        _canvasTexture = gpu.CreateTexture(canvasWidth, canvasHeight);
        _canvasRtv = gpu.Device.CreateRenderTargetView(_canvasTexture);
        _canvasSrv = gpu.Device.CreateShaderResourceView(_canvasTexture);

        _previewTexture = gpu.CreateTexture(PreviewWidth, PreviewHeight);
        _previewRtv = gpu.Device.CreateRenderTargetView(_previewTexture);
        int previewBytes = PreviewWidth * 4 * PreviewHeight;
        _previewCpu[0] = new byte[previewBytes];
        _previewCpu[1] = new byte[previewBytes];
        CreatePreviewReadback();

        CompileShaders(out _vs, out _ps, out _inputLayout);
        CompileReflectShaders(out _reflectVs, out _reflectPs);
        CompileBlobShader(out _blobVs, out _blobPs);
        CompileBlurShader(out _blurPs);
        CompileWaveShader(out _wavePs);

        var vertices = new Vertex[]
        {
            new() { Position = new Vector3(0, 0, 0), TexCoord = new Vector2(0, 0) },
            new() { Position = new Vector3(1, 0, 0), TexCoord = new Vector2(1, 0) },
            new() { Position = new Vector3(0, 1, 0), TexCoord = new Vector2(0, 1) },
            new() { Position = new Vector3(1, 1, 0), TexCoord = new Vector2(1, 1) },
        };
        _vb = gpu.Device.CreateBuffer(vertices, BindFlags.VertexBuffer);

        _reflectVb = gpu.Device.CreateBuffer(new BufferDescription(
            (uint)(Marshal.SizeOf<Vertex>() * 4),
            BindFlags.VertexBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));

        _frameCb = CreateConstantBuffer(Marshal.SizeOf<FrameConstants>());
        _layerCb = CreateConstantBuffer(Marshal.SizeOf<LayerConstants>());
        _reflectCb = CreateConstantBuffer(Marshal.SizeOf<ReflectConstants>());
        _blurCb = CreateConstantBuffer(Marshal.SizeOf<BlurConstants>());
        _blobCb = CreateConstantBuffer(Marshal.SizeOf<BlobConstants>());
        _waveCb = CreateConstantBuffer(Marshal.SizeOf<WaveConstants>());

        _sampler = gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        _borderSampler = gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border,
            BorderColor = new Color4(0, 0, 0, 0),
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            // Premultiplied alpha: shaders output rgb*a so fade+black-key doesn't
            // leave a black veil while DrawOpacity animates (esp. West walls).
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blend = gpu.Device.CreateBlendState(blendDesc);

        var maxBlendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        maxBlendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.One,
            BlendOperation = BlendOperation.Max,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Max,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _maxBlend = gpu.Device.CreateBlendState(maxBlendDesc);

        _rasterizer = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnable = true
        });
    }

    public void SetBackgroundFromBgra(byte[] bgra, int width, int height)
    {
        _backgroundSrv?.Dispose();
        _backgroundTexture?.Dispose();

        _backgroundWidth = width;
        _backgroundHeight = height;
        _backgroundTexture = _gpu.CreateTexture(width, height);
        unsafe
        {
            fixed (byte* ptr = bgra)
            {
                _gpu.Context.UpdateSubresource(_backgroundTexture, 0, null, (IntPtr)ptr, (uint)(width * 4), (uint)bgra.Length);
            }
        }

        _backgroundSrv = _gpu.Device.CreateShaderResourceView(_backgroundTexture);
    }

    public void LoadBackgroundFile(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        int w = bmp.Width;
        int h = bmp.Height;
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * h;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);

            // Convert ARGB (GDI) stride to tightly packed BGRA if needed
            if (data.Stride == w * 4)
            {
                SetBackgroundFromBgra(buffer, w, h);
            }
            else
            {
                var packed = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(buffer, y * Math.Abs(data.Stride), packed, y * w * 4, w * 4);
                SetBackgroundFromBgra(packed, w, h);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Render(
        IReadOnlyList<CompositionLayer> layers,
        Func<CompositionLayer, ID3D11ShaderResourceView?> resolveSrv,
        bool overlayPreview,
        bool overlayOutput,
        IReadOnlyList<ZoneDefinition>? zones = null,
        FloorReflectionSettings? reflection = null,
        FloorBlobFrame? blobs = null,
        FloorBlobSettings? blobSettings = null,
        FloorWaveSettings? wave = null)
    {
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        // Transparent clear so black-key holes stay alpha=0 (preview can show pixelmap underneath).
        ctx.ClearRenderTargetView(_canvasRtv, new Color4(0, 0, 0, 0));

        BindLayerPipeline();

        // Ortho: x 0..width, y 0..height (Y down)
        var vp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);

        // Pixelmap under layers when requested — keyed fades reveal it during the blend.
        if (overlayOutput && HasBackground)
            DrawTexturedQuad(0, 0, _backgroundWidth, _backgroundHeight, 0, 1f, _backgroundSrv!, useTexAlpha: true);

        var visible = layers.Where(l => l.IsEffectivelyVisible).OrderBy(l => l.ZIndex).ToList();
        var settings = reflection ?? FloorReflectionSettings.Default;
        var floor = zones?.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        var waveSettings = wave ?? FloorWaveSettings.Disabled;
        bool drawWave = waveSettings.Enabled && floor is not null && waveSettings.Opacity > 0.01f;
        var blobCfg = blobSettings ?? default;
        bool drawBlobs = blobSettings is { Enabled: true } && blobs is not null && floor is not null &&
                         (blobs.Blobs.Count > 0 || blobs.Trail.Count > 0);

        FloorMirrorSrv = null;
        FloorBlobSrv = null;

        // Floor stays black under mirror/LiDAR (canvas already cleared to black).
        foreach (var layer in visible.Where(FloorReflection.IsFloorLayer))
            DrawLayer(layer, resolveSrv, zones, bakeRoom3DRotation: false);

        if (settings.Enabled && floor is not null)
            DrawFloorReflections(visible, resolveSrv, zones!, floor, settings);

        if (drawBlobs)
            DrawFloorBlobs(blobs!, floor!, blobCfg);

        BindLayerPipeline();
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);
        foreach (var layer in visible.Where(l => !FloorReflection.IsFloorLayer(l)))
            DrawLayer(layer, resolveSrv, zones, bakeRoom3DRotation: false);

        if (drawWave)
            DrawFloorWave(floor!, zones!, waveSettings, drawBlobs ? blobs : null);

        // Unbind so NDI can sample the canvas as SRV and convert to UYVY.
        // Preview is emitted separately AFTER NDI (output-first).
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.PSSetShaderResource(0, null!);
    }

    /// <summary>
    /// Compose a 3D-only canvas. Layers are drawn exactly as on the 2D/NDI canvas
    /// (West ribbon intact). Room3DRotationDegrees is applied later on the mesh, not here.
    /// </summary>
    public void RenderRoomCanvas(
        IReadOnlyList<CompositionLayer> layers,
        Func<CompositionLayer, ID3D11ShaderResourceView?> resolveSrv,
        IReadOnlyList<ZoneDefinition>? zones = null)
    {
        EnsureRoomCanvas();
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_roomCanvasRtv!);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        ctx.ClearRenderTargetView(_roomCanvasRtv!, new Color4(0, 0, 0, 0));

        BindLayerPipeline();
        var vp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);

        // Same draw as output canvas — no Room3DRotation bake (keeps West continuous).
        var visible = layers.Where(l => l.IsEffectivelyVisible).OrderBy(l => l.ZIndex);
        foreach (var layer in visible)
            DrawLayer(layer, resolveSrv, zones, bakeRoom3DRotation: false);

        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.PSSetShaderResource(0, null!);
    }

    private void EnsureRoomCanvas()
    {
        if (_roomCanvasTexture is not null)
            return;

        _roomCanvasTexture = _gpu.CreateTexture(_canvasWidth, _canvasHeight);
        _roomCanvasRtv = _gpu.Device.CreateRenderTargetView(_roomCanvasTexture);
        _roomCanvasSrv = _gpu.Device.CreateShaderResourceView(_roomCanvasTexture);
    }

    /// <summary>
    /// UI preview blit/readback — call only after NDI SendFrame so output owns the GPU first.
    /// </summary>
    public void EmitPreview(bool overlayPreview, FloorBlobFrame? blobs = null)
    {
        var ctx = _gpu.Context;
        BindLayerPipeline();
        ctx.OMSetRenderTargets(_previewRtv);
        ctx.RSSetViewport(new Viewport(0, 0, PreviewWidth, PreviewHeight));
        ctx.ClearRenderTargetView(_previewRtv, new Color4(0, 0, 0, 1));
        var previewVp = Matrix4x4.CreateOrthographicOffCenter(0, PreviewWidth, PreviewHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(previewVp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);

        // Pixelmap first, then canvas with alpha — black-key holes (and fades) reveal the map.
        if (overlayPreview && HasBackground)
            DrawTexturedQuad(0, 0, PreviewWidth, PreviewHeight, 0, 1f, _backgroundSrv!, useTexAlpha: true);

        DrawTexturedQuad(0, 0, PreviewWidth, PreviewHeight, 0, 1f, _canvasSrv, sourcePremultiplied: true);

        if (blobs is not null && (blobs.DebugPoints.Count > 0 || blobs.DebugSensor is not null))
            DrawLidarDebugOverlay(blobs, previewVp);

        FinishPreviewEmit();
    }

    /// <summary>
    /// Custom 3D (or other) preview draw into the preview RT, then same readback path as <see cref="EmitPreview"/>.
    /// </summary>
    public void EmitCustomPreview(Action<ID3D11RenderTargetView, int, int> draw)
    {
        draw(_previewRtv, PreviewWidth, PreviewHeight);
        FinishPreviewEmit();
    }

    private void FinishPreviewEmit()
    {
        var ctx = _gpu.Context;
        var shared = _previewSharedMain[_previewWrite];
        if (shared is not null)
        {
            ctx.CopyResource(shared, _previewTexture);
            int next = (_previewWrite + 1) % PreviewSharedCount;
            Volatile.Write(ref _previewWrite, next);
            Interlocked.Increment(ref _previewCopies);
            _previewPackWake.Set();
        }

        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.PSSetShaderResource(0, null!);
    }

    private void DrawLayer(
        CompositionLayer layer,
        Func<CompositionLayer, ID3D11ShaderResourceView?> resolveSrv,
        IReadOnlyList<ZoneDefinition>? zones,
        bool bakeRoom3DRotation)
    {
        var srv = resolveSrv(layer);
        if (srv is null || layer.NativeWidth <= 0 || layer.NativeHeight <= 0)
            return;

        float threshold = Math.Clamp(layer.BlackKeyThreshold, 0f, 1f);
        float softness = Math.Max(threshold * 0.35f, 0.001f);

        float roomRot = bakeRoom3DRotation ? layer.Room3DRotationDegrees : 0f;
        float drawRot = layer.RotationDegrees + roomRot;
        drawRot %= 360f;
        if (drawRot < 0) drawRot += 360f;
        bool roomRotated = MathF.Abs(roomRot) > 0.01f;

        // With room rotation, draw the whole layer as one rotated quad (skip West ribbon slices).
        var ribbon = new List<WallRibbonDraw>();
        if (!roomRotated &&
            zones is not null &&
            WallCarousel.TryBuildRibbonDraws(layer, zones, ribbon))
        {
            foreach (var slice in ribbon)
            {
                DrawTexturedQuad(
                    slice.X, slice.Y, slice.Width, slice.Height, slice.RotationDegrees,
                    layer.EffectiveDrawOpacity, srv,
                    useTexAlpha: false,
                    blackKeyEnabled: layer.BlackKeyEnabled,
                    blackKeyThreshold: threshold,
                    blackKeySoftness: softness,
                    sourceUvMin: new Vector2(slice.U0, slice.V0),
                    sourceUvMax: new Vector2(slice.U1, slice.V1));
            }

            return;
        }

        var size = layer.GetDrawSize();
        var (u0, v0, u1, v1) = layer.GetCropUvRect();
        DrawTexturedQuad(
            layer.X, layer.Y, size.X, size.Y, drawRot, layer.EffectiveDrawOpacity, srv,
            useTexAlpha: false,
            blackKeyEnabled: layer.BlackKeyEnabled,
            blackKeyThreshold: threshold,
            blackKeySoftness: softness,
            sourceUvMin: new Vector2(u0, v0),
            sourceUvMax: new Vector2(u1, v1));
    }

    private void BindLayerPipeline()
    {
        var ctx = _gpu.Context;
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_blend);
        ctx.RSSetState(_rasterizer);
    }

    public bool TryGetPublishedPreview(out byte[] pixels)
    {
        int pub = Volatile.Read(ref _previewPublished);
        if (pub < 0 || pub > 1)
        {
            pixels = [];
            return false;
        }

        pixels = _previewCpu[pub];
        return true;
    }

    private void CreatePreviewReadback()
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
            throw new InvalidOperationException($"Preview readback D3D device failed: {hr}");

        _previewRbDevice = rbDevice;
        _previewRbContext = rbContext;
        var mt = rbDevice.QueryInterfaceOrNull<ID3D11Multithread>();
        mt?.SetMultithreadProtected(true);
        mt?.Dispose();

        for (int i = 0; i < PreviewSharedCount; i++)
        {
            _previewSharedMain[i] = _gpu.CreateTexture(
                PreviewWidth, PreviewHeight, Format.B8G8R8A8_UNorm,
                BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.Shared);

            using (var res = _previewSharedMain[i]!.QueryInterface<IDXGIResource>())
                _previewSharedRb[i] = _previewRbDevice.OpenSharedResource<ID3D11Texture2D>(res.SharedHandle);

            _previewRbStaging[i] = _previewRbDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)PreviewWidth,
                Height = (uint)PreviewHeight,
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

        _previewPackThread = new Thread(PreviewPackLoop)
        {
            IsBackground = true,
            Name = "PreviewReadback",
            // Below NDI pack/send — preview is best-effort.
            Priority = ThreadPriority.BelowNormal
        };
        _previewPackThread.Start();
    }

    private void PreviewPackLoop()
    {
        int lastWrite = -1;
        while (!_previewPackExit)
        {
            _previewPackWake.WaitOne(20);
            if (_previewPackExit || _previewRbContext is null)
                continue;
            if (Volatile.Read(ref _previewCopies) < 1)
                continue;

            int write = Volatile.Read(ref _previewWrite);
            if (write == lastWrite)
                continue;
            lastWrite = write;

            // Newest completed slot — not the 2-frame-old one (that added ~66 ms).
            int slot = (write + PreviewSharedCount - 1) % PreviewSharedCount;
            var shared = _previewSharedRb[slot];
            var staging = _previewRbStaging[slot];
            if (shared is null || staging is null)
                continue;

            _previewRbContext.CopyResource(staging, shared);
            var mapped = _previewRbContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int dest = Volatile.Read(ref _previewPublished) == 0 ? 1 : 0;
                var dst = _previewCpu[dest];
                int stride = PreviewWidth * 4;
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    for (int y = 0; y < PreviewHeight; y++)
                        Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), dst, y * stride, stride);
                }
                Volatile.Write(ref _previewPublished, dest);
                Interlocked.Increment(ref _previewGeneration);
            }
            finally
            {
                _previewRbContext.Unmap(staging, 0);
            }
            try { PreviewFrameReady?.Invoke(); } catch { /* UI may be gone */ }
        }
    }

    private void DisposePreviewReadback()
    {
        _previewPackExit = true;
        try { _previewPackWake.Set(); } catch { /* ignore */ }
        if (_previewPackThread is { IsAlive: true })
            _previewPackThread.Join(500);
        _previewPackThread = null;

        for (int i = 0; i < PreviewSharedCount; i++)
        {
            _previewRbStaging[i]?.Dispose();
            _previewRbStaging[i] = null;
            _previewSharedRb[i]?.Dispose();
            _previewSharedRb[i] = null;
            _previewSharedMain[i]?.Dispose();
            _previewSharedMain[i] = null;
        }

        _previewRbContext?.Dispose();
        _previewRbContext = null;
        _previewRbDevice?.Dispose();
        _previewRbDevice = null;
        _previewPackWake.Dispose();
    }

    private void DrawFloorReflections(
        IReadOnlyList<CompositionLayer> layers,
        Func<CompositionLayer, ID3D11ShaderResourceView?> resolveSrv,
        IReadOnlyList<ZoneDefinition> zones,
        ZoneDefinition floor,
        FloorReflectionSettings settings)
    {
        var nord = zones.FirstOrDefault(z => z.Id.Equals("wall_nord", StringComparison.OrdinalIgnoreCase));
        EnsureReflectTargets((int)MathF.Ceiling(floor.Width), (int)MathF.Ceiling(floor.Height));
        if (_reflectRtv is null || _reflectSrv is null || _reflectTex is null)
            return;

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_reflectRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _reflectW, _reflectH));
        ctx.ClearRenderTargetView(_reflectRtv, new Color4(0, 0, 0, 0));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _reflectVb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_reflectVs);
        ctx.PSSetShader(_reflectPs);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_blend);
        ctx.RSSetState(_rasterizer);

        var rtVp = Matrix4x4.CreateOrthographicOffCenter(0, _reflectW, _reflectH, 0, 0, 1);
        var vanishing = new Vector2(floor.X + floor.Width * 0.5f, floor.Y + floor.Height * 0.5f);
        float opacityScale = Math.Clamp(settings.Opacity, 0f, 1f);

        foreach (var layer in layers)
        {
            if (FloorReflection.IsFloorLayer(layer) || layer.EffectiveDrawOpacity < 0.02f)
                continue;
            var srv = resolveSrv(layer);
            if (srv is null)
                continue;

            foreach (var quad in FloorReflection.Build(layer, floor, nord, settings.Length, settings.Angle, zones))
            {
                var p0 = FloorReflection.ToRt(quad.C0, floor, _reflectW, _reflectH);
                var p1 = FloorReflection.ToRt(quad.C1, floor, _reflectW, _reflectH);
                var p2 = FloorReflection.ToRt(quad.C2, floor, _reflectW, _reflectH);
                var p3 = FloorReflection.ToRt(quad.C3, floor, _reflectW, _reflectH);

                var verts = new Vertex[]
                {
                    new() { Position = new Vector3(p0.X, p0.Y, 0), TexCoord = quad.Uv0 },
                    new() { Position = new Vector3(p1.X, p1.Y, 0), TexCoord = quad.Uv1 },
                    new() { Position = new Vector3(p2.X, p2.Y, 0), TexCoord = quad.Uv2 },
                    new() { Position = new Vector3(p3.X, p3.Y, 0), TexCoord = quad.Uv3 },
                };
                WriteReflectVertices(verts);

                var cb = new ReflectConstants
                {
                    ViewProjection = Matrix4x4.Transpose(rtVp),
                    Color = new Vector4(1, 1, 1, opacityScale * quad.LayerOpacity),
                    FloorPos = new Vector2(floor.X, floor.Y),
                    FloorSize = new Vector2(floor.Width, floor.Height),
                    Vanishing = vanishing,
                    RtSize = new Vector2(_reflectW, _reflectH),
                    MiterA = quad.MiterA,
                    MiterB = quad.MiterB,
                    FarA = quad.CutA,
                    FarB = quad.CutB,
                    LengthPx = quad.LengthPx,
                    FadeStart = Math.Clamp(settings.FadeStart, 0f, 1f),
                    SideFade = new Vector2(quad.SideFadeA, quad.SideFadeB),
                    BlackKeyEnabled = layer.BlackKeyEnabled ? 1f : 0f,
                    BlackKeyThreshold = Math.Clamp(layer.BlackKeyThreshold, 0f, 1f),
                    BlackKeySoftness = Math.Max(Math.Clamp(layer.BlackKeyThreshold, 0f, 1f) * 0.35f, 0.001f)
                };
                ctx.UpdateSubresource(cb, _reflectCb);
                ctx.VSSetConstantBuffer(0, _reflectCb);
                ctx.PSSetConstantBuffer(0, _reflectCb);
                ctx.PSSetShaderResource(0, srv);
                ctx.Draw(4, 0);
            }
        }

        ID3D11ShaderResourceView compositeSrv = _reflectSrv;
        if (settings.BlurPixels > 0.4f && _blurRtv is not null && _blurSrv is not null)
        {
            float rtBlur = settings.BlurPixels * (_reflectW / MathF.Max(floor.Width, 1f));
            BlurPass(_reflectSrv, _blurRtv, new Vector2(1, 0), rtBlur);
            BlurPass(_blurSrv, _reflectRtv, new Vector2(0, 1), rtBlur);
            compositeSrv = _reflectSrv;
        }

        BindLayerPipeline();
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        var canvasVp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(canvasVp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);
        DrawTexturedQuad(floor.X, floor.Y, floor.Width, floor.Height, 0, 1f, compositeSrv, useTexAlpha: true);
        ctx.PSSetShaderResource(0, null!);
        FloorMirrorSrv = compositeSrv;
    }

    private void DrawFloorWave(
        ZoneDefinition floor,
        IReadOnlyList<ZoneDefinition> zones,
        FloorWaveSettings settings,
        FloorBlobFrame? blobs)
    {
        int fw = Math.Max(1, (int)MathF.Round(floor.Width));
        int fh = Math.Max(1, (int)MathF.Round(floor.Height));
        EnsureWaveTarget(fw, fh);
        if (_waveRtv is null || _waveSrv is null)
            return;

        var stage = zones.FirstOrDefault(z => z.Id.Equals("wall_stage", StringComparison.OrdinalIgnoreCase));
        float stageV0 = 0f;
        float stageV1 = 1f;
        if (stage is not null && floor.Height > 1f)
        {
            stageV0 = Math.Clamp((stage.Y - floor.Y) / floor.Height, 0f, 1f);
            stageV1 = Math.Clamp((stage.Y + stage.Height - floor.Y) / floor.Height, 0f, 1f);
            if (stageV1 < stageV0)
                (stageV0, stageV1) = (stageV1, stageV0);
        }

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        ctx.PSSetShaderResource(0, null!);

        ctx.OMSetRenderTargets(_waveRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _waveW, _waveH));
        ctx.ClearRenderTargetView(_waveRtv, new Color4(0, 0, 0, 1));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_wavePs);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_blend);
        ctx.RSSetState(_rasterizer);

        var rtVp = Matrix4x4.CreateOrthographicOffCenter(0, _waveW, _waveH, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(rtVp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);

        var world = Matrix4x4.CreateScale(_waveW, _waveH, 1);
        ctx.UpdateSubresource(new LayerConstants
        {
            World = Matrix4x4.Transpose(world),
            Color = new Vector4(1, 1, 1, 1),
            SourceUvMin = Vector2.Zero,
            SourceUvMax = Vector2.One
        }, _layerCb);

        FloorReflection.GetWestFloorKinks(floor, out var westA, out var westB, zones);
        var waveCb = new WaveConstants
        {
            FloorPos = new Vector2(floor.X, floor.Y),
            FloorSize = new Vector2(floor.Width, floor.Height),
            CanvasSize = new Vector2(_canvasWidth, _canvasHeight),
            StageV = new Vector2(stageV0, stageV1),
            Progress = Math.Clamp(settings.Progress, 0f, 1f),
            Amplitude = Math.Clamp(settings.Amplitude, 0f, 2f),
            Wavelength = Math.Clamp(settings.Wavelength, 0.04f, 0.8f),
            Opacity = Math.Clamp(settings.Opacity, 0f, 1f),
            WestKinkA = westA,
            WestKinkB = westB
        };
        PackWaveBlobs(ref waveCb, floor, blobs);
        ctx.UpdateSubresource(waveCb, _waveCb);
        ctx.PSSetConstantBuffer(2, _waveCb);
        ctx.PSSetShaderResource(0, _canvasSrv);
        ctx.Draw(4, 0);
        ctx.PSSetShaderResource(0, null!);

        BindLayerPipeline();
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        var canvasVp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(canvasVp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);
        DrawTexturedQuad(floor.X, floor.Y, floor.Width, floor.Height, 0, 1f, _waveSrv);
    }

    private static void PackWaveBlobs(ref WaveConstants cb, ZoneDefinition floor, FloorBlobFrame? blobs)
    {
        if (blobs is null || floor.Width < 1f || floor.Height < 1f)
            return;

        Span<Vector4> packed = stackalloc Vector4[8];
        int n = 0;
        foreach (var blob in blobs.Blobs)
        {
            if (n >= 8)
                break;
            float u = (blob.Center.X - floor.X) / floor.Width;
            float v = (blob.Center.Y - floor.Y) / floor.Height;
            if (u < -0.08f || u > 1.08f || v < -0.08f || v > 1.08f)
                continue;
            packed[n++] = new Vector4(
                u,
                v,
                MathF.Max(blob.RadiusPx / floor.Width, 0.008f),
                Math.Clamp(blob.Alpha, 0.15f, 1f));
        }

        cb.BlobCount = n;
        if (n > 0) cb.Blob0 = packed[0];
        if (n > 1) cb.Blob1 = packed[1];
        if (n > 2) cb.Blob2 = packed[2];
        if (n > 3) cb.Blob3 = packed[3];
        if (n > 4) cb.Blob4 = packed[4];
        if (n > 5) cb.Blob5 = packed[5];
        if (n > 6) cb.Blob6 = packed[6];
        if (n > 7) cb.Blob7 = packed[7];
    }

    private void DrawFloorBlobs(FloorBlobFrame frame, ZoneDefinition floor, FloorBlobSettings settings)
    {
        EnsureBlobTargets((int)MathF.Ceiling(floor.Width), (int)MathF.Ceiling(floor.Height));
        if (_blobRtv is null || _blobSrv is null)
            return;

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_blobRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _reflectW, _reflectH));
        ctx.ClearRenderTargetView(_blobRtv, new Color4(0, 0, 0, 0));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_blobVs);
        ctx.PSSetShader(_blobPs);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_maxBlend);
        ctx.RSSetState(_rasterizer);

        var rtVp = Matrix4x4.CreateOrthographicOffCenter(0, _reflectW, _reflectH, 0, 0, 1);
        float sx = _reflectW / MathF.Max(floor.Width, 1f);
        float sy = _reflectH / MathF.Max(floor.Height, 1f);

        foreach (var blob in frame.Trail)
            DrawBlobQuad(blob, floor, sx, sy, rtVp, settings, trail: true);

        foreach (var blob in frame.Blobs)
            DrawBlobQuad(blob, floor, sx, sy, rtVp, settings, trail: false);

        ID3D11ShaderResourceView compositeSrv = _blobSrv;
        if (settings.BlurPixels > 0.4f && _blurRtv is not null && _blurSrv is not null)
        {
            float rtBlur = settings.BlurPixels * (_reflectW / MathF.Max(floor.Width, 1f));
            BlurPass(_blobSrv, _blurRtv, new Vector2(1, 0), rtBlur);
            BlurPass(_blurSrv, _blobRtv, new Vector2(0, 1), rtBlur);
            compositeSrv = _blobSrv;
        }

        BindLayerPipeline();
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        var canvasVp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(canvasVp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);
        DrawTexturedQuad(floor.X, floor.Y, floor.Width, floor.Height, 0, settings.Opacity, compositeSrv, useTexAlpha: true);
        FloorBlobSrv = compositeSrv;
    }

    private void DrawBlobQuad(
        FloorBlob blob,
        ZoneDefinition floor,
        float sx,
        float sy,
        Matrix4x4 rtVp,
        FloorBlobSettings settings,
        bool trail)
    {
        float cx = (blob.Center.X - floor.X) * sx;
        float cy = (blob.Center.Y - floor.Y) * sy;
        // Leave room for the expanding location ring past the solid disc.
        float radius = blob.RadiusPx * ((sx + sy) * 0.5f);
        float extent = radius * (trail ? 1.05f : 1.35f);
        float size = extent * 2f;
        var color = blob.Color ?? settings.Color;
        color.W = blob.Alpha * (trail ? 0.45f : 1f);

        // ~1.4 s location-style pulse, second ring offset by half a cycle in the shader.
        float pulse = trail
            ? -1f
            : (float)((Environment.TickCount64 % 1400L) / 1400.0);

        var world = Matrix4x4.CreateScale(size, size, 1) *
                    Matrix4x4.CreateTranslation(cx - extent, cy - extent, 0);

        var cb = new BlobConstants
        {
            ViewProjection = Matrix4x4.Transpose(rtVp),
            Color = color,
            Center = new Vector2(cx, cy),
            Radius = MathF.Max(radius, 1f),
            PulsePhase = pulse,
            RingWidth = MathF.Max(radius * 0.045f, 1.2f)
        };

        _gpu.Context.UpdateSubresource(cb, _blobCb);
        _gpu.Context.VSSetConstantBuffer(0, _blobCb);
        _gpu.Context.PSSetConstantBuffer(0, _blobCb);

        var layer = new LayerConstants
        {
            World = Matrix4x4.Transpose(world),
            Color = new Vector4(1, 1, 1, 1)
        };
        _gpu.Context.UpdateSubresource(layer, _layerCb);
        _gpu.Context.VSSetConstantBuffer(1, _layerCb);
        _gpu.Context.Draw(4, 0);
    }

    private void DrawLidarDebugOverlay(FloorBlobFrame frame, Matrix4x4 previewVp)
    {
        float scale = PreviewWidth / (float)Math.Max(_canvasWidth, 1);

        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_previewRtv);
        ctx.RSSetViewport(new Viewport(0, 0, PreviewWidth, PreviewHeight));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_blobVs);
        ctx.PSSetShader(_blobPs);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_blend);
        ctx.RSSetState(_rasterizer);

        foreach (var pt in frame.DebugPoints)
        {
            DrawDebugMarker(pt, scale, previewVp, new Vector4(0.2f, 1f, 0.4f, 0.85f), 3f);
        }

        if (frame.DebugSensor is Vector2 sensor)
            DrawDebugMarker(sensor, scale, previewVp, new Vector4(1f, 0.25f, 0.25f, 1f), 10f);
    }

    private void DrawDebugMarker(Vector2 canvasPos, float scale, Matrix4x4 previewVp, Vector4 color, float radiusPx)
    {
        float cx = canvasPos.X * scale;
        float cy = canvasPos.Y * scale;
        float radius = radiusPx;
        float size = radius * 2f;

        var world = Matrix4x4.CreateScale(size, size, 1) *
                    Matrix4x4.CreateTranslation(cx - radius, cy - radius, 0);

        var cb = new BlobConstants
        {
            ViewProjection = Matrix4x4.Transpose(previewVp),
            Color = color,
            Center = new Vector2(cx, cy),
            Radius = radius,
            PulsePhase = -1f,
            RingWidth = 1.5f
        };

        _gpu.Context.UpdateSubresource(cb, _blobCb);
        _gpu.Context.VSSetConstantBuffer(0, _blobCb);
        _gpu.Context.PSSetConstantBuffer(0, _blobCb);

        var layer = new LayerConstants
        {
            World = Matrix4x4.Transpose(world),
            Color = new Vector4(1, 1, 1, 1)
        };
        _gpu.Context.UpdateSubresource(layer, _layerCb);
        _gpu.Context.VSSetConstantBuffer(1, _layerCb);
        _gpu.Context.Draw(4, 0);
    }

    private void BlurPass(ID3D11ShaderResourceView source, ID3D11RenderTargetView dest, Vector2 direction, float radius)
    {
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(dest);
        ctx.RSSetViewport(new Viewport(0, 0, _reflectW, _reflectH));
        ctx.ClearRenderTargetView(dest, new Color4(0, 0, 0, 0));
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_blurPs);
        ctx.PSSetSampler(0, _borderSampler);
        ctx.OMSetBlendState(_blend);

        var vp = Matrix4x4.CreateOrthographicOffCenter(0, _reflectW, _reflectH, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);

        var world = Matrix4x4.CreateScale(_reflectW, _reflectH, 1);
        ctx.UpdateSubresource(new LayerConstants
        {
            World = Matrix4x4.Transpose(world),
            Color = new Vector4(1, 1, 1, 1),
            SourceUvMin = Vector2.Zero,
            SourceUvMax = Vector2.One
        }, _layerCb);

        ctx.UpdateSubresource(new BlurConstants
        {
            TexelSize = new Vector2(1f / _reflectW, 1f / _reflectH),
            Direction = direction,
            Radius = MathF.Max(radius, 0.5f)
        }, _blurCb);
        ctx.PSSetConstantBuffer(2, _blurCb);
        ctx.PSSetShaderResource(0, source);
        ctx.Draw(4, 0);
        ctx.PSSetShaderResource(0, null!);
    }

    private void WriteReflectVertices(Vertex[] verts)
    {
        var mapped = _gpu.Context.Map(_reflectVb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                var dest = (Vertex*)mapped.DataPointer;
                dest[0] = verts[0];
                dest[1] = verts[1];
                dest[2] = verts[2];
                dest[3] = verts[3];
            }
        }
        finally
        {
            _gpu.Context.Unmap(_reflectVb, 0);
        }
    }

    private void EnsureBlobTargets(int floorW, int floorH)
    {
        EnsureReflectTargets(floorW, floorH);
        if (_reflectW <= 0 || _reflectH <= 0)
            return;

        if (_blobTex is not null)
            return;

        _blobTex = _gpu.CreateTexture(_reflectW, _reflectH);
        _blobRtv = _gpu.Device.CreateRenderTargetView(_blobTex);
        _blobSrv = _gpu.Device.CreateShaderResourceView(_blobTex);
    }

    private void DisposeBlobTargets()
    {
        _blobSrv?.Dispose();
        _blobRtv?.Dispose();
        _blobTex?.Dispose();
        _blobSrv = null;
        _blobRtv = null;
        _blobTex = null;
    }

    private void EnsureReflectTargets(int floorW, int floorH)
    {
        int w = Math.Max(1, floorW / 2);
        int h = Math.Max(1, floorH / 2);
        if (w == _reflectW && h == _reflectH && _reflectTex is not null)
            return;

        DisposeReflectTargets();
        _reflectW = w;
        _reflectH = h;
        _reflectTex = _gpu.CreateTexture(w, h);
        _reflectRtv = _gpu.Device.CreateRenderTargetView(_reflectTex);
        _reflectSrv = _gpu.Device.CreateShaderResourceView(_reflectTex);
        _blurTex = _gpu.CreateTexture(w, h);
        _blurRtv = _gpu.Device.CreateRenderTargetView(_blurTex);
        _blurSrv = _gpu.Device.CreateShaderResourceView(_blurTex);
    }

    private void DisposeReflectTargets()
    {
        DisposeBlobTargets();
        _blurSrv?.Dispose();
        _blurRtv?.Dispose();
        _blurTex?.Dispose();
        _reflectSrv?.Dispose();
        _reflectRtv?.Dispose();
        _reflectTex?.Dispose();
        _blurSrv = null;
        _blurRtv = null;
        _blurTex = null;
        _reflectSrv = null;
        _reflectRtv = null;
        _reflectTex = null;
    }

    private void EnsureWaveTarget(int floorW, int floorH)
    {
        int w = Math.Max(1, floorW);
        int h = Math.Max(1, floorH);
        if (w == _waveW && h == _waveH && _waveTex is not null)
            return;

        DisposeWaveTarget();
        _waveW = w;
        _waveH = h;
        _waveTex = _gpu.CreateTexture(w, h);
        _waveRtv = _gpu.Device.CreateRenderTargetView(_waveTex);
        _waveSrv = _gpu.Device.CreateShaderResourceView(_waveTex);
    }

    private void DisposeWaveTarget()
    {
        _waveSrv?.Dispose();
        _waveRtv?.Dispose();
        _waveTex?.Dispose();
        _waveSrv = null;
        _waveRtv = null;
        _waveTex = null;
        _waveW = 0;
        _waveH = 0;
    }

    private void DrawTexturedQuad(
        float x, float y, float w, float h, float rotationDeg, float opacity,
        ID3D11ShaderResourceView srv,
        bool useTexAlpha = false,
        bool blackKeyEnabled = false,
        float blackKeyThreshold = 0.08f,
        float blackKeySoftness = 0.03f,
        Vector2? sourceUvMin = null,
        Vector2? sourceUvMax = null,
        bool sourcePremultiplied = false)
    {
        var world =
            Matrix4x4.CreateScale(w, h, 1) *
            Matrix4x4.CreateRotationZ(rotationDeg * MathF.PI / 180f) *
            Matrix4x4.CreateTranslation(x, y, 0);

        // Rotate around quad center
        if (MathF.Abs(rotationDeg) > 0.01f)
        {
            var toOrigin = Matrix4x4.CreateTranslation(-0.5f, -0.5f, 0);
            var scale = Matrix4x4.CreateScale(w, h, 1);
            var rot = Matrix4x4.CreateRotationZ(rotationDeg * MathF.PI / 180f);
            var toPos = Matrix4x4.CreateTranslation(x + w * 0.5f, y + h * 0.5f, 0);
            world = toOrigin * scale * rot * toPos;
        }

        var layer = new LayerConstants
        {
            World = Matrix4x4.Transpose(world),
            Color = new Vector4(1, 1, 1, Math.Clamp(opacity, 0, 1)),
            UseTexAlpha = useTexAlpha ? 1f : 0f,
            BlackKeyEnabled = blackKeyEnabled ? 1f : 0f,
            BlackKeyThreshold = blackKeyThreshold,
            BlackKeySoftness = blackKeySoftness,
            SourcePremultiplied = sourcePremultiplied ? 1f : 0f,
            SourceUvMin = sourceUvMin ?? Vector2.Zero,
            SourceUvMax = sourceUvMax ?? Vector2.One
        };
        _gpu.Context.UpdateSubresource(layer, _layerCb);
        _gpu.Context.PSSetShaderResource(0, srv);
        _gpu.Context.Draw(4, 0);
    }

    private ID3D11Buffer CreateConstantBuffer(int size)
    {
        int aligned = (size + 15) / 16 * 16;
        return _gpu.Device.CreateBuffer((uint)aligned, BindFlags.ConstantBuffer, ResourceUsage.Default);
    }

    private void CompileShaders(out ID3D11VertexShader vs, out ID3D11PixelShader ps, out ID3D11InputLayout layout)
    {
        const string hlsl = """
            cbuffer FrameCB : register(b0) { float4x4 ViewProjection; };
            cbuffer LayerCB : register(b1) {
                float4x4 World;
                float4 Color;
                float UseTexAlpha;
                float BlackKeyEnabled;
                float BlackKeyThreshold;
                float BlackKeySoftness;
                float SourcePremultiplied;
                float Pad0;
                float Pad1;
                float Pad2;
                float2 SourceUvMin;
                float2 SourceUvMax;
            };
            Texture2D tex : register(t0);
            SamplerState samp : register(s0);

            struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            VSOut VSMain(VSIn i) {
                VSOut o;
                float4 wp = mul(float4(i.pos, 1), World);
                o.pos = mul(wp, ViewProjection);
                o.uv = lerp(SourceUvMin, SourceUvMax, i.uv);
                return o;
            }

            float4 PSMain(VSOut i) : SV_Target {
                float4 c = tex.Sample(samp, i.uv);
                // Already-premultiplied blit (compose canvas → preview): keep rgb, scale by Color.a.
                if (SourcePremultiplied > 0.5) {
                    float a = c.a * Color.a;
                    return float4(c.rgb * Color.rgb * Color.a, a);
                }
                float a = UseTexAlpha > 0.5 ? c.a * Color.a : Color.a;
                if (BlackKeyEnabled > 0.5) {
                    float luma = max(c.r, max(c.g, c.b));
                    float soft = max(BlackKeySoftness, 0.001);
                    a *= smoothstep(BlackKeyThreshold, BlackKeyThreshold + soft, luma);
                }
                // Premultiplied RGB so opacity fades don't darken black-keyed regions.
                float3 rgb = c.rgb * Color.rgb * a;
                return float4(rgb, a);
            }
            """;

        var vsBlob = Compiler.Compile(hlsl, "VSMain", "shader.hlsl", "vs_4_0");
        var psBlob = Compiler.Compile(hlsl, "PSMain", "shader.hlsl", "ps_4_0");

        vs = _gpu.Device.CreateVertexShader(vsBlob.Span);
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);

        layout = _gpu.Device.CreateInputLayout([
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
        ], vsBlob.Span);
    }

    private void CompileReflectShaders(out ID3D11VertexShader vs, out ID3D11PixelShader ps)
    {
        const string hlsl = """
            cbuffer ReflectCB : register(b0) {
                float4x4 ViewProjection;
                float4 Color;
                float2 FloorPos;
                float2 FloorSize;
                float2 Vanishing;
                float2 RtSize;
                float2 MiterA;
                float2 MiterB;
                float2 FarA;
                float2 FarB;
                float LengthPx;
                float FadeStart;
                float2 SideFade;
                float BlackKeyEnabled;
                float BlackKeyThreshold;
                float BlackKeySoftness;
                float PadKey;
            };
            Texture2D tex : register(t0);
            SamplerState samp : register(s0);

            struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 canvas : TEXCOORD1; };

            VSOut VSMain(VSIn i) {
                VSOut o;
                o.pos = mul(float4(i.pos, 1), ViewProjection);
                o.uv = i.uv;
                float2 rt = max(RtSize, float2(1, 1));
                o.canvas = FloorPos + (i.pos.xy / rt) * FloorSize;
                return o;
            }

            float side(float2 a, float2 b, float2 p) {
                return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            }

            bool sameSide(float2 a, float2 b, float2 p, float2 refp) {
                return side(a, b, p) * side(a, b, refp) >= 0;
            }

            // Distance of p from the cut line a→b, positive on the side refp is on.
            float cutDistance(float2 a, float2 b, float2 p, float2 refp) {
                float2 e = b - a;
                float len = length(e);
                if (len < 0.001) return 1e9;
                float2 n = float2(-e.y, e.x) / len;
                float d = dot(p - a, n);
                return dot(refp - a, n) < 0 ? -d : d;
            }

            float4 PSMain(VSOut i) : SV_Target {
                float2 p = i.canvas;
                float2 floorMax = FloorPos + FloorSize;
                if (p.x < FloorPos.x || p.x > floorMax.x || p.y < FloorPos.y || p.y > floorMax.y)
                    clip(-1);

                if (!sameSide(MiterA, MiterB, p, FarA)) clip(-1);
                if (!sameSide(MiterA, FarA, p, MiterB)) clip(-1);
                if (!sameSide(MiterB, FarB, p, MiterA)) clip(-1);

                float2 ab = MiterB - MiterA;
                float2 n = float2(-ab.y, ab.x);
                float nlen = length(n);
                float dist = nlen > 0.001 ? abs(dot(p - MiterA, n / nlen)) : 0;

                float fadeT = saturate(dist / max(LengthPx, 1.0));
                float start = saturate(FadeStart);
                float fade = 1.0 - saturate((fadeT - start) / max(1.0 - start, 0.001));
                fade *= fade;

                if (SideFade.x > 0.5)
                    fade *= smoothstep(0.0, SideFade.x, cutDistance(MiterA, FarA, p, MiterB));
                if (SideFade.y > 0.5)
                    fade *= smoothstep(0.0, SideFade.y, cutDistance(MiterB, FarB, p, MiterA));

                float alpha = Color.a * fade;
                if (alpha < 0.004) clip(-1);

                float4 c = tex.Sample(samp, i.uv);
                if (BlackKeyEnabled > 0.5) {
                    float luma = max(c.r, max(c.g, c.b));
                    float soft = max(BlackKeySoftness, 0.001);
                    alpha *= smoothstep(BlackKeyThreshold, BlackKeyThreshold + soft, luma);
                    if (alpha < 0.004) clip(-1);
                }
                float3 rgb = c.rgb * Color.rgb * alpha;
                return float4(rgb, alpha);
            }
            """;

        var vsBlob = Compiler.Compile(hlsl, "VSMain", "reflect.hlsl", "vs_4_0");
        var psBlob = Compiler.Compile(hlsl, "PSMain", "reflect.hlsl", "ps_4_0");
        vs = _gpu.Device.CreateVertexShader(vsBlob.Span);
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);
    }

    private void CompileBlurShader(out ID3D11PixelShader ps)
    {
        const string hlsl = """
            cbuffer FrameCB : register(b0) { float4x4 ViewProjection; };
            cbuffer LayerCB : register(b1) { float4x4 World; float4 Color; };
            cbuffer BlurCB : register(b2) {
                float2 TexelSize;
                float2 Direction;
                float Radius;
                float3 Pad;
            };
            Texture2D tex : register(t0);
            SamplerState samp : register(s0);

            struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            VSOut VSMain(VSIn i) {
                VSOut o;
                float4 wp = mul(float4(i.pos, 1), World);
                o.pos = mul(wp, ViewProjection);
                o.uv = i.uv;
                return o;
            }

            float4 PSMain(VSOut i) : SV_Target {
                float2 dir = Direction * TexelSize * max(Radius, 0.5);
                float w0 = 0.227027;
                float w1 = 0.1945946;
                float w2 = 0.1216216;
                float w3 = 0.054054;
                float w4 = 0.016216;
                float4 c = tex.Sample(samp, i.uv) * w0;
                c += tex.Sample(samp, i.uv + dir) * w1;
                c += tex.Sample(samp, i.uv - dir) * w1;
                c += tex.Sample(samp, i.uv + dir * 2) * w2;
                c += tex.Sample(samp, i.uv - dir * 2) * w2;
                c += tex.Sample(samp, i.uv + dir * 3) * w3;
                c += tex.Sample(samp, i.uv - dir * 3) * w3;
                c += tex.Sample(samp, i.uv + dir * 4) * w4;
                c += tex.Sample(samp, i.uv - dir * 4) * w4;
                return c;
            }
            """;

        var psBlob = Compiler.Compile(hlsl, "PSMain", "blur.hlsl", "ps_4_0");
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);
    }

    private void CompileBlobShader(out ID3D11VertexShader vs, out ID3D11PixelShader ps)
    {
        const string hlsl = """
            cbuffer BlobCB : register(b0) {
                float4x4 ViewProjection;
                float4 Color;
                float2 Center;
                float Radius;
                float PulsePhase;
                float RingWidth;
                float Pad0;
            };
            cbuffer LayerCB : register(b1) { float4x4 World; float4 LayerColor; };

            struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut { float4 pos : SV_POSITION; float2 world : TEXCOORD0; };

            VSOut VSMain(VSIn i) {
                VSOut o;
                float4 wp = mul(float4(i.pos, 1), World);
                o.pos = mul(wp, ViewProjection);
                o.world = wp.xy;
                return o;
            }

            float Ring(float d, float ringR, float width) {
                float x = (d - ringR) / max(width, 1e-4);
                return exp(-x * x);
            }

            float4 PSMain(VSOut i) : SV_Target {
                float d = length(i.world - Center) / max(Radius, 1e-4);

                // Solid location core.
                float core = 1.0 - smoothstep(0.10, 0.22, d);

                // Soft filled disc under the core.
                float fill = (1.0 - smoothstep(0.18, 0.72, d)) * 0.28;

                // Crisp static rim (the "Ränder").
                float rim = Ring(d, 0.58, 0.035) * 0.95;

                float pulse = 0.0;
                if (PulsePhase >= 0.0) {
                    // Two out-of-phase expanding rings, fading as they grow.
                    float p1 = PulsePhase;
                    float p2 = frac(PulsePhase + 0.5);
                    float r1 = lerp(0.22, 1.20, p1);
                    float r2 = lerp(0.22, 1.20, p2);
                    float w = max(RingWidth / max(Radius, 1.0), 0.028);
                    pulse += Ring(d, r1, w) * (1.0 - p1) * 0.95;
                    pulse += Ring(d, r2, w) * (1.0 - p2) * 0.55;
                }

                float a = saturate(core + fill + rim + pulse) * Color.a;
                // Brighten the core / rings slightly so edges read clearly on dark floors.
                float3 rgb = Color.rgb * lerp(0.85, 1.15, saturate(core + rim + pulse));
                return float4(rgb * a, a);
            }
            """;

        var vsBlob = Compiler.Compile(hlsl, "VSMain", "blob.hlsl", "vs_4_0");
        var psBlob = Compiler.Compile(hlsl, "PSMain", "blob.hlsl", "ps_4_0");
        vs = _gpu.Device.CreateVertexShader(vsBlob.Span);
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);
    }

    private void CompileWaveShader(out ID3D11PixelShader ps)
    {
        const string hlsl = """
            cbuffer WaveCB : register(b2) {
                float2 FloorPos;
                float2 FloorSize;
                float2 CanvasSize;
                float2 StageV;
                float Progress;
                float Amplitude;
                float Wavelength;
                float Opacity;
                float2 WestKinkA;
                float2 WestKinkB;
                float BlobCount;
                float3 BlobPad;
                float4 Blobs[8];
            };
            Texture2D tex : register(t0);
            SamplerState samp : register(s0);

            struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            float DistToStage(float2 uv, out float2 dirUv) {
                float2 p = uv * FloorSize;
                float2 a = float2(FloorSize.x, StageV.x * FloorSize.y);
                float2 b = float2(FloorSize.x, StageV.y * FloorSize.y);
                float2 ba = b - a;
                float h = saturate(dot(p - a, ba) / max(dot(ba, ba), 1.0));
                float2 closest = a + ba * h;
                float2 d = p - closest;
                float len = length(d);
                float2 n = len > 1e-3 ? d / len : float2(-1, 0);
                dirUv = float2(n.x / max(FloorSize.x, 1.0), n.y / max(FloorSize.y, 1.0));
                return len / max(FloorSize.x, 1.0);
            }

            float Hash21(float2 p) {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float FoamNoise(float2 uv, float t) {
                float n1 = Hash21(uv * float2(92.0, 54.0) + t * 14.0);
                float n2 = Hash21(uv * float2(41.0, 73.0) - t * 9.0);
                return saturate(n1 * n2 * 1.8);
            }

            float DistToSeg(float2 p, float2 a, float2 b) {
                float2 ba = b - a;
                float t = saturate(dot(p - a, ba) / max(dot(ba, ba), 1.0));
                return length(p - (a + ba * t));
            }

            float2 SegInwardUv(float2 a, float2 b) {
                float2 ba = b - a;
                float2 n = float2(-ba.y, ba.x);
                if (n.y < 0.0) n = -n;
                float len = length(n);
                return len > 1e-5 ? n / len : float2(0, 1);
            }

            float WestEdgeV(float u) {
                if (u <= WestKinkA.x) return WestKinkA.y;
                if (u >= WestKinkB.x) return WestKinkB.y;
                float t = (u - WestKinkA.x) / max(WestKinkB.x - WestKinkA.x, 1e-5);
                return lerp(WestKinkA.y, WestKinkB.y, t);
            }

            void NearestWall(float2 uv, out float wallN, out float2 inward) {
                float2 p = uv * FloorSize;
                float2 w0 = float2(0.0, WestKinkA.y * FloorSize.y);
                float2 w1 = WestKinkA * FloorSize;
                float2 w2 = WestKinkB * FloorSize;
                float2 w3 = float2(FloorSize.x, WestKinkB.y * FloorSize.y);

                float dW01 = DistToSeg(p, w0, w1);
                float dW12 = DistToSeg(p, w1, w2);
                float dW23 = DistToSeg(p, w2, w3);
                float dWest = min(dW01, min(dW12, dW23));
                float dSud = p.x;
                float dOst = FloorSize.y - p.y;
                float wallPx = min(dSud, min(dWest, dOst));
                wallN = wallPx / max(FloorSize.x, 1.0);

                if (dSud <= dWest && dSud <= dOst)
                    inward = float2(1, 0);
                else if (dWest <= dOst)
                {
                    if (dW12 <= dW01 && dW12 <= dW23)
                        inward = SegInwardUv(WestKinkA, WestKinkB);
                    else if (dW23 <= dW01)
                        inward = SegInwardUv(WestKinkB, float2(1.0, WestKinkB.y));
                    else
                        inward = float2(0, 1);
                }
                else
                    inward = float2(0, -1);
            }

            float BlobField(float2 uv, out float2 away, out float blobR) {
                away = float2(0, 0);
                blobR = 0.02;
                float field = 0.0;
                float best = 1e6;
                [unroll]
                for (int i = 0; i < 8; i++) {
                    if (i >= (int)BlobCount) continue;
                    float2 b = Blobs[i].xy;
                    float r = max(Blobs[i].z, 0.008);
                    float a = Blobs[i].w;
                    float2 dpx = (uv - b) * FloorSize;
                    float d = length(dpx) / max(FloorSize.x, 1.0);
                    float inf = a * (1.0 - smoothstep(r * 0.55, r * 3.2, d));
                    field = max(field, inf);
                    if (d < best) {
                        best = d;
                        blobR = r;
                        float2 n = uv - b;
                        float len = length(n);
                        away = len > 1e-5 ? n / len : float2(0, 1);
                    }
                }
                return field;
            }

            float4 PSMain(VSOut i) : SV_Target {
                float2 uv = i.uv;
                float2 canvasUv0 = (FloorPos + uv * FloorSize) / max(CanvasSize, float2(1, 1));
                canvasUv0 = saturate(canvasUv0);

                float2 dir;
                float dist = DistToStage(uv, dir);
                float wallN;
                float2 inward;
                NearestWall(uv, wallN, inward);
                float2 blobAway;
                float blobR;
                float blobField = BlobField(uv, blobAway, blobR);

                // Pixelmap floor outline: West1 → West2 slant → West3, not the AABB top.
                float slantV = WestEdgeV(uv.x);
                float insideSlant = (uv.y - slantV) * FloorSize.y;
                float floorMask = smoothstep(-6.0, 6.0, insideSlant);

                float front = 1.24 * (1.0 - pow(1.0 - saturate(Progress), 4.6));
                float fade = pow(saturate(1.0 - Progress), 0.38);
                float live = smoothstep(0.0, 0.02, Progress) * fade * floorMask;

                float wl = max(Wavelength, 0.04);
                float sigma = wl * lerp(0.52, 1.25, Progress);
                float x = dist - front;
                float packet = exp(-0.5 * (x * x) / max(sigma * sigma, 1e-5));
                float wake = saturate(-x * 2.4) * exp(min(x, 0.0) * 1.6) * 0.55;

                float k = 6.28318530718 / wl;
                float gerst = sin(k * x) + 0.40 * sin(2.0 * k * x);
                float height = packet * gerst;
                float displace = packet * cos(k * x);

                float shore = 1.0 - smoothstep(0.0, 0.13, wallN);
                float shoreTight = 1.0 - smoothstep(0.0, 0.055, wallN);
                float arrived = 1.0 - smoothstep(-0.04, 0.10, x);
                float blobBreak = blobField * saturate(packet * 1.45 + arrived * 0.9) * live;
                float breaking = shore * saturate(packet * 1.35 + arrived * 0.85) * live;
                breaking = max(breaking, blobBreak);
                float foam = breaking * lerp(0.35, 1.0, FoamNoise(uv, Progress));
                foam = saturate(foam + shoreTight * arrived * live * 0.55 * fade);
                foam = saturate(foam + blobBreak * lerp(0.4, 1.0, FoamNoise(uv + blobAway, Progress + 0.2)));

                // Schleife: traveling wave from stage (right) toward the left.
                float interior = (1.0 - max(shore, blobField * 0.85)) * live;
                float hum = sin(dist * 18.0 - Progress * 22.0) * 0.78
                          + sin(dist * 9.0  - Progress * 11.0) * 0.48
                          + sin(dist * 32.0 - Progress * 28.0) * 0.22;
                hum *= interior * saturate(0.25 + arrived * 0.85 + wake);

                float amp = Amplitude * live;
                float travel = 1.0 - shore * 0.82 - blobField * 0.55;
                float2 uvW = uv;
                uvW += dir * ((displace * 0.11 + height * 0.04) * amp * travel);
                uvW -= inward * (breaking * amp * 0.09);
                uvW += inward * ((foam - 0.5) * breaking * 0.035);
                uvW += blobAway * (blobBreak * amp * 0.11);
                uvW += dir * (hum * amp * 0.12);
                uvW += float2(-1.0, 0.0) * (hum * amp * 0.05);
                uvW = saturate(uvW);
                float2 canvasUv = (FloorPos + uvW * FloorSize) / max(CanvasSize, float2(1, 1));
                canvasUv = saturate(canvasUv);

                float4 c = tex.Sample(samp, canvasUv);
                float4 c0 = tex.Sample(samp, canvasUv0);

                float dPacket = packet * (-x / max(sigma * sigma, 1e-5));
                float dGerst = k * cos(k * x) + 0.80 * k * cos(2.0 * k * x);
                float slope = (dPacket * gerst + packet * dGerst) * amp * travel;
                slope += breaking * amp * 1.8 * (0.5 - wallN * 4.0);
                slope += blobBreak * amp * 2.2 * (0.5 - blobField);
                slope += hum * amp * 2.0;

                float3 N = normalize(float3(-slope, 0.0, 0.48));
                float3 L = normalize(float3(-0.72, 0.10, 0.68));
                float3 V = float3(0, 0, 1);
                float diff = saturate(dot(N, L) * 0.70 + 0.30);
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), 36.0) * saturate(packet + wake + abs(hum) * 0.55 + blobBreak) * live;

                float shade = lerp(0.58, 1.18, diff);
                shade = lerp(shade, lerp(0.88, 1.14, diff), saturate(blobField * 1.2));
                float3 rgb = c.rgb * shade;
                rgb += spec * float3(0.90, 0.95, 1.0) * 0.55;
                rgb += saturate(height) * packet * live * float3(0.10, 0.18, 0.30) * 0.32;
                rgb += abs(hum) * float3(0.14, 0.20, 0.30) * 0.26;

                float3 foamCol = float3(0.86, 0.93, 1.0);
                rgb = lerp(rgb, foamCol, saturate(foam * 0.72) * (1.0 - blobField * 0.4));
                rgb += shoreTight * foam * live * foamCol * 0.28;
                rgb += blobBreak * foamCol * 0.22;
                rgb = lerp(c0.rgb, rgb, floorMask);

                return float4(rgb * Opacity, Opacity);
            }
            """;

        var psBlob = Compiler.Compile(hlsl, "PSMain", "wave.hlsl", "ps_4_0");
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);
    }

    public void Dispose()
    {
        DisposeWaveTarget();
        DisposeReflectTargets();
        _backgroundSrv?.Dispose();
        _backgroundTexture?.Dispose();
        _rasterizer.Dispose();
        _maxBlend.Dispose();
        _blend.Dispose();
        _borderSampler.Dispose();
        _sampler.Dispose();
        _waveCb.Dispose();
        _blobCb.Dispose();
        _blurCb.Dispose();
        _reflectCb.Dispose();
        _layerCb.Dispose();
        _frameCb.Dispose();
        _reflectVb.Dispose();
        _vb.Dispose();
        _inputLayout.Dispose();
        _wavePs.Dispose();
        _blobPs.Dispose();
        _blobVs.Dispose();
        _blurPs.Dispose();
        _reflectPs.Dispose();
        _reflectVs.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        DisposePreviewReadback();
        _previewRtv.Dispose();
        _previewTexture.Dispose();
        _roomCanvasSrv?.Dispose();
        _roomCanvasRtv?.Dispose();
        _roomCanvasTexture?.Dispose();
        _canvasSrv.Dispose();
        _canvasRtv.Dispose();
        _canvasTexture.Dispose();
    }
}
