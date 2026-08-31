using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace NdiMerger.Core.Gpu;

public sealed class GpuDevice : IDisposable
{
    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D11Multithread? _multithread;

    /// <summary>Serializes immediate-context use across the render thread and browser upload workers.</summary>
    public object ContextLock { get; } = new();

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IntPtr NativeDevicePointer => Device.NativePointer;
    public string AdapterName { get; }
    public uint VendorId { get; }

    public GpuDevice()
    {
        _adapter = SelectAdapter(out var adapterName, out var vendorId);
        AdapterName = adapterName;
        VendorId = vendorId;

        var result = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out var device,
            out _,
            out var context);

        if (result.Failure || device is null || context is null)
            throw new InvalidOperationException($"Failed to create D3D11 device on '{AdapterName}': {result}");

        Device = device;
        Context = context;

        // Allow browser upload threads to call into the same immediate context safely.
        _multithread = Device.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
    }

    private static IDXGIAdapter1 SelectAdapter(out string name, out uint vendorId)
    {
        name = "unknown";
        vendorId = 0;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? nvidia = null;
        IDXGIAdapter1? best = null;
        ulong bestMem = 0;
        var others = new List<IDXGIAdapter1>();

        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;

            var desc = adapter.Description1;

            if (desc.VendorId == 0x10DE)
                nvidia = adapter;
            else
                others.Add(adapter);

            if (desc.DedicatedVideoMemory > bestMem)
            {
                bestMem = desc.DedicatedVideoMemory;
                best = adapter;
            }
        }

        var chosen = nvidia ?? best;
        if (chosen is null)
            throw new InvalidOperationException("No DXGI adapter found.");

        foreach (var a in others)
        {
            if (!ReferenceEquals(a, chosen))
                a.Dispose();
        }

        if (best is not null && !ReferenceEquals(best, chosen) && !ReferenceEquals(best, nvidia))
            best.Dispose();

        var chosenDesc = chosen.Description1;
        name = chosenDesc.Description;
        vendorId = chosenDesc.VendorId;

        return chosen;
    }

    public ID3D11Texture2D CreateTexture(int width, int height, Format format = Format.B8G8R8A8_UNorm,
        BindFlags bind = BindFlags.ShaderResource | BindFlags.RenderTarget,
        ResourceUsage usage = ResourceUsage.Default,
        CpuAccessFlags cpuAccess = CpuAccessFlags.None,
        ResourceOptionFlags options = ResourceOptionFlags.None)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpuAccess,
            MiscFlags = options
        };
        return Device.CreateTexture2D(desc);
    }

    public ID3D11Texture2D CreateStagingTexture(int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        return CreateTexture(width, height, format, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
    }

    public void Dispose()
    {
        _multithread?.Dispose();
        Context.Dispose();
        Device.Dispose();
        _adapter.Dispose();
    }
}
