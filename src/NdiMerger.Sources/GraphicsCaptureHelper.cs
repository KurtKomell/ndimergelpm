using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NdiMerger.Sources;

/// <summary>
/// WinRT Graphics Capture helpers for HWND → D3D11 textures (no JPEG round-trip).
/// </summary>
internal static class GraphicsCaptureHelper
{
    private static readonly Guid IInspectableIid = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IDirect3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [DllImport("d3d11.dll", ExactSpelling = true, EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var unk);
        if (hr < 0 || unk == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(unk);
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = IInspectableIid;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        if (itemPtr == IntPtr.Zero)
            throw new InvalidOperationException("GraphicsCaptureItem.CreateForWindow failed.");
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    public static ID3D11Texture2D GetTexture2D(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        // Vortice GUID for ID3D11Texture2D
        var texIid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        IntPtr texPtr = access.GetInterface(ref texIid);
        if (texPtr == IntPtr.Zero)
            throw new InvalidOperationException("Failed to get ID3D11Texture2D from capture surface.");
        return new ID3D11Texture2D(texPtr);
    }

    public static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // IsBorderRequired needs a newer Windows SDK TFM; ignore if unavailable.
        try
        {
            var prop = session.GetType().GetProperty("IsBorderRequired");
            prop?.SetValue(session, false);
        }
        catch
        {
            // Older Windows builds
        }
    }
}
