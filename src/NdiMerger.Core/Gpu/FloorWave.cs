namespace NdiMerger.Core.Gpu;

/// <summary>
/// 3D-like swell that rolls from the stage wall across the floor
/// during a side-wall carousel cycle.
/// </summary>
public readonly struct FloorWaveSettings
{
    public bool Enabled { get; init; }
    /// <summary>0 at the stage seam, 1 after the swell has left the far side.</summary>
    public float Progress { get; init; }
    public float Amplitude { get; init; }
    public float Wavelength { get; init; }
    public float Opacity { get; init; }

    public static FloorWaveSettings Disabled { get; } = new() { Enabled = false };
}
