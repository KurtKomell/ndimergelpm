using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;
using Vortice.Direct3D11;

namespace NdiMerger.Output;

/// <summary>
/// Lazy per-zone NDI crop from the full pixelmap canvas.
/// Output is oriented so the floor-adjacent edge is at the bottom of the frame
/// (floor: nord edge at bottom). Composition/layers are unchanged.
/// </summary>
public sealed class NdiZoneStream : IDisposable
{
    public static readonly (string ZoneId, string NameSuffix, string DisplayName)[] Catalog =
    [
        ("wall_sud", "Sud", "Wall Sud"),
        ("wall_est", "Est", "Wall Est"),
        ("wall_west_1", "West-1", "Wall West 1"),
        ("wall_west_2", "West-2", "Wall West 2"),
        ("wall_west_3", "West-3", "Wall West 3"),
        ("wall_stage", "Stage", "Wall Stage"),
        ("floor", "Floor", "Floor")
    ];

    private readonly GpuDevice _gpu;
    private readonly string _zoneId;
    private readonly string _nameSuffix;
    private readonly string _displayName;
    private readonly NdiZoneOrientBlit _blit;
    private int _cropX;
    private int _cropY;
    private int _cropW;
    private int _cropH;
    private int _outW;
    private int _outH;
    private NdiZoneOrient _orient;
    private NdiOutputSender? _sender;
    private ID3D11Texture2D? _outTexture;
    private bool _disposed;

    public string ZoneId => _zoneId;
    public string DisplayName => _displayName;
    public string NameSuffix => _nameSuffix;
    public int CropWidth => _outW;
    public int CropHeight => _outH;
    public bool IsActive => _sender is { IsActive: true };
    public string CheckboxLabel => $"{_displayName} ({_outW}×{_outH})";
    public NdiOutputSender? Sender => _sender;

    public NdiZoneStream(GpuDevice gpu, string zoneId, string nameSuffix, string displayName)
    {
        _gpu = gpu;
        _zoneId = zoneId;
        _nameSuffix = nameSuffix;
        _displayName = displayName;
        _blit = new NdiZoneOrientBlit(gpu);
    }

    public bool TryBindZone(ZoneDefinition zone)
    {
        if (!zone.Id.Equals(_zoneId, StringComparison.OrdinalIgnoreCase))
            return false;

        int x = (int)MathF.Round(zone.X);
        int y = (int)MathF.Round(zone.Y);
        int w = (int)MathF.Round(zone.Width);
        int h = (int)MathF.Round(zone.Height);
        if (w <= 0 || h <= 0)
            return false;

        if ((w & 1) != 0)
            w--;

        _cropX = x;
        _cropY = y;
        _cropW = w;
        _cropH = h;
        _orient = NdiZoneOrientBlit.ResolveOrient(zone);
        (_outW, _outH) = NdiZoneOrientBlit.OutputSize(w, h, _orient);
        return _outW > 0 && _outH > 0;
    }

    public void EnsureStarted(string outputBaseName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_outW <= 0 || _outH <= 0)
            throw new InvalidOperationException($"Zone '{_zoneId}' is not bound.");

        if (_sender is { IsActive: true } &&
            _sender.OutputWidth == _outW &&
            _sender.OutputHeight == _outH &&
            _outTexture is not null)
            return;

        Stop();

        try
        {
            _outTexture = _gpu.CreateTexture(_outW, _outH);
            var ndiName = string.IsNullOrWhiteSpace(outputBaseName)
                ? _nameSuffix
                : $"{outputBaseName}-{_nameSuffix}";
            _sender = NdiOutputSender.CreateStandard(_gpu, ndiName);
            _sender.Initialize(_outW, _outH);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _sender?.Dispose();
        _sender = null;
        _outTexture?.Dispose();
        _outTexture = null;
    }

    public void BeginShutdown() => _sender?.BeginShutdown();

    /// <summary>
    /// Orient+crop from the full canvas into the zone NDI texture, then send.
    /// Must run on the compose thread under <see cref="GpuDevice.ContextLock"/>.
    /// </summary>
    public bool SendFromCanvas(ID3D11Texture2D canvasTexture, int canvasWidth, int canvasHeight)
    {
        if (_disposed || _sender is not { IsActive: true } || _outTexture is null)
            return false;

        _blit.Blit(
            canvasTexture, canvasWidth, canvasHeight,
            _cropX, _cropY, _cropW, _cropH,
            _outTexture, _outW, _outH, _orient);
        return _sender.SendFrame(_outTexture);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _blit.Dispose();
    }
}
