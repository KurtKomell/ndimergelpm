using System.Text;
using Vortice;
using Vortice.DXGI;

namespace NdiMerger.Sources;

/// <summary>
/// Finds an Intel (or other non-NVIDIA) adapter for WebView2 / browser capture,
/// so the main NVIDIA device can stay focused on the pixelmap compositor.
/// </summary>
internal static class BrowserGpuSelector
{
    public const uint VendorIntel = 0x8086;
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorMicrosoftBasic = 0x1414;

    public readonly struct Selection
    {
        public required string Name { get; init; }
        public required uint VendorId { get; init; }
        public required uint DeviceId { get; init; }
        public required Luid Luid { get; init; }
        public required bool IsIntel { get; init; }
        public bool IsNvidia => VendorId == VendorNvidia;
    }

    /// <summary>
    /// Prefer Intel; else any non-NVIDIA / non-BasicRender adapter; else null.
    /// Caller must Dispose the returned adapter.
    /// </summary>
    public static IDXGIAdapter1? TryAcquireBrowserAdapter(out Selection selection)
    {
        selection = default;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? intel = null;
        AdapterDescription1 intelDesc = default;
        IDXGIAdapter1? other = null;
        AdapterDescription1 otherDesc = default;

        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;

            var desc = adapter.Description1;
            if (desc.VendorId == VendorMicrosoftBasic)
            {
                adapter.Dispose();
                continue;
            }

            if (desc.VendorId == VendorIntel)
            {
                intel?.Dispose();
                intel = adapter;
                intelDesc = desc;
                continue;
            }

            if (desc.VendorId != VendorNvidia && other is null)
            {
                other = adapter;
                otherDesc = desc;
                continue;
            }

            adapter.Dispose();
        }

        if (intel is not null)
        {
            other?.Dispose();
            selection = ToSelection(intelDesc, isIntel: true);
            return intel;
        }

        if (other is not null)
        {
            selection = ToSelection(otherDesc, isIntel: false);
            return other;
        }

        return null;
    }

    public static bool IsSameAdapter(IDXGIAdapter1 a, IDXGIAdapter1 b)
        => a.Description1.Luid == b.Description1.Luid;

    public static string BuildWebViewGpuArguments(in Selection selection)
    {
        var sb = new StringBuilder();
        // Pin via adapter LUID only. Chromium --gpu-testing-* switches are for
        // test harnesses and can cause visible WebView stutter on real GPUs.
        sb.Append("--use-angle=d3d11 ");
        sb.Append("--enable-gpu-rasterization ");
        sb.Append("--ignore-gpu-blocklist ");
        sb.Append("--disable-gpu-vsync ");
        sb.Append($"--adapter-luid={selection.Luid.HighPart},{unchecked((int)selection.Luid.LowPart)}");
        return sb.ToString();
    }

    public static string ModeLabel(in Selection selection)
    {
        if (selection.IsIntel) return "Intel";
        if (selection.IsNvidia) return "NVIDIA";
        var name = selection.Name;
        return string.IsNullOrWhiteSpace(name) ? $"0x{selection.VendorId:X}" : name;
    }

    private static Selection ToSelection(in AdapterDescription1 desc, bool isIntel) => new()
    {
        Name = (desc.Description ?? string.Empty).TrimEnd('\0'),
        VendorId = desc.VendorId,
        DeviceId = desc.DeviceId,
        Luid = desc.Luid,
        IsIntel = isIntel
    };
}
