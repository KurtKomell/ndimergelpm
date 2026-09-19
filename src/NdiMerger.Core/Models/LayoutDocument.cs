using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdiMerger.Core.Models;

public sealed class LayoutDocument
{
    public string Version { get; set; } = "1.5";
    public string OutputName { get; set; } = "MAM-Pixelmap";
    public bool ShowBackgroundInOutput { get; set; } = false;
    public bool ShowBackgroundInPreview { get; set; } = true;
    public bool ShowLayerOverlays { get; set; } = true;
    public bool NdiSending { get; set; } = true;
    /// <summary>Full-frame NDI output scale as percent of canvas (10–100). Width kept even for UYVY.</summary>
    public double NdiOutputScalePercent { get; set; } = 100;
    /// <summary>Full-frame and zone NDI / compose cadence: 30 or 60.</summary>
    public int NdiOutputFps { get; set; } = 30;
    public List<string> EnabledNdiZoneIds { get; set; } = [];
    public string? NdiAdapterId { get; set; }
    public string? NdiAdapterIp { get; set; }
    /// <summary>WASAPI capture device ID for NDI audio mux on the full-frame sender.</summary>
    public string? AudioDeviceId { get; set; }
    /// <summary>When true, capture the selected audio device and send on full-frame NDI.</summary>
    public bool AudioEnabled { get; set; }
    /// <summary>Ausgangslautstärke as percent of unity (0–200). Default 100.</summary>
    public double AudioOutputGainPercent { get; set; } = 100;
    public ScaleMode SelectedScaleMode { get; set; } = ScaleMode.Native;
    public string? SelectedZoneId { get; set; }
    public string? SelectedLayerKey { get; set; }
    public double CarouselDurationSeconds { get; set; } = 1.0;
    public double FadeDurationSeconds { get; set; } = 2.0;
    public WallCarouselDirection CarouselDirection { get; set; } = WallCarouselDirection.WestToOst;
    public bool? FloorReflectionEnabled { get; set; }
    public float? FloorReflectionOpacity { get; set; }
    public float? FloorReflectionBlur { get; set; }
    public float? FloorReflectionLength { get; set; }
    public float? FloorReflectionAngle { get; set; }
    public float? FloorReflectionFadeStart { get; set; }
    public bool? Room3DEnabled { get; set; }
    public float? RoomAssembleT { get; set; }
    public bool? RoomPhotoOverlayEnabled { get; set; }
    public float? RoomPhotoOverlayOpacity { get; set; }
    public List<RoomWallOffsetEntry> RoomWallOffsets { get; set; } = [];
    public LidarSettings? Lidar { get; set; }
    public List<LayoutGroupEntry> Groups { get; set; } = [];
    public List<LayoutLayerEntry> Layers { get; set; } = [];
}

public sealed class RoomWallOffsetEntry
{
    public string ZoneId { get; set; } = "";
    public float OffsetX { get; set; }
    public float OffsetZ { get; set; }
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

public sealed class LayoutGroupEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Group";
    public bool Visible { get; set; } = true;
    public float? Opacity { get; set; }
    public bool IsExpanded { get; set; } = true;
    public int SortOrder { get; set; }
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
    /// <summary>3D-only wall content rotation for this layer.</summary>
    public float Room3DRotationDegrees { get; set; }
    /// <summary>3D-only top↔bottom UV flip for this layer.</summary>
    public bool Room3DFlipVertical { get; set; }
    public float Opacity { get; set; } = 1f;
    public ScaleMode ScaleMode { get; set; } = ScaleMode.Native;
    public string? ZoneId { get; set; }
    public string? GroupId { get; set; }
    public bool Visible { get; set; } = true;
    public int ZIndex { get; set; }
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }
    public bool BlackKeyEnabled { get; set; }
    public float BlackKeyThreshold { get; set; } = 0.08f;
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropW { get; set; }
    public int CropH { get; set; }
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
        IEnumerable<LayerGroup> groups,
        string outputName,
        bool showBgOutput,
        bool showBgPreview = true,
        bool showLayerOverlays = true,
        bool ndiSending = true,
        double ndiOutputScalePercent = 100,
        int ndiOutputFps = 30,
        ScaleMode selectedScaleMode = ScaleMode.Native,
        string? selectedZoneId = null,
        string? selectedLayerKey = null,
        double carouselDurationSeconds = 1.0,
        double fadeDurationSeconds = 2.0,
        WallCarouselDirection carouselDirection = WallCarouselDirection.WestToOst,
        bool floorReflectionEnabled = true,
        float floorReflectionOpacity = 0.45f,
        float floorReflectionBlur = 8f,
        float floorReflectionLength = 0.4f,
        float floorReflectionAngle = 0.35f,
        float floorReflectionFadeStart = 0f,
        LidarSettings? lidar = null,
        string? ndiAdapterId = null,
        string? ndiAdapterIp = null,
        IEnumerable<string>? enabledNdiZoneIds = null,
        bool room3DEnabled = false,
        float roomAssembleT = 1f,
        bool roomPhotoOverlayEnabled = false,
        float roomPhotoOverlayOpacity = 0.35f,
        IEnumerable<RoomWallOffsetEntry>? roomWallOffsets = null,
        string? audioDeviceId = null,
        bool audioEnabled = false,
        double audioOutputGainPercent = 100)
    {
        return new LayoutDocument
        {
            Version = "1.5",
            OutputName = outputName,
            ShowBackgroundInOutput = showBgOutput,
            ShowBackgroundInPreview = showBgPreview,
            ShowLayerOverlays = showLayerOverlays,
            NdiSending = ndiSending,
            NdiOutputScalePercent = Math.Clamp(ndiOutputScalePercent, 10, 100),
            NdiOutputFps = ndiOutputFps >= 45 ? 60 : 30,
            EnabledNdiZoneIds = enabledNdiZoneIds?.ToList() ?? [],
            NdiAdapterId = ndiAdapterId,
            NdiAdapterIp = ndiAdapterIp,
            AudioDeviceId = audioDeviceId,
            AudioEnabled = audioEnabled,
            AudioOutputGainPercent = Math.Clamp(audioOutputGainPercent <= 0 ? 100 : audioOutputGainPercent, 0, 200),
            SelectedScaleMode = selectedScaleMode,
            SelectedZoneId = selectedZoneId,
            SelectedLayerKey = selectedLayerKey,
            CarouselDurationSeconds = carouselDurationSeconds,
            FadeDurationSeconds = fadeDurationSeconds,
            CarouselDirection = carouselDirection,
            FloorReflectionEnabled = floorReflectionEnabled,
            FloorReflectionOpacity = floorReflectionOpacity,
            FloorReflectionBlur = floorReflectionBlur,
            FloorReflectionLength = floorReflectionLength,
            FloorReflectionAngle = floorReflectionAngle,
            FloorReflectionFadeStart = floorReflectionFadeStart,
            Room3DEnabled = room3DEnabled,
            RoomAssembleT = roomAssembleT,
            RoomPhotoOverlayEnabled = roomPhotoOverlayEnabled,
            RoomPhotoOverlayOpacity = roomPhotoOverlayOpacity,
            RoomWallOffsets = roomWallOffsets?
                .Where(e => !string.IsNullOrWhiteSpace(e.ZoneId))
                .Select(e => new RoomWallOffsetEntry
                {
                    ZoneId = e.ZoneId,
                    OffsetX = e.OffsetX,
                    OffsetZ = e.OffsetZ
                })
                .ToList() ?? [],
            Lidar = lidar,
            Groups = groups.OrderBy(g => g.SortOrder).Select(g => new LayoutGroupEntry
            {
                Id = g.Id.ToString("N"),
                Name = g.Name,
                Visible = g.Visible,
                Opacity = g.Opacity,
                IsExpanded = g.IsExpanded,
                SortOrder = g.SortOrder
            }).ToList(),
            Layers = layers.Select(l => new LayoutLayerEntry
            {
                Name = l.Name,
                SourceKind = l.SourceKind,
                SourceKey = l.SourceKey,
                X = l.X,
                Y = l.Y,
                Scale = l.Scale,
                RotationDegrees = l.RotationDegrees,
                Room3DRotationDegrees = l.Room3DRotationDegrees,
                Room3DFlipVertical = l.Room3DFlipVertical,
                Opacity = l.Opacity,
                ScaleMode = l.ScaleMode,
                ZoneId = l.ZoneId,
                GroupId = l.GroupId?.ToString("N"),
                Visible = l.Visible,
                ZIndex = l.ZIndex,
                NativeWidth = l.NativeWidth,
                NativeHeight = l.NativeHeight,
                BlackKeyEnabled = l.BlackKeyEnabled,
                BlackKeyThreshold = l.BlackKeyThreshold,
                CropX = l.CropX,
                CropY = l.CropY,
                CropW = l.CropW,
                CropH = l.CropH
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
