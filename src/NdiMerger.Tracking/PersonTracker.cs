using System.Numerics;
using NdiMerger.Core.Models;

namespace NdiMerger.Tracking;

public sealed class PersonTracker
{
    private const int BackgroundWarmupScans = 5;

    private readonly BackgroundModel _background = new();
    private readonly List<TrackState> _tracks = [];
    private int _nextTrackId = 1;
    private DateTime _lastScanTimestamp = DateTime.MinValue;
    private int _warmupScansRemaining = BackgroundWarmupScans;

    public void ResetBackground()
    {
        _background.Reset();
        _warmupScansRemaining = BackgroundWarmupScans;
        _lastScanTimestamp = DateTime.MinValue;
    }

    public FloorBlobFrame Update(LidarScan? scan, LidarSettings settings, ZoneDefinition floor, DateTime nowUtc)
    {
        if (scan is null || !settings.Enabled)
            return FloorBlobFrame.Empty;

        if (scan.Timestamp != _lastScanTimestamp)
        {
            _lastScanTimestamp = scan.Timestamp;
            if (_warmupScansRemaining > 0)
                _warmupScansRemaining--;
        }

        bool inWarmup = _warmupScansRemaining > 0;
        var debugPoints = settings.ShowDebugPoints ? new List<Vector2>() : [];
        var candidates = new List<(Vector2 pos, float spread)>();

        _background.BeginFrame();
        if (!inWarmup)
        {
            foreach (var track in _tracks)
            {
                if (!track.Confirmed)
                    continue;
                _background.ProtectAngle(
                    CanvasToSensorAngle(track.Position, settings),
                    halfWidthBins: 14);
            }
        }

        foreach (var pt in scan.Points)
        {
            if (pt.Quality < settings.MinQuality)
                continue;
            if (pt.DistanceMm <= 0 ||
                pt.DistanceMm < settings.MinDistanceMm ||
                pt.DistanceMm > settings.MaxDistanceMm)
                continue;

            var canvas = settings.ToCanvas(pt.AngleDegrees, pt.DistanceMm);
            if (!IsInsideFloor(canvas, floor, marginPx: 8f))
                continue;

            // The overlay shows the whole scan so the room outline stays visible for calibration.
            if (settings.ShowDebugPoints)
                debugPoints.Add(canvas);

            if (inWarmup)
            {
                _background.Learn(pt.AngleDegrees, pt.DistanceMm);
                continue;
            }

            if (!_background.IsForeground(pt.AngleDegrees, pt.DistanceMm, settings.ForegroundMarginMm))
            {
                _background.Learn(pt.AngleDegrees, pt.DistanceMm);
                continue;
            }

            candidates.Add((canvas, pt.DistanceMm));
        }

        var clusters = inWarmup ? [] : ClusterCandidates(candidates, settings);
        MatchTracks(clusters, settings, nowUtc);

        var blobs = new List<FloorBlob>();
        var trail = new List<FloorBlob>();
        float baseRadius = settings.BlobRadiusPx;
        BuildProximityBlobs(settings, nowUtc, baseRadius, blobs, trail);

        return new FloorBlobFrame
        {
            Blobs = blobs,
            Trail = trail,
            DebugPoints = debugPoints,
            DebugSensor = settings.ShowDebugPoints
                ? new Vector2(settings.SensorXPx, settings.SensorYPx)
                : null
        };
    }

    /// <summary>
    /// Two (or more) nearby confirmed people fuse into one larger red pulsing circle;
    /// everyone else keeps the normal blue location marker.
    /// </summary>
    private void BuildProximityBlobs(
        LidarSettings settings,
        DateTime nowUtc,
        float baseRadius,
        List<FloorBlob> blobs,
        List<FloorBlob> trail)
    {
        var visible = _tracks.Where(t => t.Visible).ToList();
        if (visible.Count == 0)
            return;

        float mergePx = settings.MergeDistanceMeters * MathF.Max(settings.PixelsPerMeter, 1f);
        int n = visible.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[b] = a;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Vector2.Distance(visible[i].Position, visible[j].Position) <= mergePx)
                    Union(i, j);
            }
        }

        var groups = new Dictionary<int, List<TrackState>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = [];
                groups[root] = list;
            }

            list.Add(visible[i]);
        }

        var mergeColor = new Vector4(1f, 0.18f, 0.22f, 1f);

        foreach (var group in groups.Values)
        {
            if (group.Count >= 2)
            {
                Vector2 center = default;
                float alpha = 0;
                foreach (var t in group)
                {
                    center += t.Position;
                    alpha = MathF.Max(alpha, t.Alpha);
                }

                center /= group.Count;

                float span = 0;
                foreach (var t in group)
                    span = MathF.Max(span, Vector2.Distance(center, t.Position));

                float radius = MathF.Max(baseRadius * 1.85f, span + baseRadius * 0.75f);
                blobs.Add(new FloorBlob
                {
                    Center = center,
                    RadiusPx = radius,
                    Alpha = alpha,
                    Color = mergeColor
                });
                continue;
            }

            var track = group[0];
            blobs.Add(new FloorBlob
            {
                Center = track.Position,
                RadiusPx = baseRadius,
                Alpha = track.Alpha
            });
            AppendTrail(track, trail, settings, nowUtc, baseRadius);
        }
    }

    private static bool IsInsideFloor(Vector2 p, ZoneDefinition floor, float marginPx)
    {
        return p.X >= floor.X - marginPx &&
               p.X <= floor.X + floor.Width + marginPx &&
               p.Y >= floor.Y - marginPx &&
               p.Y <= floor.Y + floor.Height + marginPx;
    }

    private static List<(Vector2 centroid, float spread)> ClusterCandidates(
        List<(Vector2 pos, float spread)> points,
        LidarSettings settings)
    {
        var clusters = new List<(Vector2 centroid, float spread)>();
        if (points.Count == 0)
            return clusters;

        // Keep scan angle order — sorting by canvas X breaks LiDAR adjacency clustering.
        var current = new List<Vector2> { points[0].pos };
        float currentSpread = points[0].spread;

        for (int i = 1; i < points.Count; i++)
        {
            float gap = Vector2.Distance(points[i].pos, points[i - 1].pos);
            float gapMm = gap / MathF.Max(settings.PixelsPerMeter, 1f) * 1000f;
            if (gapMm > settings.ClusterGapMm)
            {
                TryAddCluster(current, currentSpread, settings, clusters);
                current = new List<Vector2>();
                currentSpread = points[i].spread;
            }

            current.Add(points[i].pos);
            currentSpread = MathF.Max(currentSpread, points[i].spread);
        }

        TryAddCluster(current, currentSpread, settings, clusters);
        return clusters;
    }

    private static void TryAddCluster(
        List<Vector2> points,
        float spread,
        LidarSettings settings,
        List<(Vector2 centroid, float spread)> output)
    {
        if (points.Count < settings.MinClusterPoints)
            return;

        float widthMm = (MaxSpan(points) / MathF.Max(settings.PixelsPerMeter, 1f)) * 1000f;
        if (widthMm < settings.MinPersonWidthMm || widthMm > settings.MaxPersonWidthMm)
            return;

        Vector2 centroid = default;
        foreach (var p in points)
            centroid += p;
        centroid /= points.Count;

        output.Add((centroid, spread));
    }

    private static float MaxSpan(List<Vector2> points)
    {
        float max = 0;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
                max = MathF.Max(max, Vector2.Distance(points[i], points[j]));
        }

        return max;
    }

    private void MatchTracks(
        List<(Vector2 centroid, float spread)> clusters,
        LidarSettings settings,
        DateTime nowUtc)
    {
        var used = new bool[clusters.Count];
        foreach (var track in _tracks)
            track.Matched = false;

        for (int c = 0; c < clusters.Count; c++)
        {
            var cluster = clusters[c];
            TrackState? best = null;
            float bestDist = float.MaxValue;

            foreach (var track in _tracks)
            {
                float d = Vector2.Distance(track.Position, cluster.centroid);
                if (d < bestDist && d <= settings.MaxJumpPx)
                {
                    bestDist = d;
                    best = track;
                }
            }

            if (best is not null)
            {
                best.Matched = true;
                best.Hits++;
                best.Missed = 0;
                best.LastSeen = nowUtc;
                best.Position = Vector2.Lerp(best.Position, cluster.centroid, settings.SmoothingAlpha);
                best.Velocity = Vector2.Lerp(best.Velocity, cluster.centroid - best.Position, 0.5f);
                best.MaxDisplacementPx = MathF.Max(
                    best.MaxDisplacementPx,
                    Vector2.Distance(best.SpawnPosition, best.Position));
                best.History.Add(new TrailSample(cluster.centroid, nowUtc));
                TrimHistory(best, settings, nowUtc);
                used[c] = true;
            }
        }

        for (int c = 0; c < clusters.Count; c++)
        {
            if (used[c])
                continue;

            var track = new TrackState
            {
                Id = _nextTrackId++,
                Position = clusters[c].centroid,
                SpawnPosition = clusters[c].centroid,
                LastSeen = nowUtc,
                Hits = 1
            };
            track.History.Add(new TrailSample(clusters[c].centroid, nowUtc));
            _tracks.Add(track);
        }

        float minMovePx = settings.MinMovementMm / 1000f * MathF.Max(settings.PixelsPerMeter, 1f);

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i];
            if (!track.Matched)
            {
                track.Missed++;
                if (!track.Confirmed)
                    track.Hits = Math.Max(0, track.Hits - 1);
            }

            // A track has to walk before it counts as a person, and stays one once it did.
            if (!track.Confirmed && track.Hits >= settings.MinHits && track.MaxDisplacementPx >= minMovePx)
                track.Confirmed = true;

            track.Visible = track.Confirmed && track.Alpha > 0.01f;

            // Confirmed people linger much longer while standing still / briefly occluded.
            int holdMs = track.Confirmed ? Math.Max(settings.HoldMs, 4000) : settings.HoldMs;
            double ageMs = (nowUtc - track.LastSeen).TotalMilliseconds;
            if (track.Missed > 0 && ageMs > holdMs)
            {
                track.Alpha = MathF.Max(0, track.Alpha - 0.04f);
                if (track.Alpha <= 0.01f)
                    _tracks.RemoveAt(i);
            }
            else if (track.Confirmed)
            {
                track.Alpha = MathF.Min(1f, track.Alpha + 0.2f);
            }
        }
    }

    private static float CanvasToSensorAngle(Vector2 canvas, LidarSettings settings)
    {
        float dx = (canvas.X - settings.SensorXPx) * (settings.MirrorX ? -1f : 1f);
        float dy = canvas.Y - settings.SensorYPx;
        // Inverse of ToCanvas: x = sin(a)*r, y = -cos(a)*r
        float angle = MathF.Atan2(dx, -dy) * (180f / MathF.PI) - settings.RotationDegrees;
        angle %= 360f;
        if (angle < 0) angle += 360f;
        return angle;
    }

    private static void AppendTrail(
        TrackState track,
        List<FloorBlob> trail,
        LidarSettings settings,
        DateTime nowUtc,
        float baseRadius)
    {
        if (settings.TrailLengthSeconds <= 0 || settings.TrailStrength <= 0)
            return;

        foreach (var sample in track.History)
        {
            float age = (float)(nowUtc - sample.Time).TotalSeconds;
            if (age <= 0 || age > settings.TrailLengthSeconds)
                continue;

            float t = 1f - age / settings.TrailLengthSeconds;
            trail.Add(new FloorBlob
            {
                Center = sample.Position,
                RadiusPx = baseRadius * (0.35f + 0.25f * t),
                Alpha = settings.TrailStrength * t * track.Alpha
            });
        }
    }

    private static void TrimHistory(TrackState track, LidarSettings settings, DateTime nowUtc)
    {
        while (track.History.Count > 0 &&
               (nowUtc - track.History[0].Time).TotalSeconds > settings.TrailLengthSeconds + 0.25)
        {
            track.History.RemoveAt(0);
        }

        const int maxSamples = 64;
        while (track.History.Count > maxSamples)
            track.History.RemoveAt(0);
    }

    private sealed class TrackState
    {
        public int Id { get; init; }
        public Vector2 Position { get; set; }
        public Vector2 SpawnPosition { get; init; }
        public Vector2 Velocity { get; set; }
        public float MaxDisplacementPx { get; set; }
        public DateTime LastSeen { get; set; }
        public int Hits { get; set; }
        public int Missed { get; set; }
        public bool Matched { get; set; }
        public bool Confirmed { get; set; }
        public bool Visible { get; set; }
        public float Alpha { get; set; }
        public List<TrailSample> History { get; } = [];
    }

    private readonly record struct TrailSample(Vector2 Position, DateTime Time);
}
