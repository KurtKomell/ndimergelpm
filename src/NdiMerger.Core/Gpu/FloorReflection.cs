using System.Numerics;
using NdiMerger.Core.Models;

namespace NdiMerger.Core.Gpu;

public enum FloorReflectEdge
{
    West = 0,
    Ost = 1,
    Sud = 2,
    Nord = 3
}

public readonly struct FloorReflectionSettings
{
    public bool Enabled { get; init; }
    public float Opacity { get; init; }
    public float BlurPixels { get; init; }
    public float Length { get; init; }
    public float Angle { get; init; }
    public float FadeStart { get; init; }

    public static FloorReflectionSettings Default { get; } = new()
    {
        Enabled = true,
        Opacity = 0.45f,
        BlurPixels = 8f,
        Length = 0.4f,
        Angle = 0.35f,
        FadeStart = 0f
    };
}

public readonly struct FloorReflectQuad
{
    public required Vector2 C0 { get; init; }
    public required Vector2 C1 { get; init; }
    public required Vector2 C2 { get; init; }
    public required Vector2 C3 { get; init; }
    public required Vector2 Uv0 { get; init; }
    public required Vector2 Uv1 { get; init; }
    public required Vector2 Uv2 { get; init; }
    public required Vector2 Uv3 { get; init; }
    public required FloorReflectEdge Edge { get; init; }
    public required float LengthPx { get; init; }
    public required Vector2 MiterA { get; init; }
    public required Vector2 MiterB { get; init; }
    /// <summary>Mitred corner point at <see cref="MiterA"/>; the shader cuts along MiterA→CutA.</summary>
    public Vector2 CutA { get; init; }
    /// <summary>Mitred corner point at <see cref="MiterB"/>; the shader cuts along MiterB→CutB.</summary>
    public Vector2 CutB { get; init; }
    /// <summary>Sideways fade width in px at the A cut; 0 keeps a hard edge (shared corner).</summary>
    public float SideFadeA { get; init; }
    /// <summary>Sideways fade width in px at the B cut; 0 keeps a hard edge (shared corner).</summary>
    public float SideFadeB { get; init; }
    public required float LayerOpacity { get; init; }
}

/// <summary>
/// Maps a wall layer onto the floor as a perspective reflection.
/// Strips stay perpendicular to the wall; corners are mitred so neighbours
/// meet on one cut and do not overlap. 90° room corners use 45°. West 1/2
/// and West 2/3 use the real floor-edge angle. Stage is never reflected.
/// </summary>
public static class FloorReflection
{
    // Neighbour stubs only drive OffsetPolyMiter. Zero-travel stub segments
    // are skipped, so the strip itself stays axis-aligned / along West 2.
    // West outline is BuildWestFloorPoly (West 2 length along the diagonal).
    private static readonly Vector2[] SudPoly =
    [
        new(4326, 1200), // West stub → 45° at NW
        new(1100, 1200), // West / Süd
        new(1100, 4598), // Süd / Ost
        new(6838, 4598), // Ost stub → 45° at SW
    ];

    private static readonly Vector2[] OstPoly =
    [
        new(1100, 1200), // Süd stub → 45° at SW
        new(1100, 4598), // Süd / Ost
        new(6838, 4598), // Ost / Nord
        new(6838, 3306), // Nord stub → 45° at SE
    ];

    private static readonly Vector2[] NordPoly =
    [
        new(1100, 4598), // Ost stub → 45° at SE
        new(6838, 4598), // Ost / Nord
        new(6838, 3306), // Nord / stage (straight)
    ];

    public static void GetWestFloorKinks(
        ZoneDefinition floor,
        out Vector2 uvA,
        out Vector2 uvB,
        IReadOnlyList<ZoneDefinition>? zones = null)
    {
        var poly = BuildWestFloorPoly(floor, zones);
        uvA = ToFloorUv(poly[2], floor);
        uvB = ToFloorUv(poly[3], floor);
    }

    /// <summary>
    /// Floor outline along West: West 1 horizontal, then West 2 as a diagonal
    /// whose Euclidean length equals the West 2 panel, then West 3 horizontal
    /// at its panel length up to the floor's east edge.
    /// </summary>
    public static Vector2[] BuildWestFloorPoly(ZoneDefinition floor, IReadOnlyList<ZoneDefinition>? zones = null)
    {
        float west2StartX = 4326f;
        float west2Len = 1866f;
        float west3Len = 880f;
        if (zones is not null)
        {
            var w1 = FindZone(zones, "wall_west_1");
            var w2 = FindZone(zones, "wall_west_2");
            var w3 = FindZone(zones, "wall_west_3");
            if (w2 is not null)
            {
                west2StartX = w2.X;
                west2Len = w2.ContentWidth > 0 ? w2.ContentWidth : w2.Width;
            }
            else if (w1 is not null)
            {
                west2StartX = w1.X + w1.Width;
            }
            if (w3 is not null)
                west3Len = w3.ContentWidth > 0 ? w3.ContentWidth : w3.Width;
        }

        float floorRight = floor.X + floor.Width;
        float floorTop = floor.Y;
        west3Len = MathF.Min(west3Len, MathF.Max(8f, floorRight - west2StartX - 8f));
        float dx = (floorRight - west3Len) - west2StartX;
        dx = Math.Clamp(dx, 1f, west2Len);
        float dy = MathF.Sqrt(MathF.Max(west2Len * west2Len - dx * dx, 0f));

        return
        [
            new(floor.X, floor.Y + floor.Height),
            new(floor.X, floorTop),
            new(west2StartX, floorTop),
            new(west2StartX + dx, floorTop + dy),
            new(floorRight, floorTop + dy)
        ];
    }

    private static ZoneDefinition? FindZone(IReadOnlyList<ZoneDefinition> zones, string id) =>
        zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static Vector2 ToFloorUv(Vector2 canvas, ZoneDefinition floor) => new(
        (canvas.X - floor.X) / MathF.Max(floor.Width, 1f),
        (canvas.Y - floor.Y) / MathF.Max(floor.Height, 1f));

    public static FloorReflectEdge? ResolveEdge(
        CompositionLayer layer,
        ZoneDefinition floor,
        ZoneDefinition? nord)
    {
        var fromZone = FromZoneId(layer.ZoneId);
        if (fromZone is not null)
            return fromZone;

        return InferFromPosition(layer, floor, nord);
    }

    public static FloorReflectEdge? FromZoneId(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return null;
        if (zoneId.Equals("floor", StringComparison.OrdinalIgnoreCase))
            return null;
        if (zoneId.Equals("wall_stage", StringComparison.OrdinalIgnoreCase))
            return null;
        if (zoneId.StartsWith("wall_west", StringComparison.OrdinalIgnoreCase))
            return FloorReflectEdge.West;
        if (zoneId.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
            return FloorReflectEdge.Ost;
        if (zoneId.Equals("wall_sud", StringComparison.OrdinalIgnoreCase))
            return FloorReflectEdge.Sud;
        if (zoneId.Equals("wall_nord", StringComparison.OrdinalIgnoreCase))
            return FloorReflectEdge.Nord;
        return null;
    }

    public static IReadOnlyList<FloorReflectQuad> Build(
        CompositionLayer layer,
        ZoneDefinition floor,
        ZoneDefinition? nord,
        float length,
        float angle,
        IReadOnlyList<ZoneDefinition>? zones = null)
    {
        var edge = ResolveEdge(layer, floor, nord);
        if (edge is null)
            return [];
        if (layer.NativeWidth <= 0 || layer.NativeHeight <= 0)
            return [];
        if (layer.EffectiveDrawOpacity < 0.02f)
            return [];

        length = Math.Clamp(length, 0.05f, 1f);
        angle = Math.Clamp(angle, 0f, 1f);
        float opacity = Math.Clamp(layer.EffectiveDrawOpacity, 0f, 1f);
        var vanishing = new Vector2(floor.X + floor.Width * 0.5f, floor.Y + floor.Height * 0.5f);
        float lengthPx = MathF.Max(8f, floor.Height * length);
        float offsetDist = lengthPx * (1f - Math.Clamp(angle, 0f, 1f) * 0.55f);

        return edge.Value switch
        {
            FloorReflectEdge.West => BuildAlongPoly(layer, BuildWestFloorPoly(floor, zones), edge.Value, alongY: false, flipU: false, flipV: false, offsetDist, vanishing, opacity),
            FloorReflectEdge.Sud => BuildAlongPoly(layer, SudPoly, edge.Value, alongY: true, flipU: SudFlipU(layer), flipV: SudFlipV(layer), offsetDist, vanishing, opacity),
            FloorReflectEdge.Ost => BuildAlongPoly(layer, OstPoly, edge.Value, alongY: false, flipU: true, flipV: false, offsetDist, vanishing, opacity),
            FloorReflectEdge.Nord => BuildAlongPoly(layer, NordPoly, edge.Value, alongY: true, flipU: false, flipV: false, offsetDist, vanishing, opacity),
            _ => []
        };
    }

    public static IReadOnlyList<FloorReflectQuad> TryBuild(
        CompositionLayer layer,
        ZoneDefinition floor,
        ZoneDefinition? nord,
        float length,
        float angle,
        IReadOnlyList<ZoneDefinition>? zones = null) => Build(layer, floor, nord, length, angle, zones);

    public static Vector2 ToRt(Vector2 canvas, ZoneDefinition floor, int rtW, int rtH)
    {
        float x = (canvas.X - floor.X) / MathF.Max(floor.Width, 1f) * rtW;
        float y = (canvas.Y - floor.Y) / MathF.Max(floor.Height, 1f) * rtH;
        return new Vector2(x, y);
    }

    public static bool IsFloorLayer(CompositionLayer layer) =>
        layer.ZoneId is not null &&
        layer.ZoneId.Equals("floor", StringComparison.OrdinalIgnoreCase);

    // 90° CCW puts texture v=0 on the floor seam and u along +Y.
    // 270° (source default) puts v=1 on the seam and reverses u along the wall.
    private static bool SudFlipU(CompositionLayer layer)
    {
        float rot = ((layer.RotationDegrees % 360f) + 360f) % 360f;
        return MathF.Abs(rot - 270f) < 1f;
    }

    private static bool SudFlipV(CompositionLayer layer)
    {
        float rot = ((layer.RotationDegrees % 360f) + 360f) % 360f;
        return MathF.Abs(rot - 90f) < 1f;
    }

    private static List<FloorReflectQuad> BuildAlongPoly(
        CompositionLayer layer,
        Vector2[] poly,
        FloorReflectEdge edge,
        bool alongY,
        bool flipU,
        bool flipV,
        float offsetDist,
        Vector2 vanishing,
        float opacity)
    {
        var list = new List<FloorReflectQuad>();
        var (bx, by, bw, bh) = layer.GetMapBounds();
        float layer0 = alongY ? by : bx;
        float layerSize = alongY ? bh : bw;
        float layer1 = layer0 + layerSize;
        var offset = OffsetPolyMiter(poly, offsetDist, vanishing);
        float vFar = flipV ? 1f : 0f;
        float vSeam = flipV ? 0f : 1f;

        for (int i = 0; i < poly.Length - 1; i++)
        {
            var a = poly[i];
            var b = poly[i + 1];
            float axisA = alongY ? a.Y : a.X;
            float axisB = alongY ? b.Y : b.X;
            if (MathF.Abs(axisB - axisA) < 1f)
                continue;

            float seg0 = MathF.Min(axisA, axisB);
            float seg1 = MathF.Max(axisA, axisB);
            float overlap0 = MathF.Max(layer0, seg0);
            float overlap1 = MathF.Min(layer1, seg1);
            if (overlap1 - overlap0 < 2f)
                continue;

            float u0 = (overlap0 - layer0) / MathF.Max(layerSize, 1f);
            float u1 = (overlap1 - layer0) / MathF.Max(layerSize, 1f);
            if (flipU)
            {
                u0 = 1f - u0;
                u1 = 1f - u1;
            }

            float t0 = (overlap0 - axisA) / (axisB - axisA);
            float t1 = (overlap1 - axisA) / (axisB - axisA);
            var seam0 = Vector2.Lerp(a, b, t0);
            var seam1 = Vector2.Lerp(a, b, t1);
            // Straight extrusion: the strip stays a rectangle so the mirrored
            // content is not sheared. Corners are cut by the shader along
            // MiterA→CutA / MiterB→CutB (the mitred bisectors).
            var inward = InwardOnFloor(b - a, vanishing, a);
            var far0 = seam0 + inward * offsetDist;
            var far1 = seam1 + inward * offsetDist;

            // Open ends of the polyline (no neighbouring wall to meet) fade out
            // sideways instead of ending on a hard cut.
            float sideFade = MathF.Min(offsetDist, (b - a).Length());

            list.Add(new FloorReflectQuad
            {
                C0 = far0,
                C1 = far1,
                C2 = seam0,
                C3 = seam1,
                Uv0 = layer.MapCropUv(u0, vFar),
                Uv1 = layer.MapCropUv(u1, vFar),
                Uv2 = layer.MapCropUv(u0, vSeam),
                Uv3 = layer.MapCropUv(u1, vSeam),
                Edge = edge,
                LengthPx = offsetDist,
                MiterA = a,
                MiterB = b,
                CutA = offset[i],
                CutB = offset[i + 1],
                SideFadeA = i == 0 ? sideFade : 0f,
                SideFadeB = i == poly.Length - 2 ? sideFade : 0f,
                LayerOpacity = opacity
            });
        }

        return list;
    }

    /// <summary>
    /// Inward offset of a floor-edge polyline using miter joins.
    /// West/Süd is a 90° room corner → 45° gehrung toward the floor.
    /// West 1 / West 2 keeps the real kink (~29°), not 90°.
    /// </summary>
    private static Vector2[] OffsetPolyMiter(Vector2[] poly, float dist, Vector2 vanishing)
    {
        var offset = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 n0, n1;
            if (i == 0)
            {
                n0 = n1 = InwardOnFloor(poly[1] - poly[0], vanishing, poly[0]);
            }
            else if (i == poly.Length - 1)
            {
                n0 = n1 = InwardOnFloor(poly[i] - poly[i - 1], vanishing, poly[i]);
            }
            else
            {
                n0 = InwardOnFloor(poly[i] - poly[i - 1], vanishing, poly[i]);
                n1 = InwardOnFloor(poly[i + 1] - poly[i], vanishing, poly[i]);
            }

            var miter = n0 + n1;
            if (miter.LengthSquared() < 1e-10f)
            {
                offset[i] = poly[i] + n0 * dist;
                continue;
            }

            miter = Vector2.Normalize(miter);
            float denom = Vector2.Dot(miter, n0);
            if (MathF.Abs(denom) < 0.12f)
                denom = MathF.CopySign(0.12f, denom);
            offset[i] = poly[i] + miter * (dist / denom);
        }

        return offset;
    }

    private static bool TryMirrorAxisAligned(
        CompositionLayer layer,
        ZoneDefinition floor,
        ZoneDefinition? nord,
        FloorReflectEdge edge,
        float length,
        float angle,
        Vector2 vanishing,
        float opacity,
        out FloorReflectQuad quad)
    {
        quad = default;
        var corners = LayerCorners(layer);
        GetSeam(edge, floor, out var seamOrigin, out var along, out var inward);

        float maxDist = 0f;
        var mirrored = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            float dist = Vector2.Dot(seamOrigin - corners[i], inward);
            if (dist < 0f) dist = 0f;
            mirrored[i] = corners[i] + inward * (2f * dist);
            if (dist > maxDist) maxDist = dist;
        }

        if (maxDist < 1f)
            return false;

        float floorDepth = edge is FloorReflectEdge.Ost ? floor.Height : floor.Width;
        float lengthPx = MathF.Max(8f, floorDepth * length);
        float squash = lengthPx / maxDist;
        GetMiters(edge, floor, nord, out var miterA, out var miterB);

        for (int i = 0; i < 4; i++)
        {
            float dist = Vector2.Dot(mirrored[i] - seamOrigin, inward);
            var alongPos = seamOrigin + along * Vector2.Dot(mirrored[i] - seamOrigin, along);
            mirrored[i] = alongPos + inward * (dist * squash);
        }

        quad = new FloorReflectQuad
        {
            C0 = mirrored[0],
            C1 = mirrored[1],
            C2 = mirrored[2],
            C3 = mirrored[3],
            Uv0 = layer.MapCropUv(0, 0),
            Uv1 = layer.MapCropUv(1, 0),
            Uv2 = layer.MapCropUv(0, 1),
            Uv3 = layer.MapCropUv(1, 1),
            Edge = edge,
            LengthPx = lengthPx,
            MiterA = miterA,
            MiterB = miterB,
            LayerOpacity = opacity
        };
        return true;
    }

    private static Vector2 InwardOnFloor(Vector2 along, Vector2 vanishing, Vector2 onSeam)
    {
        var n1 = new Vector2(-along.Y, along.X);
        var n2 = -n1;
        if (n1.LengthSquared() < 1e-6f)
            return Vector2.UnitY;
        n1 = Vector2.Normalize(n1);
        n2 = Vector2.Normalize(n2);
        return Vector2.Dot(vanishing - onSeam, n1) >= Vector2.Dot(vanishing - onSeam, n2) ? n1 : n2;
    }

    private static FloorReflectEdge? InferFromPosition(
        CompositionLayer layer,
        ZoneDefinition floor,
        ZoneDefinition? nord)
    {
        var (bx, by, bw, bh) = layer.GetMapBounds();
        if (bw <= 0 || bh <= 0)
            return null;

        float cx = bx + bw * 0.5f;
        float cy = by + bh * 0.5f;
        float floorRight = floor.X + floor.Width;
        float floorBottom = floor.Y + floor.Height;

        if (cy < floor.Y)
            return FloorReflectEdge.West;
        if (cy > floorBottom)
            return FloorReflectEdge.Ost;
        if (cx < floor.X)
            return FloorReflectEdge.Sud;
        if (cx > floorRight)
        {
            float nordTop = nord is not null ? nord.Y : floor.Y + floor.Height * 0.5f;
            if (cy >= nordTop)
                return FloorReflectEdge.Nord;
            return null;
        }

        return null;
    }

    public static void GetMiters(
        FloorReflectEdge edge,
        ZoneDefinition floor,
        ZoneDefinition? nord,
        out Vector2 a,
        out Vector2 b)
    {
        float l = floor.X;
        float t = floor.Y;
        float r = floor.X + floor.Width;
        float bot = floor.Y + floor.Height;

        switch (edge)
        {
            case FloorReflectEdge.West:
            {
                var west = BuildWestFloorPoly(floor);
                a = west[1];
                b = west[^1];
                break;
            }
            case FloorReflectEdge.Ost:
                a = new Vector2(l, bot);
                b = new Vector2(r, bot);
                break;
            case FloorReflectEdge.Sud:
                a = new Vector2(l, t);
                b = new Vector2(l, bot);
                break;
            default:
            {
                float top = nord is not null
                    ? Math.Clamp(nord.Y, t, bot)
                    : t;
                float bottom = nord is not null
                    ? Math.Clamp(nord.Y + nord.Height, t, bot)
                    : bot;
                a = new Vector2(r, top);
                b = new Vector2(r, bottom);
                break;
            }
        }
    }

    private static void GetSeam(
        FloorReflectEdge edge,
        ZoneDefinition floor,
        out Vector2 origin,
        out Vector2 along,
        out Vector2 inward)
    {
        float l = floor.X;
        float t = floor.Y;
        float r = floor.X + floor.Width;
        float bot = floor.Y + floor.Height;

        switch (edge)
        {
            case FloorReflectEdge.Ost:
                origin = new Vector2(l, bot);
                along = Vector2.UnitX;
                inward = -Vector2.UnitY;
                break;
            case FloorReflectEdge.Sud:
                origin = new Vector2(l, t);
                along = Vector2.UnitY;
                inward = Vector2.UnitX;
                break;
            default:
                origin = new Vector2(r, t);
                along = Vector2.UnitY;
                inward = -Vector2.UnitX;
                break;
        }
    }

    private static Vector2[] LayerCorners(CompositionLayer layer)
    {
        var size = layer.GetDrawSize();
        float w = size.X;
        float h = size.Y;
        float x = layer.X;
        float y = layer.Y;
        float rot = layer.RotationDegrees;

        Matrix4x4 world;
        if (MathF.Abs(rot) > 0.01f)
        {
            world =
                Matrix4x4.CreateTranslation(-0.5f, -0.5f, 0) *
                Matrix4x4.CreateScale(w, h, 1) *
                Matrix4x4.CreateRotationZ(rot * MathF.PI / 180f) *
                Matrix4x4.CreateTranslation(x + w * 0.5f, y + h * 0.5f, 0);
        }
        else
        {
            world = Matrix4x4.CreateScale(w, h, 1) * Matrix4x4.CreateTranslation(x, y, 0);
        }

        Vector2 Xform(float u, float v)
        {
            var p = Vector3.Transform(new Vector3(u, v, 0), world);
            return new Vector2(p.X, p.Y);
        }

        return [Xform(0, 0), Xform(1, 0), Xform(0, 1), Xform(1, 1)];
    }
}
