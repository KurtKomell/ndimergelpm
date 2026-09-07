using System.Numerics;

namespace NdiMerger.Core.Models;

public sealed class LidarSettings
{
    public bool Enabled { get; set; } = true;
    public string? ComPort { get; set; }
    public bool AutoConnect { get; set; }

    public float SensorXPx { get; set; }
    public float SensorYPx { get; set; }
    public float RotationDegrees { get; set; }
    public bool MirrorX { get; set; }
    public float PixelsPerMeter { get; set; } = 478f;
    /// <summary>Real-world floor width along canvas X (meters).</summary>
    public float FloorWidthMeters { get; set; } = 12f;
    /// <summary>Real-world floor length along canvas Y (meters).</summary>
    public float FloorLengthMeters { get; set; } = 12f;
    public float MinDistanceMm { get; set; } = 300f;
    public float MaxDistanceMm { get; set; } = 12000f;
    public bool ShowDebugPoints { get; set; } = true;

    public float ForegroundMarginMm { get; set; } = 200f;
    public float ClusterGapMm { get; set; } = 250f;
    public int MinClusterPoints { get; set; } = 3;
    public float MinPersonWidthMm { get; set; } = 70f;
    public float MaxPersonWidthMm { get; set; } = 900f;

    /// <summary>Distance a track must travel before it is drawn; keeps static furniture invisible.</summary>
    public float MinMovementMm { get; set; } = 300f;

    /// <summary>When two confirmed people are closer than this, they merge into one red pulse.</summary>
    public float MergeDistanceMeters { get; set; } = 0.5f;
    public int MinHits { get; set; } = 2;
    public int HoldMs { get; set; } = 500;
    public float SmoothingAlpha { get; set; } = 0.35f;
    public float MaxJumpPx { get; set; } = 120f;
    public int MinQuality { get; set; } = 10;

    public float BlobRadiusMeters { get; set; } = 0.45f;
    public float BlobSoftness { get; set; } = 0.65f;
    public float BlobOpacity { get; set; } = 0.9f;
    public float BlobBlurPixels { get; set; } = 1.5f;
    public float BlobColorR { get; set; } = 0.2f;
    public float BlobColorG { get; set; } = 0.5f;
    public float BlobColorB { get; set; } = 1f;
    public float TrailLengthSeconds { get; set; } = 1.5f;
    public float TrailStrength { get; set; } = 0.5f;

    public float BlobRadiusPx => BlobRadiusMeters * PixelsPerMeter;

    public FloorBlobSettings ToBlobSettings() => new()
    {
        Enabled = Enabled,
        Opacity = BlobOpacity,
        BlurPixels = BlobBlurPixels,
        Softness = BlobSoftness,
        Color = new Vector4(BlobColorR, BlobColorG, BlobColorB, 1f)
    };

    public Vector2 ToCanvas(float angleDeg, float distanceMm)
    {
        float a = (angleDeg + RotationDegrees) * MathF.PI / 180f;
        float rPx = distanceMm / 1000f * PixelsPerMeter;
        float x = MathF.Sin(a) * rPx * (MirrorX ? -1f : 1f);
        float y = -MathF.Cos(a) * rPx;
        return new Vector2(SensorXPx + x, SensorYPx + y);
    }

    public static LidarSettings CreateDefaultForFloor(ZoneDefinition floor)
    {
        return new LidarSettings
        {
            SensorXPx = floor.X + floor.Width * 0.5f,
            SensorYPx = floor.Y + floor.Height * 0.5f,
            PixelsPerMeter = floor.Width / 12f,
            FloorWidthMeters = 12f,
            FloorLengthMeters = floor.Height / MathF.Max(floor.Width / 12f, 1f)
        };
    }
}
