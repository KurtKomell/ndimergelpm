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
/// One unwrapped LED slice for the West ribbon (West 1–3).
/// </summary>
public readonly struct WallRibbonDraw
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float RotationDegrees { get; init; }
    public float U0 { get; init; }
    public float V0 { get; init; }
    public float U1 { get; init; }
    public float V1 { get; init; }
}

/// <summary>
/// Synchronized conveyor for West (3 panels), Ost, and Sud walls.
/// Stage is not part of the carousel.
/// </summary>
public static class WallCarousel
{
    public const string WestPersistZoneId = "wall_west_1";
    public const string MovingLayerMarker = "\u200B⟶";

    private readonly struct WestRibbonPanel
    {
        public float RibbonX { get; init; }
        public float RibbonWidth { get; init; }
    }

    private static readonly string[] WestZoneIds =
    [
        "wall_west_1",
        "wall_west_2",
        "wall_west_3"
    ];
    public static IReadOnlyList<CarouselDirectionChoice> DirectionChoices { get; } =
    [
        new(WallCarouselDirection.WestToOst, "West → East → South"),
        new(WallCarouselDirection.WestToSud, "West → South → East")
    ];

    public static bool IsMovingCopy(CompositionLayer layer) =>
        layer.Name.Contains(MovingLayerMarker, StringComparison.Ordinal);

    public static bool IsCarouselSource(CompositionLayer layer) =>
        !IsMovingCopy(layer) &&
        layer.SourceKind != SourceKind.Browser &&
        layer.SourceKind != SourceKind.Solid;

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
        if (!TryGetWestRibbon(zones, out var panels, out float ribbonY, out float ribbonH))
            throw new InvalidOperationException("West wall zones not found in pixelmap.");

        float x0 = panels[0].RibbonX;
        float x1 = panels[^1].RibbonX + panels[^1].RibbonWidth;
        return new ZoneDefinition
        {
            Id = WestPersistZoneId,
            Name = "Wall West",
            X = x0,
            Y = ribbonY,
            Width = x1 - x0,
            Height = ribbonH,
            ContentWidth = x1 - x0,
            ContentHeight = ribbonH,
            RotationDegrees = 0
        };
    }

    public static float ZoneRotation(WallGroup group) => group switch
    {
        WallGroup.West => 0f,
        WallGroup.Ost => 180f,
        WallGroup.Sud => 270f,
        _ => 0f
    };

    public static bool TryBuildRibbonDraws(
        CompositionLayer layer,
        IReadOnlyList<ZoneDefinition> zones,
        List<WallRibbonDraw> dest)
    {
        dest.Clear();
        if (!IsWestRibbonCandidate(layer, zones))
            return false;
        if (!TryGetWestRibbon(zones, out var panels, out float ribbonY, out float ribbonH))
            return false;

        var size = layer.GetDrawSize();
        if (size.X < 2f || size.Y < 2f)
            return false;

        float lx0 = layer.X;
        float ly0 = layer.Y;
        float lx1 = layer.X + size.X;
        float ly1 = layer.Y + size.Y;

        foreach (var panel in panels)
        {
            float ox0 = MathF.Max(lx0, panel.RibbonX);
            float ox1 = MathF.Min(lx1, panel.RibbonX + panel.RibbonWidth);
            float oy0 = MathF.Max(ly0, ribbonY);
            float oy1 = MathF.Min(ly1, ribbonY + ribbonH);
            if (ox1 - ox0 < 1f || oy1 - oy0 < 1f)
                continue;

            float u0 = (ox0 - lx0) / size.X;
            float u1 = (ox1 - lx0) / size.X;
            float v0 = (oy0 - ly0) / size.Y;
            float v1 = (oy1 - ly0) / size.Y;
            var uv0 = layer.MapCropUv(u0, v0);
            var uv1 = layer.MapCropUv(u1, v1);

            dest.Add(new WallRibbonDraw
            {
                X = ox0,
                Y = oy0,
                Width = ox1 - ox0,
                Height = oy1 - oy0,
                RotationDegrees = 0f,
                U0 = uv0.X,
                V0 = uv0.Y,
                U1 = uv1.X,
                V1 = uv1.Y
            });
        }

        return dest.Count > 0;
    }

    public static IReadOnlyList<(float X, float Y, float W, float H)> GetRibbonOverlayRects(
        CompositionLayer layer,
        IReadOnlyList<ZoneDefinition> zones)
    {
        var draws = new List<WallRibbonDraw>();
        if (!TryBuildRibbonDraws(layer, zones, draws))
            return [];

        var rects = new List<(float X, float Y, float W, float H)>(draws.Count);
        foreach (var draw in draws)
        {
            float rot = ((draw.RotationDegrees % 360f) + 360f) % 360f;
            bool swap = MathF.Abs(rot - 90f) < 1f || MathF.Abs(rot - 270f) < 1f;
            float bw = swap ? draw.Height : draw.Width;
            float bh = swap ? draw.Width : draw.Height;
            float cx = draw.X + draw.Width * 0.5f;
            float cy = draw.Y + draw.Height * 0.5f;
            rects.Add((cx - bw * 0.5f, cy - bh * 0.5f, bw, bh));
        }

        return rects;
    }

    public static bool LayerContainsMapPoint(
        CompositionLayer layer,
        IReadOnlyList<ZoneDefinition> zones,
        double mapX,
        double mapY)
    {
        var rects = GetRibbonOverlayRects(layer, zones);
        if (rects.Count > 0)
        {
            foreach (var (x, y, w, h) in rects)
            {
                if (mapX >= x && mapX <= x + w && mapY >= y && mapY <= y + h)
                    return true;
            }

            return false;
        }

        var (bx, by, bw, bh) = layer.GetMapBounds();
        return bw > 0 && bh > 0 &&
               mapX >= bx && mapX <= bx + bw &&
               mapY >= by && mapY <= by + bh;
    }

    private static bool IsWestRibbonCandidate(CompositionLayer layer, IReadOnlyList<ZoneDefinition> zones)
    {
        float rot = ((layer.RotationDegrees % 360f) + 360f) % 360f;
        if (rot >= 2f && rot <= 358f)
            return false;

        if (!TryGetWestRibbon(zones, out var panels, out float ribbonY, out float ribbonH))
            return false;

        var size = layer.GetDrawSize();
        if (size.X < 2f || size.Y < 2f)
            return false;

        float ribbonX0 = panels[0].RibbonX;
        float ribbonX1 = panels[^1].RibbonX + panels[^1].RibbonWidth;
        bool overlaps =
            layer.X + size.X > ribbonX0 &&
            layer.X < ribbonX1 &&
            layer.Y + size.Y > ribbonY &&
            layer.Y < ribbonY + ribbonH;
        if (!overlaps)
            return false;

        // Ribbon path only when content actually spans more than one West panel
        // (carousel / full-West snap). Single-panel West 1/2/3 snaps use normal quads
        // so each zone keeps its own canvas overlay.
        int hitPanels = 0;
        foreach (var panel in panels)
        {
            float ox0 = MathF.Max(layer.X, panel.RibbonX);
            float ox1 = MathF.Min(layer.X + size.X, panel.RibbonX + panel.RibbonWidth);
            float oy0 = MathF.Max(layer.Y, ribbonY);
            float oy1 = MathF.Min(layer.Y + size.Y, ribbonY + ribbonH);
            if (ox1 - ox0 >= 1f && oy1 - oy0 >= 1f)
                hitPanels++;
        }

        return hitPanels >= 2;
    }

    private static bool TryGetWestRibbon(
        IReadOnlyList<ZoneDefinition> zones,
        out List<WestRibbonPanel> panels,
        out float ribbonY,
        out float ribbonH)
    {
        panels = [];
        ribbonY = 0f;
        ribbonH = 0f;
        float x = 0f;
        bool started = false;

        foreach (var id in WestZoneIds)
        {
            var zone = zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (zone is null)
                return false;

            float w = zone.ContentWidth > 0 ? zone.ContentWidth : zone.Width;
            if (!started)
            {
                x = zone.X;
                ribbonY = zone.Y;
                ribbonH = zone.Height > 0 ? zone.Height : 1200f;
                started = true;
            }

            panels.Add(new WestRibbonPanel
            {
                RibbonX = x,
                RibbonWidth = w
            });
            x += w;
        }

        return panels.Count > 0;
    }

    private enum SlideFrom { Left, Right, Top, Bottom }

    /// <summary>Hide the 1 px raster/filter hairline where two copies meet.</summary>
    private const float SeamOverlapPx = 2f;

    public static void ComputeWallPose(CompositionLayer layer, ZoneDefinition zone,
        out float x, out float y, out float scale, out float rotation)
    {
        rotation = ResolveGroup(zone.Id) is WallGroup g
            ? ZoneRotation(g)
            : zone.RotationDegrees;
        float targetW = zone.ContentWidth > 0 ? zone.ContentWidth : zone.Width;
        float targetH = zone.ContentHeight > 0 ? zone.ContentHeight : zone.Height;
        float zcx = zone.X + zone.Width * 0.5f;
        float zcy = zone.Y + zone.Height * 0.5f;

        var (srcW, srcH) = layer.GetSourcePixelSize();
        if (srcW <= 0 || srcH <= 0)
        {
            scale = 1f;
            x = zcx - targetW * 0.5f;
            y = zcy - targetH * 0.5f;
            return;
        }

        // Always fit zone height (centered horizontally).
        scale = targetH / srcH;
        float dw = srcW * scale;
        float dh = srcH * scale;
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

        var (srcW, srcH) = layer.GetSourcePixelSize();
        if (srcW <= 0 || srcH <= 0)
        {
            scale = 1f;
            x = zcx - targetW * 0.5f;
            y = zcy - targetH * 0.5f;
            return;
        }
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
        CompositionLayer? best = null;
        float bestArea = -1f;
        foreach (var layer in layers.Where(l => IsCarouselSource(l) && l.IsLogicallyVisible))
        {
            if (ResolveGroup(layer.ZoneId) != group)
                continue;
            float area = LayerArea(layer);
            if (area >= bestArea)
            {
                bestArea = area;
                best = layer;
            }
        }

        if (best is not null)
            return best;

        var zone = BuildTargetZone(group, zones);
        bestArea = -1f;
        foreach (var layer in layers.Where(l => IsCarouselSource(l) && l.IsLogicallyVisible))
        {
            if (ResolveGroup(layer.ZoneId) is not null)
                continue;
            if (!IsLayerCenterInZone(layer, zone))
                continue;
            float area = LayerArea(layer);
            if (area >= bestArea)
            {
                bestArea = area;
                best = layer;
            }
        }

        return best;
    }

    private static float LayerArea(CompositionLayer layer)
    {
        var (_, _, bw, bh) = layer.GetMapBounds();
        return bw * bh;
    }

    public static string? DescribeMissingWallLayers(
        IReadOnlyList<ZoneDefinition> zones,
        IReadOnlyList<CompositionLayer> layers)
    {
        var missing = new List<string>();
        if (FindLayerForGroup(WallGroup.West, zones, layers) is null)
            missing.Add("West");
        if (FindLayerForGroup(WallGroup.Ost, zones, layers) is null)
            missing.Add("East");
        if (FindLayerForGroup(WallGroup.Sud, zones, layers) is null)
            missing.Add("South");
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
        var copy = new CompositionLayer
        {
            Name = source.Name + " " + MovingLayerMarker,
            SourceKind = source.SourceKind,
            SourceKey = source.SourceKey,
            X = source.X,
            Y = source.Y,
            Scale = source.Scale,
            RotationDegrees = source.RotationDegrees,
            Room3DRotationDegrees = source.Room3DRotationDegrees,
            Room3DFlipVertical = source.Room3DFlipVertical,
            Opacity = source.Opacity <= 0 ? 1f : source.Opacity,
            ScaleMode = source.ScaleMode,
            ZoneId = null,
            Visible = true,
            ParentGroupVisible = true,
            ParentGroupDrawOpacity = 1f,
            ZIndex = source.ZIndex + 1,
            NativeWidth = source.NativeWidth,
            NativeHeight = source.NativeHeight,
            BlackKeyEnabled = source.BlackKeyEnabled,
            BlackKeyThreshold = source.BlackKeyThreshold
        };
        copy.SetCrop(source.CropX, source.CropY, source.CropW, source.CropH);
        copy.SnapDrawOpacity();
        return copy;
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
        if (westLayer == ostLayer || ostLayer == sudLayer || sudLayer == westLayer)
            return null;

        return BuildSimultaneousCycle(zones, westLayer, ostLayer, sudLayer, direction);
    }

    public static WallSimultaneousCycle BuildSimultaneousCycle(
        IReadOnlyList<ZoneDefinition> zones,
        CompositionLayer westLayer,
        CompositionLayer ostLayer,
        CompositionLayer sudLayer,
        WallCarouselDirection direction = WallCarouselDirection.WestToOst)
    {
        var westZone = BuildTargetZone(WallGroup.West, zones);
        var ostZone = BuildTargetZone(WallGroup.Ost, zones);
        var sudZone = BuildTargetZone(WallGroup.Sud, zones);

        var tracks = new List<WallMotionTrack>();
        var commits = new List<WallCommitSpec>();
        var movingCopies = new List<WallMovingCopy>();

        if (direction == WallCarouselDirection.WestToOst)
        {
            AddSlidePair(tracks, movingCopies, WallGroup.West, westLayer, sudLayer, westZone, SlideFrom.Left);
            AddSlidePair(tracks, movingCopies, WallGroup.Ost, ostLayer, westLayer, ostZone, SlideFrom.Right);
            AddSlidePair(tracks, movingCopies, WallGroup.Sud, sudLayer, ostLayer, sudZone, SlideFrom.Bottom);

            AddCommit(commits, westLayer, ostZone);
            AddCommit(commits, ostLayer, sudZone);
            AddCommit(commits, sudLayer, westZone);
        }
        else
        {
            AddSlidePair(tracks, movingCopies, WallGroup.West, westLayer, ostLayer, westZone, SlideFrom.Right);
            AddSlidePair(tracks, movingCopies, WallGroup.Sud, sudLayer, westLayer, sudZone, SlideFrom.Top);
            AddSlidePair(tracks, movingCopies, WallGroup.Ost, ostLayer, sudLayer, ostZone, SlideFrom.Left);

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

    private static void AddSlidePair(
        List<WallMotionTrack> tracks,
        List<WallMovingCopy> movingCopies,
        WallGroup group,
        CompositionLayer outgoingSource,
        CompositionLayer incomingSource,
        ZoneDefinition targetZone,
        SlideFrom from)
    {
        ComputeWallPose(incomingSource, targetZone, out float inEndX, out float inEndY, out float inScale, out float inRot);

        var outCopy = CreateMovingCopy(outgoingSource);
        outCopy.SetOpacityImmediate(outgoingSource.Opacity <= 0 ? 1f : outgoingSource.Opacity);
        movingCopies.Add(new WallMovingCopy
        {
            Source = outgoingSource,
            Copy = outCopy,
            InsertAbove = outgoingSource
        });

        var outSize = outCopy.GetDrawSize();
        MapAabb(outCopy.X, outCopy.Y, outSize.X, outSize.Y, outCopy.RotationDegrees,
            out float omx, out float omy, out float omw, out float omh);

        var (inSrcW, inSrcH) = incomingSource.GetSourcePixelSize();
        float inDw = MathF.Max(inSrcW * inScale, 1f);
        float inDh = MathF.Max(inSrcH * inScale, 1f);
        MapAabb(inEndX, inEndY, inDw, inDh, inRot, out float imxEnd, out float imyEnd, out float imw, out float imh);

        float imx = imxEnd;
        float imy = imyEnd;
        switch (from)
        {
            case SlideFrom.Left:
                imx = omx - imw + SeamOverlapPx;
                break;
            case SlideFrom.Right:
                imx = omx + omw - SeamOverlapPx;
                break;
            case SlideFrom.Top:
                imy = omy - imh + SeamOverlapPx;
                break;
            case SlideFrom.Bottom:
                imy = omy + omh - SeamOverlapPx;
                break;
        }

        LayerPosFromAabb(imx, imy, imw, imh, inDw, inDh, out float inStartX, out float inStartY);
        float dx = inEndX - inStartX;
        float dy = inEndY - inStartY;

        tracks.Add(new WallMotionTrack
        {
            Group = group,
            Layer = outCopy,
            IsIncomingCopy = false,
            StartX = outCopy.X,
            StartY = outCopy.Y,
            StartScale = outCopy.Scale,
            Rotation = outCopy.RotationDegrees,
            EndX = outCopy.X + dx,
            EndY = outCopy.Y + dy,
            EndScale = outCopy.Scale
        });

        var inCopy = CreateMovingCopy(incomingSource);
        inCopy.RotationDegrees = inRot;
        inCopy.Scale = inScale;
        inCopy.X = inStartX;
        inCopy.Y = inStartY;
        inCopy.SetOpacityImmediate(incomingSource.Opacity <= 0 ? 1f : incomingSource.Opacity);
        movingCopies.Add(new WallMovingCopy
        {
            Source = incomingSource,
            Copy = inCopy,
            InsertAbove = outgoingSource
        });

        tracks.Add(new WallMotionTrack
        {
            Group = group,
            Layer = inCopy,
            IsIncomingCopy = true,
            StartX = inStartX,
            StartY = inStartY,
            StartScale = inScale,
            Rotation = inRot,
            EndX = inEndX,
            EndY = inEndY,
            EndScale = inScale
        });
    }

    private static void MapAabb(float x, float y, float dw, float dh, float rotationDeg,
        out float mx, out float my, out float mw, out float mh)
    {
        float rot = ((rotationDeg % 360f) + 360f) % 360f;
        bool swap = MathF.Abs(rot - 90f) < 1f || MathF.Abs(rot - 270f) < 1f;
        mw = swap ? dh : dw;
        mh = swap ? dw : dh;
        float cx = x + dw * 0.5f;
        float cy = y + dh * 0.5f;
        mx = cx - mw * 0.5f;
        my = cy - mh * 0.5f;
    }

    private static void LayerPosFromAabb(float mx, float my, float mw, float mh, float dw, float dh,
        out float x, out float y)
    {
        x = mx + mw * 0.5f - dw * 0.5f;
        y = my + mh * 0.5f - dh * 0.5f;
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
