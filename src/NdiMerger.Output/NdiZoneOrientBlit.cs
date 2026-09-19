using System.Numerics;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NdiMerger.Output;

/// <summary>
/// How to unwrap a pixelmap AABB crop so the floor-adjacent edge is at the
/// bottom of the NDI frame. Floor: nord edge at bottom (90° CW).
/// </summary>
public enum NdiZoneOrient : byte
{
    None = 0,
    Rot180 = 1,
    Rot90Cw = 2,
    Rot90Ccw = 3
}

/// <summary>
/// GPU blit: sample a canvas AABB into an oriented NDI output texture.
/// </summary>
public sealed class NdiZoneOrientBlit : IDisposable
{
    private readonly GpuDevice _gpu;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _cb;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _opaque;
    private ID3D11RasterizerState? _raster;
    private ID3D11ShaderResourceView? _canvasSrv;
    private IntPtr _canvasNative;
    private bool _ready;

    private struct Constants
    {
        public Vector4 CropUv; // x0,y0,x1,y1 in canvas UV
        public int Orient;
        public int _pad0, _pad1, _pad2;
    }

    public NdiZoneOrientBlit(GpuDevice gpu) => _gpu = gpu;

    public static NdiZoneOrient ResolveOrient(ZoneDefinition zone)
    {
        // Zone-id canonical unwrap: floor edge → bottom of NDI frame (same as 3D).
        return (NdiZoneOrient)WallFloorMapping.FloorBottomOrientCode(zone.Id);
    }

    public static (int OutW, int OutH) OutputSize(int cropW, int cropH, NdiZoneOrient orient)
    {
        bool swap = orient is NdiZoneOrient.Rot90Cw or NdiZoneOrient.Rot90Ccw;
        int w = swap ? cropH : cropW;
        int h = swap ? cropW : cropH;
        if ((w & 1) != 0) w--;
        return (w, h);
    }

    public void Blit(
        ID3D11Texture2D canvas,
        int canvasW,
        int canvasH,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        ID3D11Texture2D dest,
        int destW,
        int destH,
        NdiZoneOrient orient)
    {
        EnsurePipeline();
        if (!_ready || _vs is null || _ps is null || _cb is null)
            return;

        if (_canvasNative != canvas.NativePointer)
        {
            _canvasSrv?.Dispose();
            _canvasSrv = _gpu.Device.CreateShaderResourceView(canvas);
            _canvasNative = canvas.NativePointer;
        }

        if (_canvasSrv is null)
            return;

        using var rtv = _gpu.Device.CreateRenderTargetView(dest);
        var ctx = _gpu.Context;
        ctx.OMSetRenderTargets(rtv);
        ctx.OMSetBlendState(_opaque);
        ctx.OMSetDepthStencilState(null);
        ctx.RSSetState(_raster);
        ctx.RSSetViewport(new Viewport(0, 0, destW, destH));
        ctx.IASetInputLayout(null!);
        ctx.IASetVertexBuffer(0, null!, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetShaderResource(0, _canvasSrv);

        float cw = Math.Max(canvasW, 1);
        float ch = Math.Max(canvasH, 1);
        var constants = new Constants
        {
            CropUv = new Vector4(
                cropX / cw,
                cropY / ch,
                (cropX + cropW) / cw,
                (cropY + cropH) / ch),
            Orient = (int)orient
        };
        ctx.UpdateSubresource(constants, _cb);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.Draw(3, 0);

        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private void EnsurePipeline()
    {
        if (_ready)
            return;

        const string hlsl = """
            Texture2D src : register(t0);
            SamplerState samp : register(s0);
            cbuffer C : register(b0) {
                float4 CropUv;
                int Orient;
                int pad0, pad1, pad2;
            };

            void VSMain(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0)
            {
                float2 p = float2((id << 1) & 2, id & 2);
                uv = p;
                pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
            }

            float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                float u = uv.x;
                float v = uv.y;
                float su, sv;
                // Map output UV → source AABB UV (Y-down). Floor edge ends at v=1.
                if (Orient == 1) { // 180
                    su = 1 - u; sv = 1 - v;
                } else if (Orient == 2) { // 90 CW: right → bottom
                    su = v; sv = 1 - u;
                } else if (Orient == 3) { // 90 CCW: left → bottom
                    su = 1 - v; sv = u;
                } else {
                    su = u; sv = v;
                }
                float2 srcUv = float2(
                    lerp(CropUv.x, CropUv.z, su),
                    lerp(CropUv.y, CropUv.w, sv));
                return src.SampleLevel(samp, srcUv, 0);
            }
            """;

        try
        {
            var vsBlob = Compiler.Compile(hlsl, "VSMain", "ndi_zone_orient.hlsl", "vs_4_0");
            var psBlob = Compiler.Compile(hlsl, "PSMain", "ndi_zone_orient.hlsl", "ps_4_0");
            _vs = _gpu.Device.CreateVertexShader(vsBlob.Span);
            _ps = _gpu.Device.CreatePixelShader(psBlob.Span);
            int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<Constants>() + 15) / 16 * 16;
            _cb = _gpu.Device.CreateBuffer((uint)cbSize, BindFlags.ConstantBuffer, ResourceUsage.Default);
            _sampler = _gpu.Device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp
            });
            var blend = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = false,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
            _opaque = _gpu.Device.CreateBlendState(blend);
            _raster = _gpu.Device.CreateRasterizerState(new RasterizerDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                FrontCounterClockwise = false,
                DepthClipEnable = false
            });
            _ready = true;
        }
        catch
        {
            DisposePipeline();
            _ready = false;
        }
    }

    private void DisposePipeline()
    {
        _canvasSrv?.Dispose();
        _canvasSrv = null;
        _canvasNative = IntPtr.Zero;
        _vs?.Dispose();
        _vs = null;
        _ps?.Dispose();
        _ps = null;
        _cb?.Dispose();
        _cb = null;
        _sampler?.Dispose();
        _sampler = null;
        _opaque?.Dispose();
        _opaque = null;
        _raster?.Dispose();
        _raster = null;
    }

    public void Dispose()
    {
        DisposePipeline();
        _ready = false;
    }
}
