using System.Numerics;
using System.Runtime.InteropServices;
using NdiMerger.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

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

public sealed class PixelMapCompositor : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly ID3D11Texture2D _canvasTexture;
    private readonly ID3D11RenderTargetView _canvasRtv;
    private readonly ID3D11ShaderResourceView _canvasSrv;

    private readonly ID3D11Texture2D _previewTexture;
    private readonly ID3D11RenderTargetView _previewRtv;
    private readonly ID3D11Texture2D _previewStaging;

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _vb;
    private readonly ID3D11Buffer _frameCb;
    private readonly ID3D11Buffer _layerCb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11SamplerState _borderSampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly ID3D11VertexShader _reflectVs;
    private readonly ID3D11PixelShader _reflectPs;
    private readonly ID3D11PixelShader _blurPs;
    private readonly ID3D11Buffer _reflectVb;
    private readonly ID3D11Buffer _reflectCb;
    private readonly ID3D11Buffer _blurCb;

    private ID3D11Texture2D? _reflectTex;
    private ID3D11RenderTargetView? _reflectRtv;
    private ID3D11ShaderResourceView? _reflectSrv;
    private ID3D11Texture2D? _blurTex;
    private ID3D11RenderTargetView? _blurRtv;
    private ID3D11ShaderResourceView? _blurSrv;
    private int _reflectW;
    private int _reflectH;

    private ID3D11Texture2D? _backgroundTexture;
    private ID3D11ShaderResourceView? _backgroundSrv;
    private int _backgroundWidth;
    private int _backgroundHeight;

    public const float OverlayOpacity = 0.5f;

    public int CanvasWidth => _canvasWidth;
    public int CanvasHeight => _canvasHeight;
    public int BackgroundWidth => _backgroundWidth;
    public int BackgroundHeight => _backgroundHeight;
    private bool HasBackground => _backgroundSrv is not null && _backgroundWidth > 0 && _backgroundHeight > 0;
    public int PreviewWidth { get; }
    public int PreviewHeight { get; }
    public ID3D11Texture2D CanvasTexture => _canvasTexture;
    public ID3D11ShaderResourceView CanvasSrv => _canvasSrv;
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
        _previewStaging = gpu.CreateStagingTexture(PreviewWidth, PreviewHeight);

        CompileShaders(out _vs, out _ps, out _inputLayout);
        CompileReflectShaders(out _reflectVs, out _reflectPs);
        CompileBlurShader(out _blurPs);

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
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blend = gpu.Device.CreateBlendState(blendDesc);

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
        FloorReflectionSettings? reflection = null)
    {
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        ctx.ClearRenderTargetView(_canvasRtv, new Color4(0, 0, 0, 1));

        BindLayerPipeline();

        // Ortho: x 0..width, y 0..height (Y down)
        var vp = Matrix4x4.CreateOrthographicOffCenter(0, _canvasWidth, _canvasHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);

        var visible = layers.Where(l => l.IsEffectivelyVisible).OrderBy(l => l.ZIndex).ToList();
        var settings = reflection ?? FloorReflectionSettings.Default;
        var floor = zones?.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));

        foreach (var layer in visible.Where(FloorReflection.IsFloorLayer))
            DrawLayer(layer, resolveSrv);

        if (settings.Enabled && floor is not null)
            DrawFloorReflections(visible, resolveSrv, zones!, floor, settings);

        BindLayerPipeline();
        ctx.OMSetRenderTargets(_canvasRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);

        foreach (var layer in visible.Where(l => !FloorReflection.IsFloorLayer(l)))
            DrawLayer(layer, resolveSrv);

        ctx.OMSetRenderTargets(_previewRtv);
        ctx.RSSetViewport(new Viewport(0, 0, PreviewWidth, PreviewHeight));
        ctx.ClearRenderTargetView(_previewRtv, new Color4(0, 0, 0, 1));
        var previewVp = Matrix4x4.CreateOrthographicOffCenter(0, PreviewWidth, PreviewHeight, 0, 0, 1);
        ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(previewVp) }, _frameCb);
        DrawTexturedQuad(0, 0, PreviewWidth, PreviewHeight, 0, 1f, _canvasSrv);

        if (overlayPreview && HasBackground)
            DrawTexturedQuad(0, 0, PreviewWidth, PreviewHeight, 0, OverlayOpacity, _backgroundSrv!, useTexAlpha: true);

        ctx.CopyResource(_previewStaging, _previewTexture);

        if (overlayOutput && HasBackground)
        {
            ctx.OMSetRenderTargets(_canvasRtv);
            ctx.RSSetViewport(new Viewport(0, 0, _canvasWidth, _canvasHeight));
            ctx.UpdateSubresource(new FrameConstants { ViewProjection = Matrix4x4.Transpose(vp) }, _frameCb);
            DrawTexturedQuad(0, 0, _backgroundWidth, _backgroundHeight, 0, OverlayOpacity, _backgroundSrv!, useTexAlpha: true);
        }
    }

    private void DrawLayer(CompositionLayer layer, Func<CompositionLayer, ID3D11ShaderResourceView?> resolveSrv)
    {
        var srv = resolveSrv(layer);
        if (srv is null || layer.NativeWidth <= 0 || layer.NativeHeight <= 0)
            return;

        float w = layer.NativeWidth * layer.Scale;
        float h = layer.NativeHeight * layer.Scale;
        float threshold = Math.Clamp(layer.BlackKeyThreshold, 0f, 1f);
        float softness = Math.Max(threshold * 0.35f, 0.001f);
        DrawTexturedQuad(
            layer.X, layer.Y, w, h, layer.RotationDegrees, layer.Opacity, srv,
            useTexAlpha: false,
            blackKeyEnabled: layer.BlackKeyEnabled,
            blackKeyThreshold: threshold,
            blackKeySoftness: softness);
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

    public bool TryReadPreviewBgra(byte[] destination, out int stride)
    {
        stride = PreviewWidth * 4;
        if (destination.Length < stride * PreviewHeight)
            return false;

        var mapped = _gpu.Context.Map(_previewStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                for (int y = 0; y < PreviewHeight; y++)
                {
                    Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), destination, y * stride, stride);
                }
            }
        }
        finally
        {
            _gpu.Context.Unmap(_previewStaging, 0);
        }

        return true;
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
            if (FloorReflection.IsFloorLayer(layer) || layer.Opacity < 0.02f)
                continue;
            var srv = resolveSrv(layer);
            if (srv is null)
                continue;

            foreach (var quad in FloorReflection.Build(layer, floor, nord, settings.Length, settings.Angle))
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
            Color = new Vector4(1, 1, 1, 1)
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

    private void DrawTexturedQuad(
        float x, float y, float w, float h, float rotationDeg, float opacity,
        ID3D11ShaderResourceView srv,
        bool useTexAlpha = false,
        bool blackKeyEnabled = false,
        float blackKeyThreshold = 0.08f,
        float blackKeySoftness = 0.03f)
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
            BlackKeySoftness = blackKeySoftness
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
                float4 c = tex.Sample(samp, i.uv);
                float a = UseTexAlpha > 0.5 ? c.a * Color.a : Color.a;
                if (BlackKeyEnabled > 0.5) {
                    float luma = max(c.r, max(c.g, c.b));
                    float soft = max(BlackKeySoftness, 0.001);
                    a *= smoothstep(BlackKeyThreshold, BlackKeyThreshold + soft, luma);
                }
                return float4(c.rgb * Color.rgb, a);
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
                return float4(c.rgb * Color.rgb, alpha);
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

    public void Dispose()
    {
        DisposeReflectTargets();
        _backgroundSrv?.Dispose();
        _backgroundTexture?.Dispose();
        _rasterizer.Dispose();
        _blend.Dispose();
        _borderSampler.Dispose();
        _sampler.Dispose();
        _blurCb.Dispose();
        _reflectCb.Dispose();
        _layerCb.Dispose();
        _frameCb.Dispose();
        _reflectVb.Dispose();
        _vb.Dispose();
        _inputLayout.Dispose();
        _blurPs.Dispose();
        _reflectPs.Dispose();
        _reflectVs.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _previewStaging.Dispose();
        _previewRtv.Dispose();
        _previewTexture.Dispose();
        _canvasSrv.Dispose();
        _canvasRtv.Dispose();
        _canvasTexture.Dispose();
    }
}
