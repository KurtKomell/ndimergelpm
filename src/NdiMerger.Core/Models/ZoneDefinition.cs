namespace NdiMerger.Core.Models;

public sealed class PixelMapDefinition
{
    public string Name { get; set; } = "Pixelmap";
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public string? BackgroundImage { get; set; }
    public List<ZoneDefinition> Zones { get; set; } = [];
}

public sealed class ZoneDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float ContentWidth { get; set; }
    public float ContentHeight { get; set; }
    public float RotationDegrees { get; set; }
    public string? SingleImage { get; set; }
}
