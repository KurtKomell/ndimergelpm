using System.Diagnostics.CodeAnalysis;

namespace NdiMerger.Sources;

/// <summary>
/// Encodes browser source identity as: {id}|{width}|{height}|{url}
/// URL is last so it may contain '|' characters.
/// </summary>
public sealed class BrowserSourceSpec
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    public string Key => $"{Id}|{Width}|{Height}|{Url}";

    public string DisplayName
    {
        get
        {
            try
            {
                var host = new Uri(Url).Host;
                if (!string.IsNullOrWhiteSpace(host))
                    return $"Browser: {host}";
            }
            catch
            {
                // fall through
            }

            var trimmed = Url.Length > 40 ? Url[..37] + "…" : Url;
            return $"Browser: {trimmed}";
        }
    }

    public static BrowserSourceSpec Create(string url, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is required.", nameof(url));

        var normalized = url.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "https://" + normalized;

        return new BrowserSourceSpec
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Url = normalized,
            Width = Math.Clamp(width, 1, 7680),
            Height = Math.Clamp(height, 1, 4320)
        };
    }

    public static bool TryParse(string key, [NotNullWhen(true)] out BrowserSourceSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        // id | width | height | url  — split first 3 pipes only
        var parts = key.Split('|', 4);
        if (parts.Length < 4)
            return false;

        if (!int.TryParse(parts[1], out var w) || w <= 0)
            return false;
        if (!int.TryParse(parts[2], out var h) || h <= 0)
            return false;
        if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[3]))
            return false;

        spec = new BrowserSourceSpec
        {
            Id = parts[0],
            Width = Math.Clamp(w, 1, 7680),
            Height = Math.Clamp(h, 1, 4320),
            Url = parts[3]
        };
        return true;
    }

    public static BrowserSourceSpec Parse(string key)
    {
        if (!TryParse(key, out var spec))
            throw new FormatException($"Invalid browser source key: {key}");
        return spec;
    }
}
