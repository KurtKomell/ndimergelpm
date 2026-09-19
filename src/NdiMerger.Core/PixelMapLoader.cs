using System.Text.Json;
using System.Text.Json.Serialization;
using NdiMerger.Core.Models;

namespace NdiMerger.Core;

public static class PixelMapLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PixelMapDefinition LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var map = JsonSerializer.Deserialize<PixelMapDefinition>(json, Options)
                  ?? throw new InvalidDataException($"Failed to parse pixelmap: {path}");

        if (map.CanvasWidth <= 0 || map.CanvasHeight <= 0)
            throw new InvalidDataException("Pixelmap canvas size is invalid.");

        WallFloorMapping.ApplyCanonicalRotations(map.Zones);
        return map;
    }

    public static ZoneDefinition? FindZone(PixelMapDefinition map, string? zoneId) =>
        string.IsNullOrEmpty(zoneId) ? null : map.Zones.FirstOrDefault(z => z.Id == zoneId);
}
