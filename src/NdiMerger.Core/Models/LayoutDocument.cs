using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdiMerger.Core.Models;

public sealed class LayoutDocument
{
    public string Version { get; set; } = "1.1";
    public string OutputName { get; set; } = "MAM-Pixelmap";
    public bool ShowBackgroundInOutput { get; set; } = false;
    public bool ShowBackgroundInPreview { get; set; } = true;
    public bool NdiSending { get; set; } = true;
    public ScaleMode SelectedScaleMode { get; set; } = ScaleMode.Native;
    public string? SelectedZoneId { get; set; }
    public string? SelectedLayerKey { get; set; }
    public double CarouselDurationSeconds { get; set; } = 1.0;
    public WallCarouselDirection CarouselDirection { get; set; } = WallCarouselDirection.WestToOst;
    public bool? FloorReflectionEnabled { get; set; }
    public float? FloorReflectionOpacity { get; set; }
    public float? FloorReflectionBlur { get; set; }
    public float? FloorReflectionLength { get; set; }
    public float? FloorReflectionAngle { get; set; }
    public float? FloorReflectionFadeStart { get; set; }
    public List<LayoutLayerEntry> Layers { get; set; } = [];
}

public sealed class AppSession
{
    public LayoutDocument Layout { get; set; } = new();
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int WindowState { get; set; }
}

public sealed class LayoutLayerEntry
{
    public string Name { get; set; } = "";
    public SourceKind SourceKind { get; set; }
    public string SourceKey { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Scale { get; set; } = 1f;
    public float RotationDegrees { get; set; }
    public float Opacity { get; set; } = 1f;
    public ScaleMode ScaleMode { get; set; } = ScaleMode.Native;
    public string? ZoneId { get; set; }
    public bool Visible { get; set; } = true;
    public int ZIndex { get; set; }
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }
}

public static class LayoutSerializer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Save(LayoutDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static LayoutDocument Load(string json) =>
        JsonSerializer.Deserialize<LayoutDocument>(json, Options) ?? new LayoutDocument();

    public static LayoutDocument FromLayers(
        IEnumerable<CompositionLayer> layers,
        string outputName,
        bool showBgOutput,
        bool showBgPreview = true,
        bool ndiSending = true,
        ScaleMode selectedScaleMode = ScaleMode.Native,
        string? selectedZoneId = null,
        string? selectedLayerKey = null,
        double carouselDurationSeconds = 1.0,
        WallCarouselDirection carouselDirection = WallCarouselDirection.WestToOst,
        bool floorReflectionEnabled = true,
        float floorReflectionOpacity = 0.45f,
        float floorReflectionBlur = 8f,
        float floorReflectionLength = 0.4f,
        float floorReflectionAngle = 0.35f,
        float floorReflectionFadeStart = 0f)
    {
        return new LayoutDocument
        {
            OutputName = outputName,
            ShowBackgroundInOutput = showBgOutput,
            ShowBackgroundInPreview = showBgPreview,
            NdiSending = ndiSending,
            SelectedScaleMode = selectedScaleMode,
            SelectedZoneId = selectedZoneId,
            SelectedLayerKey = selectedLayerKey,
            CarouselDurationSeconds = carouselDurationSeconds,
            CarouselDirection = carouselDirection,
            FloorReflectionEnabled = floorReflectionEnabled,
            FloorReflectionOpacity = floorReflectionOpacity,
            FloorReflectionBlur = floorReflectionBlur,
            FloorReflectionLength = floorReflectionLength,
            FloorReflectionAngle = floorReflectionAngle,
            FloorReflectionFadeStart = floorReflectionFadeStart,
            Layers = layers.Select(l => new LayoutLayerEntry
            {
                Name = l.Name,
                SourceKind = l.SourceKind,
                SourceKey = l.SourceKey,
                X = l.X,
                Y = l.Y,
                Scale = l.Scale,
                RotationDegrees = l.RotationDegrees,
                Opacity = l.Opacity,
                ScaleMode = l.ScaleMode,
                ZoneId = l.ZoneId,
                Visible = l.Visible,
                ZIndex = l.ZIndex,
                NativeWidth = l.NativeWidth,
                NativeHeight = l.NativeHeight
            }).ToList()
        };
    }

    public static string LayerKey(SourceKind kind, string sourceKey) => $"{kind}:{sourceKey}";
}

public static class SessionStore
{
    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NdiMergerLPM",
            "last-session.json");

    public static AppSession? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<AppSession>(File.ReadAllText(FilePath), LayoutSerializer.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AppSession session)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(session, LayoutSerializer.Options));
        }
        catch
        {
            // ignore persistence errors on shutdown
        }
    }
}
