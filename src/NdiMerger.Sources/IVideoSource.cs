using System.Runtime.InteropServices;
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
    private byte[]? _uploadFrame;
    private int _pendingW;
    private int _pendingH;
    private bool _hasPending;

    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    private GpuDevice? _gpu;
    private bool _newFrame;

    /// <summary>
    /// DirectShow RGB32 often stores 0 in alpha. Browser/NDI BGRX already has opaque alpha.
    /// Walking every pixel on the render thread hitchs the compositor without showing as GPU load.
    /// </summary>
    protected bool ForceOpaqueAlpha { get; set; } = true;

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract SourceKind Kind { get; }
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public abstract bool IsConnected { get; }
    public bool HasNewFrame => _newFrame;

    public virtual void Start(GpuDevice gpu)
    {
        _gpu = gpu;
    }

    protected void SubmitFrame(byte[] bgra, int width, int height, int stride)
    {
        unsafe
        {
            fixed (byte* ptr = bgra)
                SubmitFrameFromPtr((IntPtr)ptr, width, height, stride);
        }
    }

    protected void SubmitFrameFromPtr(IntPtr data, int width, int height, int stride, bool flipVertical = false)
    {
        if (data == IntPtr.Zero || width <= 0 || height <= 0) return;

        int packedStride = width * 4;
        int len = packedStride * height;
        lock (_frameLock)
        {
            if (_pendingFrame is null || _pendingFrame.Length != len)
                _pendingFrame = new byte[len];

            if (flipVertical)
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data + (height - 1 - y) * stride, _pendingFrame, y * packedStride, packedStride);
            }
            else if (stride == packedStride)
            {
                Marshal.Copy(data, _pendingFrame, 0, len);
            }
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(data + y * stride, _pendingFrame, y * packedStride, packedStride);
            }

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
            w = _pendingW;
            h = _pendingH;
            // Swap so the capture thread can fill _pendingFrame during GPU upload.
            frame = _pendingFrame;
            var recycled = _uploadFrame;
            _pendingFrame = recycled is not null && recycled.Length == frame.Length
                ? recycled
                : new byte[frame.Length];
            _uploadFrame = frame;
            _hasPending = false;
        }

        EnsureTexture(w, h);

        int pixelBytes = w * h * 4;
        if (ForceOpaqueAlpha)
        {
            for (int i = 3; i < pixelBytes; i += 4)
                frame[i] = 255;
        }

        unsafe
        {
            fixed (byte* ptr = frame)
            {
                _gpu.Context.UpdateSubresource(_texture!, 0, null, (IntPtr)ptr, (uint)(w * 4), (uint)pixelBytes);
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
