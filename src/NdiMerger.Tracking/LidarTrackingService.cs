using NdiMerger.Core.Models;

namespace NdiMerger.Tracking;

public sealed class LidarTrackingService : IDisposable
{
    private readonly RpLidarClient _client = new();
    private readonly PersonTracker _tracker = new();
    private LidarSettings _settings = new();
    private ZoneDefinition? _floor;
    private volatile FloorBlobFrame _latestFrame = FloorBlobFrame.Empty;
    private bool _disposed;

    public LidarStatus Status => _client.Status;
    public FloorBlobFrame LatestFrame => _latestFrame;
    public LidarSettings Settings => _settings;

    public static IReadOnlyList<string> ListPorts() => RpLidarClient.ListPorts();

    public void ApplySettings(LidarSettings settings, ZoneDefinition? floor)
    {
        _settings = settings;
        _floor = floor;
    }

    public void Connect(string portName)
    {
        _tracker.ResetBackground();
        _client.Connect(portName);
        _settings.ComPort = portName;
    }

    public void Disconnect() => _client.Disconnect();

    public void ResetBackground() => _tracker.ResetBackground();

    public FloorBlobFrame UpdateFrame()
    {
        if (_floor is null || !_settings.Enabled)
        {
            _latestFrame = FloorBlobFrame.Empty;
            return _latestFrame;
        }

        _latestFrame = _tracker.Update(_client.LatestScan, _settings, _floor, DateTime.UtcNow);
        return _latestFrame;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }
}
