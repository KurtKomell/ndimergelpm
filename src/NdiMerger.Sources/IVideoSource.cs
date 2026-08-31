using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.Direct3D11;

namespace NdiMerger.Sources;

public interface IVideoSource : IDisposable
{
    string Key { get; }
    string DisplayName { get; }
    SourceKind Kind { get; }
    int Width { get; }
    int Height { get; }
    bool IsConnected { get; }
    bool HasNewFrame { get; }

    void Start(GpuDevice gpu);
    void Update();
    ID3D11ShaderResourceView? GetSrv();
}

public sealed class DiscoveredSource
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required SourceKind Kind { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// Uploads BGRA frames from a CPU buffer into a D3D11 texture.
/// </summary>
public abstract class BgraUploadSource : IVideoSource
{
    private readonly object _frameLock = new();
    private byte[]? _pendingFrame;
    private int _pendingW;
    private int _pendingH;
    private bool _hasPending;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private GpuDevice? _gpu;
    private bool _newFrame;

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract SourceKind Kind { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public abstract bool IsConnected { get; }
    public bool HasNewFrame => _newFrame;

    public virtual void Start(GpuDevice gpu)
    {
        _gpu = gpu;
    }

    protected void SubmitFrame(byte[] bgra, int width, int height, int stride)
    {
        byte[] packed;
        if (stride != width * 4)
        {
            packed = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(bgra, y * stride, packed, y * width * 4, width * 4);
        }
        else
        {
            packed = (byte[])bgra.Clone();
        }

        lock (_frameLock)
        {
            _pendingFrame = packed;
            _pendingW = width;
            _pendingH = height;
            _hasPending = true;
        }
    }

    public void Update()
    {
        _newFrame = false;
        if (_gpu is null) return;

        byte[]? frame;
        int w, h;
        lock (_frameLock)
        {
            if (!_hasPending || _pendingFrame is null) return;
            frame = _pendingFrame;
            w = _pendingW;
            h = _pendingH;
            _hasPending = false;
            _pendingFrame = null;
        }

        EnsureTexture(w, h);

        // DirectShow RGB32 commonly leaves alpha at 0; compositor treats alpha as opacity.
        for (int i = 3; i < frame.Length; i += 4)
            frame[i] = 255;

        unsafe
        {
            fixed (byte* ptr = frame)
            {
                _gpu.Context.UpdateSubresource(_texture!, 0, null, (IntPtr)ptr, (uint)(w * 4), (uint)frame.Length);
            }
        }

        Width = w;
        Height = h;
        _newFrame = true;
    }

    private void EnsureTexture(int w, int h)
    {
        if (_texture is not null && Width == w && Height == h) return;
        _srv?.Dispose();
        _texture?.Dispose();
        _texture = _gpu!.CreateTexture(w, h);
        _srv = _gpu.Device.CreateShaderResourceView(_texture);
        Width = w;
        Height = h;
    }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    public virtual void Dispose()
    {
        _srv?.Dispose();
        _texture?.Dispose();
    }
}
