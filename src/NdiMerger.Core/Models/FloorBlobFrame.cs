using System.Numerics;

namespace NdiMerger.Core.Models;

public readonly struct FloorBlob
{
    public required Vector2 Center { get; init; }
    public required float RadiusPx { get; init; }
    public required float Alpha { get; init; }

    /// <summary>When set, overrides <see cref="FloorBlobSettings.Color"/> (e.g. red merge groups).</summary>
    public Vector4? Color { get; init; }
}

public sealed class FloorBlobFrame
{
    public IReadOnlyList<FloorBlob> Blobs { get; init; } = [];
    public IReadOnlyList<FloorBlob> Trail { get; init; } = [];
    public IReadOnlyList<Vector2> DebugPoints { get; init; } = [];
    public Vector2? DebugSensor { get; init; }

    public static FloorBlobFrame Empty { get; } = new();
}

public readonly struct FloorBlobSettings
{
    public bool Enabled { get; init; }
    public float Opacity { get; init; }
    public float BlurPixels { get; init; }
    public float Softness { get; init; }
    public Vector4 Color { get; init; }

    public static FloorBlobSettings Default { get; } = new()
    {
        Enabled = true,
        Opacity = 0.9f,
        BlurPixels = 1.5f,
        Softness = 0.65f,
        Color = new Vector4(0.2f, 0.5f, 1f, 1f)
    };
}
