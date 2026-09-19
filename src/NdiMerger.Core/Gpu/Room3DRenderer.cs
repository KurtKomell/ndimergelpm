using System.Numerics;
using System.Runtime.InteropServices;
using NdiMerger.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NdiMerger.Core.Gpu;

public enum RoomCameraMode
{
    /// <summary>Orbit outside the room around a target point.</summary>
    Orbit = 0,
    /// <summary>First-person walk inside the room.</summary>
    Walk = 1
}

public readonly record struct Room3DCamera
{
    public RoomCameraMode Mode { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public float Distance { get; init; }
    /// <summary>Orbit look-at, or walk eye position (Y = eye height).</summary>
    public Vector3 Target { get; init; }

    public static Room3DCamera DefaultOrbit(float floorW, float floorH) => new()
    {
        Mode = RoomCameraMode.Orbit,
        Yaw = 0.55f,
        Pitch = 0.35f,
        Distance = MathF.Max(floorW, floorH) * 1.35f,
        Target = new Vector3(floorW * 0.45f, 400f, floorH * 0.55f)
    };

    public static Room3DCamera DefaultWalk(float floorW, float floorH) => new()
    {
        Mode = RoomCameraMode.Walk,
        // Look toward Sud (west / -X) from near Est-Sud corner — into the room
        Yaw = MathF.PI * 0.75f,
        Pitch = 0.05f,
        Distance = 1f,
        Target = new Vector3(floorW * 0.55f, MathF.Min(900f, floorH * 0.25f), floorH * 0.72f)
    };

    public static Room3DCamera Default(float floorW, float floorH) => DefaultOrbit(floorW, floorH);

    public Vector3 Forward()
    {
        float cp = MathF.Cos(Pitch);
        return Vector3.Normalize(new Vector3(
            MathF.Sin(Yaw) * cp,
            MathF.Sin(Pitch),
            MathF.Cos(Yaw) * cp));
    }

    public Vector3 Right()
    {
        var f = Forward();
        var r = Vector3.Cross(Vector3.UnitY, f);
        return r.LengthSquared() > 1e-8f ? Vector3.Normalize(r) : Vector3.UnitX;
    }

    public Vector3 EyePosition()
    {
        if (Mode == RoomCameraMode.Walk)
            return Target;

        float cp = MathF.Cos(Pitch);
        return Target + new Vector3(
            MathF.Sin(Yaw) * cp * Distance,
            MathF.Sin(Pitch) * Distance,
            MathF.Cos(Yaw) * cp * Distance);
    }

    public Matrix4x4 View()
    {
        var eye = EyePosition();
        if (Mode == RoomCameraMode.Walk)
            return Matrix4x4.CreateLookAt(eye, eye + Forward(), Vector3.UnitY);
        return Matrix4x4.CreateLookAt(eye, Target, Vector3.UnitY);
    }

    public void ScreenToRay(
        float ndcX, float ndcY, float aspect, float fovY,
        out Vector3 origin, out Vector3 dir)
    {
        origin = EyePosition();
        var view = View();
        Matrix4x4.Invert(view, out var invView);
        float tan = MathF.Tan(fovY * 0.5f);
        var viewDir = Vector3.Normalize(new Vector3(ndcX * tan * aspect, ndcY * tan, -1f));
        dir = Vector3.Normalize(Vector3.TransformNormal(viewDir, invView));
    }

    public Room3DCamera ClampLook() => this with
    {
        Pitch = Mode == RoomCameraMode.Walk
            ? Math.Clamp(Pitch, -1.35f, 1.35f)
            : Math.Clamp(Pitch, 0.05f, 1.45f),
        Distance = Math.Clamp(Distance, 500f, 80000f)
    };

    public Room3DCamera ClampWalkInside(float floorW, float floorH, float margin = 80f)
    {
        if (Mode != RoomCameraMode.Walk)
            return this;
        return this with
        {
            Target = new Vector3(
                Math.Clamp(Target.X, margin, MathF.Max(margin + 1f, floorW - margin)),
                Math.Clamp(Target.Y, 200f, 3500f),
                Math.Clamp(Target.Z, margin, MathF.Max(margin + 1f, floorH - margin)))
        };
    }
}

public readonly struct RoomLiveDraw
{
    public required string ZoneId { get; init; }
    public required ID3D11ShaderResourceView Srv { get; init; }
    public float ContentRotationDegrees { get; init; }
    public float Opacity { get; init; }
    public Vector2 UvMin { get; init; }
    public Vector2 UvMax { get; init; }
    public ScaleMode ScaleMode { get; init; }
    public int SourceW { get; init; }
    public int SourceH { get; init; }
    public float PanelContentW { get; init; }
    public float PanelContentH { get; init; }
    /// <summary>False for Spout/NDI (often BGRX with alpha 0); true for PNG overlays.</summary>
    public bool UseTexAlpha { get; init; }
    /// <summary>0 none, 1 180, 2 90 CW, 3 90 CCW — canvas AABB → wall content UV.</summary>
    public int CanvasOrient { get; init; }
    public bool BlackKeyEnabled { get; init; }
    public float BlackKeyThreshold { get; init; }
    /// <summary>SRV RGB is already premultiplied by alpha (compose canvas crop).</summary>
    public bool SourcePremultiplied { get; init; }
    /// <summary>Draw order within a wall (lower first).</summary>
    public int SortOrder { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RoomFrameCb
{
    public Matrix4x4 ViewProjection;
    public Vector4 LightDir;
    public Vector4 Ambient;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RoomLayerCb
{
    public Vector4 Corner0;
    public Vector4 AxisX;
    public Vector4 AxisY;
    public Vector4 Color;
    public Vector2 UvMin;
    public Vector2 UvMax;
    public float UseTexture;
    public float UseTexAlpha;
    public float ContentRotRad;
    public float CanvasOrient;
    public float BlackKeyEnabled;
    public float BlackKeyThreshold;
    public float BlackKeySoftness;
    public float SourcePremultiplied;
}

/// <summary>
/// Perspective room preview: foldable walls with live Spout/NDI SRVs and optional photo overlays.
/// </summary>
public sealed class Room3DRenderer : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vb;
    private readonly ID3D11Buffer _ib;
    private readonly ID3D11Buffer _frameCb;
    private readonly ID3D11Buffer _layerCb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11RasterizerState _rasterizerOverlay;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11DepthStencilState _depthReadonly;
    private ID3D11Texture2D? _depthTex;
    private ID3D11DepthStencilView? _dsv;
    private int _depthW;
    private int _depthH;

    private readonly Dictionary<string, (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)> _photos = new(StringComparer.OrdinalIgnoreCase);
    private List<RoomPanel> _panels = [];
    private float _floorW = 5738f;
    private float _floorH = 3398f;

    public Room3DCamera Camera { get; set; }
    public float AssembleT { get; set; } = 1f;
    public bool PhotoOverlayEnabled { get; set; }
    public float PhotoOverlayOpacity { get; set; } = 0.35f;
    public float FloorWidth => _floorW;
    public float FloorHeight => _floorH;
    public IReadOnlyList<RoomPanel> Panels => _panels;

    public Room3DRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        Compile(out _vs, out _ps, out _layout);

        // Unit quad in local space: u along X (0..1), v along Y (0..1) — transformed per panel
        var verts = new Vertex[]
        {
            new() { Position = new Vector3(0, 0, 0), TexCoord = new Vector2(0, 1) },
            new() { Position = new Vector3(1, 0, 0), TexCoord = new Vector2(1, 1) },
            new() { Position = new Vector3(1, 1, 0), TexCoord = new Vector2(1, 0) },
            new() { Position = new Vector3(0, 1, 0), TexCoord = new Vector2(0, 0) },
        };
        _vb = gpu.Device.CreateBuffer(verts, BindFlags.VertexBuffer);
        _ib = gpu.Device.CreateBuffer(new ushort[] { 0, 1, 2, 0, 2, 3 }, BindFlags.IndexBuffer);
        _frameCb = CreateCb(Marshal.SizeOf<RoomFrameCb>());
        _layerCb = CreateCb(Marshal.SizeOf<RoomLayerCb>());

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

        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            // Premultiplied: fade + black-key on live walls without a black veil.
            SourceBlend = Blend.One,
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
        // Pull live/photo slightly toward the camera so the panel solid stays under
        // the content (West 2 slant otherwise z-fights and the solid covers the layer).
        _rasterizerOverlay = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            FrontCounterClockwise = false,
            DepthClipEnable = true,
            DepthBias = -256,
            SlopeScaledDepthBias = -2f,
            DepthBiasClamp = 0f
        });

        _depthState = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual
        });
        _depthReadonly = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual
        });

        Camera = Room3DCamera.Default(_floorW, _floorH);
    }

    public void RebuildPanels(IReadOnlyList<ZoneDefinition> zones)
    {
        _panels = RoomGeometry.BuildPanels(zones);
        var floor = zones.FirstOrDefault(z => z.Id.Equals("floor", StringComparison.OrdinalIgnoreCase));
        if (floor is not null)
        {
            _floorW = floor.Width;
            _floorH = floor.Height;
        }

        Camera = Room3DCamera.DefaultOrbit(_floorW, _floorH);
    }

    public void SetCameraMode(RoomCameraMode mode)
    {
        if (Camera.Mode == mode)
            return;

        if (mode == RoomCameraMode.Walk)
        {
            // Drop into the room near the current look target / orbit focus
            var eye = new Vector3(
                Math.Clamp(Camera.Target.X, 200f, _floorW - 200f),
                850f,
                Math.Clamp(Camera.Target.Z, 200f, _floorH - 200f));
            Camera = new Room3DCamera
            {
                Mode = RoomCameraMode.Walk,
                Yaw = Camera.Yaw,
                Pitch = 0.05f,
                Distance = 1f,
                Target = eye
            }.ClampWalkInside(_floorW, _floorH);
        }
        else
        {
            var eye = Camera.EyePosition();
            var focus = new Vector3(_floorW * 0.5f, 400f, _floorH * 0.5f);
            var toEye = eye - focus;
            float dist = Math.Clamp(toEye.Length(), 1500f, 80000f);
            float yaw = MathF.Atan2(toEye.X, toEye.Z);
            float pitch = Math.Clamp(MathF.Asin(Math.Clamp(toEye.Y / dist, -1f, 1f)), 0.05f, 1.45f);
            Camera = new Room3DCamera
            {
                Mode = RoomCameraMode.Orbit,
                Yaw = yaw,
                Pitch = pitch,
                Distance = dist,
                Target = focus
            };
        }
    }

    public void ApplyCamera(Room3DCamera cam)
    {
        Camera = cam.ClampLook().ClampWalkInside(_floorW, _floorH);
    }

    public void LoadPhoto(string zoneId, string path)
    {
        if (!File.Exists(path))
            return;
        using var bmp = new System.Drawing.Bitmap(path);
        UploadPhoto(zoneId, bmp);
    }

    public void LoadPhotoFromCrop(
        string zoneId,
        string backgroundPath,
        ZoneDefinition zone)
    {
        if (!File.Exists(backgroundPath))
            return;
        using var src = new System.Drawing.Bitmap(backgroundPath);
        int x = Math.Clamp((int)zone.X, 0, src.Width - 1);
        int y = Math.Clamp((int)zone.Y, 0, src.Height - 1);
        int w = Math.Clamp((int)zone.Width, 1, src.Width - x);
        int h = Math.Clamp((int)zone.Height, 1, src.Height - y);
        using var crop = src.Clone(new System.Drawing.Rectangle(x, y, w, h), src.PixelFormat);
        WallFloorMapping.ApplyPhotoOrientation(crop, zone.Id);
        UploadPhoto(zoneId, crop);
    }

    private void UploadPhoto(string zoneId, System.Drawing.Bitmap bmp)
    {
        ReleasePhoto(zoneId);
        int w = bmp.Width;
        int h = bmp.Height;
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * h;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            // Convert stride if needed
            if (data.Stride != w * 4)
            {
                var packed = new byte[w * 4 * h];
                for (int row = 0; row < h; row++)
                    Buffer.BlockCopy(buffer, row * Math.Abs(data.Stride), packed, row * w * 4, w * 4);
                buffer = packed;
            }

            var tex = _gpu.CreateTexture(w, h);
            unsafe
            {
                fixed (byte* ptr = buffer)
                    _gpu.Context.UpdateSubresource(tex, 0, null, (IntPtr)ptr, (uint)(w * 4), (uint)buffer.Length);
            }
            var srv = _gpu.Device.CreateShaderResourceView(tex);
            _photos[zoneId] = (tex, srv);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void ReleasePhoto(string zoneId)
    {
        if (_photos.Remove(zoneId, out var entry))
        {
            entry.Srv.Dispose();
            entry.Tex.Dispose();
        }
    }

    public void ClearPhotos()
    {
        foreach (var id in _photos.Keys.ToList())
            ReleasePhoto(id);
    }

    public RoomPanel? FindPanel(string zoneId) =>
        _panels.FirstOrDefault(p => p.ZoneId.Equals(zoneId, StringComparison.OrdinalIgnoreCase));

    public void Render(
        ID3D11RenderTargetView rtv,
        int width,
        int height,
        IReadOnlyList<RoomLiveDraw> liveDraws,
        ID3D11ShaderResourceView? floorMirrorSrv = null,
        ID3D11ShaderResourceView? floorBlobSrv = null)
    {
        EnsureDepth(width, height);
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(rtv, _dsv);
        ctx.RSSetViewport(new Viewport(0, 0, width, height));
        ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);

        float aspect = width / (float)Math.Max(height, 1);
        const float fov = MathF.PI / 4f;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 10f, 200000f);
        var vp = Camera.View() * proj;

        ctx.UpdateSubresource(new RoomFrameCb
        {
            ViewProjection = Matrix4x4.Transpose(vp),
            LightDir = new Vector4(Vector3.Normalize(new Vector3(0.35f, 1f, 0.25f)), 0f),
            Ambient = new Vector4(0.92f, 0.92f, 0.93f, 1f)
        }, _frameCb);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.IASetIndexBuffer(_ib, Format.R16_UInt, 0);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.VSSetConstantBuffer(0, _frameCb);
        ctx.PSSetConstantBuffer(0, _frameCb);
        ctx.VSSetConstantBuffer(1, _layerCb);
        ctx.PSSetConstantBuffer(1, _layerCb);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(_blend);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.RSSetState(_rasterizer);

        var liveByZone = liveDraws
            .GroupBy(d => d.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RoomLiveDraw>)g.OrderBy(d => d.SortOrder).ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Pass 1: floor solid always; wall solids ONLY on empty panels (no live).
        // Keyed live content must not sit on a gray plate — holes go to black clear.
        ctx.OMSetDepthStencilState(_depthState);
        ctx.RSSetState(_rasterizer);
        foreach (var panel in _panels)
        {
            bool isFloor = panel.Kind == RoomPanelKind.Floor;
            if (!isFloor && liveByZone.ContainsKey(panel.ZoneId))
                continue;

            var quad = RoomGeometry.Evaluate(panel, AssembleT);
            DrawPanel(quad, null, 1f, Vector2.Zero, Vector2.One,
                useTex: false, useTexAlpha: false, 0f,
                solidColor: isFloor
                    ? new Vector4(0f, 0f, 0f, 1f)
                    : new Vector4(0.14f, 0.15f, 0.18f, 1f));
        }

        // Pass 2: live / floor FX / photos above solids (depth-biased).
        ctx.OMSetDepthStencilState(_depthReadonly);
        ctx.RSSetState(_rasterizerOverlay);
        foreach (var panel in _panels)
        {
            var quad = RoomGeometry.Evaluate(panel, AssembleT);
            bool isFloor = panel.Kind == RoomPanelKind.Floor;

            if (isFloor)
            {
                Vector2 floorMin = new(0f, 1f);
                Vector2 floorMax = new(1f, 0f);
                if (floorMirrorSrv is not null)
                {
                    DrawPanel(quad, floorMirrorSrv, 1f, floorMin, floorMax,
                        useTex: true, useTexAlpha: true, 0f);
                }
                if (floorBlobSrv is not null)
                {
                    DrawPanel(quad, floorBlobSrv, 1f, floorMin, floorMax,
                        useTex: true, useTexAlpha: true, 0f);
                }
            }

            if (liveByZone.TryGetValue(panel.ZoneId, out var lives))
            {
                foreach (var live in lives)
                {
                    if (live.Opacity <= 0.001f)
                        continue;
                    DrawPanel(quad, live.Srv, live.Opacity, live.UvMin, live.UvMax,
                        useTex: true, useTexAlpha: live.UseTexAlpha, live.ContentRotationDegrees,
                        canvasOrient: live.CanvasOrient,
                        blackKeyEnabled: live.BlackKeyEnabled,
                        blackKeyThreshold: live.BlackKeyThreshold,
                        sourcePremultiplied: live.SourcePremultiplied);
                }
            }

            if (PhotoOverlayEnabled && PhotoOverlayOpacity > 0.01f
                && _photos.TryGetValue(panel.ZoneId, out var photo))
            {
                Vector2 photoMin = Vector2.Zero;
                Vector2 photoMax = Vector2.One;
                if (isFloor)
                    (photoMin, photoMax) = (new Vector2(0f, 1f), new Vector2(1f, 0f));
                DrawPanel(quad, photo.Srv, PhotoOverlayOpacity, photoMin, photoMax,
                    useTex: true, useTexAlpha: true, 0f);
            }
        }

        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, (ID3D11DepthStencilView)null!);
        ctx.PSSetShaderResource(0, null!);
    }

    public bool TryPick(float ndcX, float ndcY, float aspect, out string zoneId)
    {
        const float fov = MathF.PI / 4f;
        Camera.ScreenToRay(ndcX, ndcY, aspect, fov, out var origin, out var dir);
        return RoomGeometry.TryPick(_panels, AssembleT, origin, dir, out zoneId, out _);
    }

    private void DrawPanel(
        RoomQuad quad,
        ID3D11ShaderResourceView? srv,
        float opacity,
        Vector2 uvMin,
        Vector2 uvMax,
        bool useTex,
        bool useTexAlpha,
        float contentRotDeg,
        Vector4? solidColor = null,
        int canvasOrient = 0,
        bool blackKeyEnabled = false,
        float blackKeyThreshold = 0.08f,
        bool sourcePremultiplied = false)
    {
        var color = solidColor ?? Vector4.One;
        color.W = opacity;
        float soft = MathF.Max(Math.Clamp(blackKeyThreshold, 0f, 1f) * 0.35f, 0.001f);
        var axisX = quad.C1 - quad.C0;
        var axisY = quad.C3 - quad.C0;
        _gpu.Context.UpdateSubresource(new RoomLayerCb
        {
            Corner0 = new Vector4(quad.C0, 0f),
            AxisX = new Vector4(axisX, 0f),
            AxisY = new Vector4(axisY, 0f),
            Color = color,
            UvMin = uvMin,
            UvMax = uvMax,
            UseTexture = useTex && srv is not null ? 1f : 0f,
            UseTexAlpha = useTexAlpha ? 1f : 0f,
            ContentRotRad = contentRotDeg * (MathF.PI / 180f),
            CanvasOrient = canvasOrient,
            BlackKeyEnabled = blackKeyEnabled ? 1f : 0f,
            BlackKeyThreshold = Math.Clamp(blackKeyThreshold, 0f, 1f),
            BlackKeySoftness = soft,
            SourcePremultiplied = sourcePremultiplied ? 1f : 0f
        }, _layerCb);

        if (srv is not null && useTex)
            _gpu.Context.PSSetShaderResource(0, srv);
        else
            _gpu.Context.PSSetShaderResource(0, null!);

        _gpu.Context.DrawIndexed(6, 0, 0);
    }

    private void EnsureDepth(int w, int h)
    {
        if (_dsv is not null && _depthW == w && _depthH == h)
            return;
        _dsv?.Dispose();
        _depthTex?.Dispose();
        _depthW = w;
        _depthH = h;
        _depthTex = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil
        });
        _dsv = _gpu.Device.CreateDepthStencilView(_depthTex);
    }

    private ID3D11Buffer CreateCb(int size)
    {
        int aligned = (size + 15) & ~15;
        return _gpu.Device.CreateBuffer((uint)aligned, BindFlags.ConstantBuffer, ResourceUsage.Default);
    }

    private void Compile(out ID3D11VertexShader vs, out ID3D11PixelShader ps, out ID3D11InputLayout layout)
    {
        const string hlsl = """
            cbuffer FrameCB : register(b0) {
                float4x4 ViewProjection;
                float4 LightDir;
                float4 Ambient;
            };
            cbuffer LayerCB : register(b1) {
                float4 Corner0;
                float4 AxisX;
                float4 AxisY;
                float4 Color;
                float2 UvMin;
                float2 UvMax;
                float UseTexture;
                float UseTexAlpha;
                float ContentRotRad;
                float CanvasOrient;
                float BlackKeyEnabled;
                float BlackKeyThreshold;
                float BlackKeySoftness;
                float SourcePremultiplied;
            };
            Texture2D tex : register(t0);
            SamplerState samp : register(s0);

            struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
            struct VSOut {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 localUv : TEXCOORD1;
                float3 nrm : TEXCOORD2;
            };

            VSOut VSMain(VSIn i) {
                VSOut o;
                float3 wp = Corner0.xyz + i.pos.x * AxisX.xyz + i.pos.y * AxisY.xyz;
                o.pos = mul(float4(wp, 1), ViewProjection);
                float2 uv = i.uv;
                if (abs(ContentRotRad) > 0.0001) {
                    float2 c = float2(0.5, 0.5);
                    float2 d = uv - c;
                    float ca = cos(ContentRotRad);
                    float sa = sin(ContentRotRad);
                    d = float2(ca * d.x - sa * d.y, sa * d.x + ca * d.y);
                    uv = c + d;
                }
                o.localUv = uv;
                float2 t = saturate(uv);
                float su = t.x, sv = t.y;
                if (CanvasOrient > 0.5 && CanvasOrient < 1.5) { su = 1 - t.x; sv = 1 - t.y; }
                else if (CanvasOrient > 1.5 && CanvasOrient < 2.5) { su = t.y; sv = 1 - t.x; }
                else if (CanvasOrient > 2.5 && CanvasOrient < 3.5) { su = 1 - t.y; sv = t.x; }
                else if (CanvasOrient > 3.5) { su = 1 - t.y; sv = 1 - t.x; }
                o.uv = lerp(UvMin, UvMax, float2(su, sv));
                o.nrm = normalize(cross(AxisX.xyz, AxisY.xyz));
                return o;
            }

            float4 PSMain(VSOut i) : SV_Target {
                float4 c = UseTexture > 0.5 ? tex.Sample(samp, i.uv) : float4(1,1,1,1);
                float3 n = normalize(i.nrm);
                // Nearly unlit so Spout/canvas content stays readable on walls.
                float ndl = abs(dot(n, normalize(LightDir.xyz))) * 0.12 + Ambient.x;

                if (UseTexture > 0.5 && (i.localUv.x < -0.001 || i.localUv.x > 1.001 || i.localUv.y < -0.001 || i.localUv.y > 1.001))
                    return float4(0, 0, 0, 0);

                // Compose-canvas blit: RGB already premultiplied.
                if (SourcePremultiplied > 0.5) {
                    float a = c.a * Color.a;
                    float3 rgb = c.rgb * Color.rgb * Color.a * ndl;
                    return float4(rgb, a);
                }

                float a = UseTexAlpha > 0.5 ? c.a * Color.a : Color.a;
                if (BlackKeyEnabled > 0.5) {
                    float luma = max(c.r, max(c.g, c.b));
                    float soft = max(BlackKeySoftness, 0.001);
                    a *= smoothstep(BlackKeyThreshold, BlackKeyThreshold + soft, luma);
                }
                // Premultiplied so opacity fades don't leave a black veil on keyed areas.
                float3 rgb = c.rgb * Color.rgb * ndl * a;
                return float4(rgb, a);
            }
            """;

        var vsBlob = Compiler.Compile(hlsl, "VSMain", "room3d.hlsl", "vs_4_0");
        var psBlob = Compiler.Compile(hlsl, "PSMain", "room3d.hlsl", "ps_4_0");
        vs = _gpu.Device.CreateVertexShader(vsBlob.Span);
        ps = _gpu.Device.CreatePixelShader(psBlob.Span);
        layout = _gpu.Device.CreateInputLayout([
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
        ], vsBlob.Span);
    }

    public void Dispose()
    {
        ClearPhotos();
        _dsv?.Dispose();
        _depthTex?.Dispose();
        _depthReadonly.Dispose();
        _depthState.Dispose();
        _rasterizerOverlay.Dispose();
        _rasterizer.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _layerCb.Dispose();
        _frameCb.Dispose();
        _ib.Dispose();
        _vb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
