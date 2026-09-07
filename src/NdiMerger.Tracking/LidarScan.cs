namespace NdiMerger.Tracking;

public readonly struct LidarPoint
{
    public float AngleDegrees { get; init; }
    public float DistanceMm { get; init; }
    public byte Quality { get; init; }
}

public sealed class LidarScan
{
    public required IReadOnlyList<LidarPoint> Points { get; init; }
    public DateTime Timestamp { get; init; }
    public double Rpm { get; init; }
}

public enum LidarHealthStatus
{
    Unknown,
    Good,
    Warning,
    Error
}

public sealed class LidarStatus
{
    public bool IsConnected { get; set; }
    public bool IsScanning { get; set; }
    public LidarHealthStatus Health { get; set; }
    public double Rpm { get; set; }
    public int PointsPerRevolution { get; set; }
    public string? LastError { get; set; }
    public string? ComPort { get; set; }
    public byte ScanDataType { get; set; }
}
