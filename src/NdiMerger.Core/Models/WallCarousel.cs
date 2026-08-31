namespace NdiMerger.Core.Models;

public enum WallCarouselDirection
{
    WestToOst = 0,
    WestToSud = 1
}

public enum WallGroup
{
    West,
    Ost,
    Sud
}

public sealed class WallMotionTrack
{
    public required CompositionLayer Layer { get; init; }
    public required WallGroup Group { get; init; }
    public bool IsIncomingCopy { get; init; }
    public float StartX { get; init; }
    public float StartY { get; init; }
    public float StartScale { get; init; }
    public float Rotation { get; init; }
    public float EndX { get; init; }
    public float EndY { get; init; }
    public float EndScale { get; init; }
}

public sealed class WallCommitSpec
{
    public required CompositionLayer Original { get; init; }
    public required ZoneDefinition TargetZone { get; init; }
    public float EndX { get; init; }
    public float EndY { get; init; }
    public float EndScale { get; init; }
    public float EndRotation { get; init; }
    public ScaleMode EndScaleMode { get; init; }
    public float SavedOpacity { get; init; }
}

public sealed class WallMovingCopy
{
    public required CompositionLayer Source { get; init; }
    public required CompositionLayer Copy { get; init; }
    /// <summary>Layer in the list that this copy is inserted directly above.</summary>
    public required CompositionLayer InsertAbove { get; init; }
}

public sealed class WallSimultaneousCycle
{
    public required IReadOnlyList<WallMotionTrack> Tracks { get; init; }
    public required IReadOnlyList<WallCommitSpec> Commits { get; init; }
    public required IReadOnlyList<WallMovingCopy> MovingCopies { get; init; }
}

/// <summary>
/// Synchronized conveyor for West (3 panels), Ost, and Sud walls.
/// </summary>
public static class WallCarousel
{
    public const string WestPersistZoneId = "wall_west_1";
    public const string MovingLayerMarker = "\u200B⟶";

    private static readonly string[] WestZoneIds =
    [
        "wall_west_1",
        "wall_west_2",
        "wall_west_3"
    ];

    public static IReadOnlyList<CarouselDirectionChoice> DirectionChoices { get; } =
    [
        new(WallCarouselDirection.WestToOst, "West → Ost → Süd"),
        new(WallCarouselDirection.WestToSud, "West → Süd → Ost")
    ];

    public static bool IsMovingCopy(CompositionLayer layer) =>
        layer.Name.Contains(MovingLayerMarker, StringComparison.Ordinal);

    public static WallGroup? ResolveGroup(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return null;

        if (WestZoneIds.Contains(zoneId, StringComparer.OrdinalIgnoreCase))
            return WallGroup.West;
        if (zoneId.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
            return WallGroup.Ost;
        if (zoneId.Equals("wall_sud", StringComparison.OrdinalIgnoreCase))
            return WallGroup.Sud;
        return null;
    }

    public static WallGroup NextGroup(WallGroup current, WallCarouselDirection direction = WallCarouselDirection.WestToOst)
    {
        return direction switch
        {
            WallCarouselDirection.WestToOst => current switch
            {
                WallGroup.West => WallGroup.Ost,
                WallGroup.Ost => WallGroup.Sud,
                _ => WallGroup.West
            },
            _ => current switch
            {
                WallGroup.West => WallGroup.Sud,
                WallGroup.Sud => WallGroup.Ost,
                _ => WallGroup.West
            }
        };
    }

    public static ZoneDefinition BuildTargetZone(WallGroup group, IReadOnlyList<ZoneDefinition> zones)
    {
        return group switch
        {
            WallGroup.West => BuildCombinedWest(zones),
            WallGroup.Ost => CloneZone(RequireZone(zones, "wall_est")),
            WallGroup.Sud => CloneZone(RequireZone(zones, "wall_sud")),
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
    }

    public static ZoneDefinition BuildCombinedWest(IReadOnlyList<ZoneDefinition> zones)
    {
        var panels = WestZoneIds
            .Select(id => RequireZone(zones, id))
            .ToList();

        float minX = panels.Min(z => z.X);
        float minY = panels.Min(z => z.Y);
        float maxX = panels.Max(z => z.X + z.Width);
        float maxY = panels.Max(z => z.Y + z.Height);

        return new ZoneDefinition
        {
            Id = WestPersistZoneId,
            Name = "Wall West",
            X = minX,
            Y = minY,
            Width = maxX - minX,
            Height = maxY - minY,
            ContentWidth = maxX - minX,
            ContentHeight = maxY - minY,
            RotationDegrees = 0
        };
    }

    public static float ZoneRotation(WallGroup group) => group switch
    {
        WallGroup.West => 0f,
        WallGroup.Ost => 180f,
        WallGroup.Sud => 90f,
        _ => 0f
    };

    public static void ComputeWallPose(CompositionLayer layer, ZoneDefinition zone,
        out float x, out float y, out float scale, out float rotation)
    {
        rotation = zone.RotationDegrees;
        float targetW = zone.ContentWidth > 0 ? zone.ContentWidth : zone.Width;
        float targetH = zone.ContentHeight > 0 ? zone.ContentHeight : zone.Height;
        float zcx = zone.X + zone.Width * 0.5f;
        float zcy = zone.Y + zone.Height * 0.5f;

        if (layer.NativeWidth <= 0 || layer.NativeHeight <= 0)
        {
            scale = 1f;
            x = zcx - targetW * 0.5f;
            y = zcy - targetH * 0.5f;
            return;
        }

        // Always fit zone height (centered horizontally).
        scale = targetH / layer.NativeHeight;
        float dw = layer.NativeWidth * scale;
        float dh = layer.NativeHeight * scale;
        x = zcx - dw * 0.5f;
        y = zcy - dh * 0.5f;
    }

    public static void ComputePose(CompositionLayer layer, ZoneDefinition zone, ScaleMode mode,
        out float x, out float y, out float scale, out float rotation)
    {
        rotation = zone.RotationDegrees;
        float targetW = zone.ContentWidth > 0 ? zone.ContentWidth : zone.Width;
        float targetH = zone.ContentHeight > 0 ? zone.ContentHeight : zone.Height;
        float zcx = zone.X + zone.Width * 0.5f;
        float zcy = zone.Y + zone.Height * 0.5f;

        if (layer.NativeWidth <= 0 || layer.NativeHeight <= 0)
        {
            scale = 1f;
            x = zcx - targetW * 0.5f;
            y = zcy - targetH * 0.5f;
            return;
        }

        float srcW = layer.NativeWidth;
        float srcH = layer.NativeHeight;
        scale = mode switch
        {
            ScaleMode.FitZone => MathF.Min(targetW / srcW, targetH / srcH),
            ScaleMode.FillZone => MathF.Max(targetW / srcW, targetH / srcH),
            _ => 1f
        };

        float dw = srcW * scale;
        float dh = srcH * scale;
        x = zcx - dw * 0.5f;
        y = zcy - dh * 0.5f;
    }

    public static CompositionLayer? FindLayerForGroup(
        WallGroup group,
        IReadOnlyList<ZoneDefinition> zones,
        IReadOnlyList<CompositionLayer> layers)
    {
        var zone = BuildTargetZone(group, zones);
        CompositionLayer? best = null;
        float bestArea = 0f;
        foreach (var layer in layers.Where(l => !IsMovingCopy(l) && l.Visible))
        {
            if (!IsLayerCenterInZone(layer, zone))
                continue;

            var (_, _, bw, bh) = layer.GetMapBounds();
            float area = bw * bh;
            if (area > bestArea)
            {
                bestArea = area;
                best = layer;
            }
        }

        return best;
    }

    public static string? DescribeMissingWallLayers(
        IReadOnlyList<ZoneDefinition> zones,
        IReadOnlyList<CompositionLayer> layers)
    {
        var missing = new List<string>();
        if (FindLayerForGroup(WallGroup.West, zones, layers) is null)
            missing.Add("West");
        if (FindLayerForGroup(WallGroup.Ost, zones, layers) is null)
            missing.Add("Ost");
        if (FindLayerForGroup(WallGroup.Sud, zones, layers) is null)
            missing.Add("Süd");
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private static bool IsLayerCenterInZone(CompositionLayer layer, ZoneDefinition zone)
    {
        var (bx, by, bw, bh) = layer.GetMapBounds();
        if (bw <= 0 || bh <= 0)
            return false;

        float cx = bx + bw * 0.5f;
        float cy = by + bh * 0.5f;
        return cx >= zone.X && cx <= zone.X + zone.Width &&
               cy >= zone.Y && cy <= zone.Y + zone.Height;
    }

    public static CompositionLayer CreateMovingCopy(CompositionLayer source)
    {
        return new CompositionLayer
        {
            Name = source.Name + " " + MovingLayerMarker,
            SourceKind = source.SourceKind,
            SourceKey = source.SourceKey,
            X = source.X,
            Y = source.Y,
            Scale = source.Scale,
            RotationDegrees = source.RotationDegrees,
            Opacity = source.Opacity,
            ScaleMode = source.ScaleMode,
            ZoneId = null,
            Visible = true,
            ZIndex = source.ZIndex + 1,
            NativeWidth = source.NativeWidth,
            NativeHeight = source.NativeHeight
        };
    }

    /// <summary>
    /// 6 synchronized tracks: West/Ost/Süd each outgoing + incoming copy.
    /// Layer permutation: West→Ost, Ost→Süd, Süd→West.
    /// </summary>
    public static WallSimultaneousCycle? TryBuildSimultaneousCycle(
        IReadOnlyList<ZoneDefinition> zones,
        IReadOnlyList<CompositionLayer> layers,
        WallCarouselDirection direction = WallCarouselDirection.WestToOst)
    {
        if (DescribeMissingWallLayers(zones, layers) is not null)
            return null;

        var westLayer = FindLayerForGroup(WallGroup.West, zones, layers)!;
        var ostLayer = FindLayerForGroup(WallGroup.Ost, zones, layers)!;
        var sudLayer = FindLayerForGroup(WallGroup.Sud, zones, layers)!;

        var westZone = BuildTargetZone(WallGroup.West, zones);
        var ostZone = BuildTargetZone(WallGroup.Ost, zones);
        var sudZone = BuildTargetZone(WallGroup.Sud, zones);

        float slideWest = westZone.Width * 1.05f;
        float slideOst = ostZone.Width * 1.05f;
        float slideSud = sudZone.Height * 1.05f;

        var tracks = new List<WallMotionTrack>();
        var commits = new List<WallCommitSpec>();
        var movingCopies = new List<WallMovingCopy>();

        if (direction == WallCarouselDirection.WestToOst)
        {
            tracks.Add(OutgoingCopy(WallGroup.West, westLayer, movingCopies, westLayer.X + slideWest, westLayer.Y));
            tracks.Add(OutgoingCopy(WallGroup.Ost, ostLayer, movingCopies, ostLayer.X - slideOst, ostLayer.Y));
            tracks.Add(OutgoingCopy(WallGroup.Sud, sudLayer, movingCopies, sudLayer.X, sudLayer.Y - slideSud));

            tracks.Add(BuildIncoming(WallGroup.West, sudLayer, westLayer, westZone, movingCopies, slideWest, fromLeft: true));
            tracks.Add(BuildIncoming(WallGroup.Ost, westLayer, ostLayer, ostZone, movingCopies, slideOst, fromRight: true));
            tracks.Add(BuildIncoming(WallGroup.Sud, ostLayer, sudLayer, sudZone, movingCopies, slideSud, fromBottom: true));

            AddCommit(commits, westLayer, ostZone);
            AddCommit(commits, ostLayer, sudZone);
            AddCommit(commits, sudLayer, westZone);
        }
        else
        {
            tracks.Add(OutgoingCopy(WallGroup.West, westLayer, movingCopies, westLayer.X - slideWest, westLayer.Y));
            tracks.Add(OutgoingCopy(WallGroup.Ost, ostLayer, movingCopies, ostLayer.X + slideOst, ostLayer.Y));
            tracks.Add(OutgoingCopy(WallGroup.Sud, sudLayer, movingCopies, sudLayer.X, sudLayer.Y + slideSud));

            tracks.Add(BuildIncoming(WallGroup.West, ostLayer, westLayer, westZone, movingCopies, slideWest, fromRight: true));
            tracks.Add(BuildIncoming(WallGroup.Sud, westLayer, sudLayer, sudZone, movingCopies, slideSud, fromTop: true));
            tracks.Add(BuildIncoming(WallGroup.Ost, sudLayer, ostLayer, ostZone, movingCopies, slideOst, fromLeft: true));

            AddCommit(commits, westLayer, sudZone);
            AddCommit(commits, sudLayer, ostZone);
            AddCommit(commits, ostLayer, westZone);
        }

        return new WallSimultaneousCycle
        {
            Tracks = tracks,
            Commits = commits,
            MovingCopies = movingCopies
        };
    }

    private static WallMotionTrack OutgoingCopy(
        WallGroup group,
        CompositionLayer source,
        List<WallMovingCopy> movingCopies,
        float endX,
        float endY)
    {
        var copy = CreateMovingCopy(source);
        copy.Opacity = source.Opacity <= 0 ? 1f : source.Opacity;
        movingCopies.Add(new WallMovingCopy
        {
            Source = source,
            Copy = copy,
            InsertAbove = source
        });
        return Outgoing(group, copy, endX, endY);
    }

    private static WallMotionTrack Outgoing(WallGroup group, CompositionLayer layer, float endX, float endY) => new()
    {
        Group = group,
        Layer = layer,
        IsIncomingCopy = false,
        StartX = layer.X,
        StartY = layer.Y,
        StartScale = layer.Scale,
        Rotation = layer.RotationDegrees,
        EndX = endX,
        EndY = endY,
        EndScale = layer.Scale
    };

    private static WallMotionTrack BuildIncoming(
        WallGroup group,
        CompositionLayer source,
        CompositionLayer destinationLayer,
        ZoneDefinition targetZone,
        List<WallMovingCopy> movingCopies,
        float slideDist,
        bool fromLeft = false,
        bool fromRight = false,
        bool fromBottom = false,
        bool fromTop = false)
    {
        ComputeWallPose(source, targetZone, out float endX, out float endY, out float endScale, out float endRot);

        float startX = endX;
        float startY = endY;
        if (fromLeft)
        {
            startX = endX - slideDist;
            startY = endY;
        }
        else if (fromRight)
        {
            startX = endX + slideDist;
            startY = endY;
        }
        else if (fromBottom)
        {
            startX = endX;
            startY = endY + slideDist;
        }
        else if (fromTop)
        {
            startX = endX;
            startY = endY - slideDist;
        }

        var copy = CreateMovingCopy(source);
        copy.RotationDegrees = endRot;
        copy.Scale = endScale;
        copy.Opacity = source.Opacity <= 0 ? 1f : source.Opacity;
        // Stack on destination wall so source→other-wall motion doesn't cover the exit.
        movingCopies.Add(new WallMovingCopy
        {
            Source = source,
            Copy = copy,
            InsertAbove = destinationLayer
        });

        copy.X = startX;
        copy.Y = startY;

        return new WallMotionTrack
        {
            Group = group,
            Layer = copy,
            IsIncomingCopy = true,
            StartX = startX,
            StartY = startY,
            StartScale = endScale,
            Rotation = endRot,
            EndX = endX,
            EndY = endY,
            EndScale = endScale
        };
    }

    private static void AddCommit(List<WallCommitSpec> commits, CompositionLayer original, ZoneDefinition targetZone)
    {
        ComputeWallPose(original, targetZone, out float endX, out float endY, out float endScale, out float endRot);

        commits.Add(new WallCommitSpec
        {
            Original = original,
            TargetZone = targetZone,
            EndX = endX,
            EndY = endY,
            EndScale = endScale,
            EndRotation = endRot,
            EndScaleMode = ScaleMode.FitZone,
            SavedOpacity = original.Opacity <= 0 ? 1f : original.Opacity
        });
    }

    private static ZoneDefinition RequireZone(IReadOnlyList<ZoneDefinition> zones, string id)
    {
        var z = zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (z is null)
            throw new InvalidOperationException($"Zone '{id}' not found in pixelmap.");
        return z;
    }

    private static ZoneDefinition CloneZone(ZoneDefinition z) => new()
    {
        Id = z.Id,
        Name = z.Name,
        X = z.X,
        Y = z.Y,
        Width = z.Width,
        Height = z.Height,
        ContentWidth = z.ContentWidth,
        ContentHeight = z.ContentHeight,
        RotationDegrees = z.RotationDegrees,
        SingleImage = z.SingleImage
    };
}

public sealed record CarouselDirectionChoice(WallCarouselDirection Value, string Label);
