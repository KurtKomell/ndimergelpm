using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.Direct3D11;

namespace NdiMerger.Sources;

/// <summary>
/// Static solid-color texture (e.g. black matte) usable like any other input layer.
/// </summary>
public sealed class SolidColorVideoSource : IVideoSource
{
    public const string BlackKey = "black";
    public const int DefaultWidth = 1920;
    public const int DefaultHeight = 1080;

    private readonly byte _b;
    private readonly byte _g;
    private readonly byte _r;
    private readonly byte _a;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;

    public SolidColorVideoSource(string key, string displayName, byte b, byte g, byte r, byte a = 255,
        int width = DefaultWidth, int height = DefaultHeight)
    {
        Key = key;
        DisplayName = displayName;
        _b = b;
        _g = g;
        _r = r;
        _a = a;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public string Key { get; }
    public string DisplayName { get; }
    public SourceKind Kind => SourceKind.Solid;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsConnected => _srv is not null;
    public bool HasNewFrame => false;

    public static SolidColorVideoSource CreateBlack() =>
        new(BlackKey, "Black Matte", 0, 0, 0, 255);

    public static IReadOnlyList<DiscoveredSource> List() =>
    [
        new DiscoveredSource
        {
            Key = BlackKey,
            DisplayName = "Black Matte",
            Kind = SourceKind.Solid,
            Width = DefaultWidth,
            Height = DefaultHeight
        }
    ];

    public void Start(GpuDevice gpu)
    {
        _srv?.Dispose();
        _texture?.Dispose();

        var pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = _b;
            pixels[i + 1] = _g;
            pixels[i + 2] = _r;
            pixels[i + 3] = _a;
        }

        _texture = gpu.CreateTexture(Width, Height);
        unsafe
        {
            fixed (byte* ptr = pixels)
            {
                gpu.Context.UpdateSubresource(_texture, 0, null, (IntPtr)ptr, (uint)(Width * 4), (uint)pixels.Length);
            }
        }

        _srv = gpu.Device.CreateShaderResourceView(_texture);
    }

    public void Update() { }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    public void Dispose()
    {
        _srv?.Dispose();
        _texture?.Dispose();
        _srv = null;
        _texture = null;
    }
}
