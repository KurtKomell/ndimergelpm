using System.Runtime.InteropServices;
using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Spout2.NET;
using Vortice.Direct3D11;

namespace NdiMerger.Sources;

public sealed class SpoutSourceCatalog : IDisposable
{
    private readonly SpoutSenders _senders = new();

    public IReadOnlyList<DiscoveredSource> List()
    {
        try
        {
            return _senders.Names()
                .Select(n =>
                {
                    _senders.TryGetInfo(n, out var info);
                    var label = info.Width > 0
                        ? $"{n} ({info.Width}x{info.Height})"
                        : n;
                    return new DiscoveredSource
                    {
                        Key = n,
                        DisplayName = label,
                        Kind = SourceKind.Spout,
                        Width = info.Width,
                        Height = info.Height
                    };
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static bool TryGetSenderSize(string senderName, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var senders = new SpoutSenders();
            if (senders.TryGetInfo(senderName, out var info) && info.Width > 0 && info.Height > 0)
            {
                width = info.Width;
                height = info.Height;
                return true;
            }
        }
        catch { /* optional */ }

        return false;
    }

    public void Dispose() => _senders.Dispose();
}

/// <summary>
/// Receives Spout frames on the same D3D11 adapter (NVIDIA) and samples the sender texture directly.
/// </summary>
public sealed class SpoutVideoSource : IVideoSource
{
    private readonly string _senderName;
    private SpoutReceiver? _receiver;
    private ID3D11ShaderResourceView? _srv;
    private GpuDevice? _gpu;
    private IntPtr _boundTexturePtr;
    private bool _newFrame;

    public string Key => _senderName;
    public string DisplayName => _senderName;
    public SourceKind Kind => SourceKind.Spout;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsConnected => _receiver?.IsConnected ?? false;
    public bool HasNewFrame => _newFrame;

    public SpoutVideoSource(string senderName) => _senderName = senderName;

    public void Start(GpuDevice gpu)
    {
        _gpu = gpu;
        _receiver = new SpoutReceiver(gpu.NativeDevicePointer, _senderName);
    }

    public void Update()
    {
        _newFrame = false;
        if (_receiver is null || _gpu is null) return;

        if (!_receiver.Receive())
        {
            // Keep last SRV so compose + 3D walls still show the previous frame when
            // Spout briefly fails to deliver (common under load / sender hitch).
            return;
        }

        bool frameNew = _receiver.IsFrameNew;
        while (_receiver.IsFrameNew && _receiver.Receive())
            frameNew = true;

        var texturePtr = _receiver.Texture;
        if (texturePtr == IntPtr.Zero) return;

        int w = _receiver.SenderWidth;
        int h = _receiver.SenderHeight;
        if (w <= 0 || h <= 0)
            ReadTextureSize(texturePtr, out w, out h);

        if (w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }

        if (_receiver.IsUpdated || texturePtr != _boundTexturePtr || _srv is null)
            RecreateSrv(texturePtr);

        _newFrame = frameNew;
    }

    private void RecreateSrv(IntPtr texturePtr)
    {
        ReleaseSrv();
        _boundTexturePtr = texturePtr;

        var texture = new ID3D11Texture2D(texturePtr);
        try
        {
            _srv = _gpu!.Device.CreateShaderResourceView(texture);
        }
        finally
        {
            Marshal.AddRef(texturePtr);
            texture.Dispose();
        }
    }

    private static void ReadTextureSize(IntPtr texturePtr, out int width, out int height)
    {
        width = 0;
        height = 0;
        var texture = new ID3D11Texture2D(texturePtr);
        try
        {
            var desc = texture.Description;
            width = (int)desc.Width;
            height = (int)desc.Height;
        }
        finally
        {
            Marshal.AddRef(texturePtr);
            texture.Dispose();
        }
    }

    private void ReleaseSrv()
    {
        _srv?.Dispose();
        _srv = null;
        _boundTexturePtr = IntPtr.Zero;
    }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    public void Dispose()
    {
        ReleaseSrv();
        _receiver?.Dispose();
    }
}
