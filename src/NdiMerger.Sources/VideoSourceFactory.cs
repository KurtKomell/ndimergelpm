using NdiMerger.Core.Gpu;
using NdiMerger.Core.Models;

namespace NdiMerger.Sources;

public static class VideoSourceFactory
{
    public static IVideoSource Create(DiscoveredSource discovered)
    {
        return discovered.Kind switch
        {
            SourceKind.Ndi => new NdiVideoSource(discovered.Key),
            SourceKind.Spout => new SpoutVideoSource(discovered.Key),
            SourceKind.Capture => new CaptureVideoSource(discovered.Key, discovered.DisplayName),
            SourceKind.Solid => CreateSolid(discovered.Key, discovered.DisplayName),
            SourceKind.Browser => new BrowserVideoSource(discovered.Key, discovered.DisplayName),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public static IVideoSource Create(SourceKind kind, string key, string? displayName = null)
    {
        return kind switch
        {
            SourceKind.Ndi => new NdiVideoSource(key),
            SourceKind.Spout => new SpoutVideoSource(key),
            SourceKind.Capture => new CaptureVideoSource(key, displayName ?? key),
            SourceKind.Solid => CreateSolid(key, displayName),
            SourceKind.Browser => new BrowserVideoSource(key, displayName),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static IVideoSource CreateSolid(string key, string? displayName)
    {
        if (key.Equals(SolidColorVideoSource.BlackKey, StringComparison.OrdinalIgnoreCase))
            return SolidColorVideoSource.CreateBlack();

        return new SolidColorVideoSource(key, displayName ?? key, 0, 0, 0);
    }

    public static List<DiscoveredSource> DiscoverAll(NdiSourceCatalog? ndi, SpoutSourceCatalog? spout)
    {
        var list = new List<DiscoveredSource>();
        try
        {
            if (ndi is not null) list.AddRange(ndi.List());
        }
        catch { /* NDI unavailable */ }

        try
        {
            if (spout is not null) list.AddRange(spout.List());
        }
        catch { /* Spout unavailable */ }

        try
        {
            list.AddRange(CaptureSourceCatalog.List());
        }
        catch { /* capture unavailable */ }

        list.AddRange(SolidColorVideoSource.List());
        return list;
    }
}

public sealed class SourceRuntimeHub : IDisposable
{
    private readonly Dictionary<string, IVideoSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private GpuDevice? _gpu;

    public void AttachGpu(GpuDevice gpu) => _gpu = gpu;

    public IVideoSource GetOrCreate(SourceKind kind, string key, string? displayName = null)
    {
        lock (_lock)
        {
            var id = $"{kind}:{key}";
            if (_sources.TryGetValue(id, out var existing))
                return existing;

            if (_gpu is null)
                throw new InvalidOperationException("GPU not attached.");

            var source = VideoSourceFactory.Create(kind, key, displayName);
            try
            {
                source.Start(_gpu);
            }
            catch
            {
                source.Dispose();
                throw;
            }
            _sources[id] = source;
            return source;
        }
    }

    public IVideoSource? TryGet(SourceKind kind, string key)
    {
        lock (_lock)
        {
            _sources.TryGetValue($"{kind}:{key}", out var s);
            return s;
        }
    }

    public void UpdateAll()
    {
        lock (_lock)
        {
            foreach (var s in _sources.Values)
                s.Update();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sources.Values)
                s.Dispose();
            _sources.Clear();
        }
    }
}
