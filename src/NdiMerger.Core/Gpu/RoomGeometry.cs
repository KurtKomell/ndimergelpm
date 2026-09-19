using System.Numerics;
using NdiMerger.Core.Models;

namespace NdiMerger.Core.Gpu;

public enum RoomPanelKind
{
    Floor,
    Wall
}

/// <summary>
/// One foldable room surface derived from the pixelmap zone layout.
/// Local UV: u along the hinge (0..1), v from floor/hinge (0) to top (1).
/// </summary>
public sealed class RoomPanel
{
    public required string ZoneId { get; init; }
    public required string Name { get; init; }
    public required RoomPanelKind Kind { get; init; }
    public float ContentWidth { get; init; }
    public float ContentHeight { get; init; }
    /// <summary>World XZ hinge start (or floor corner) before panel offset.</summary>
    public Vector3 HingeStart { get; init; }
    /// <summary>Unit direction along the hinge / floor edge.</summary>
    public Vector3 HingeDir { get; init; }
    /// <summary>Length of the hinge edge in world units.</summary>
    public float HingeLength { get; init; }
    /// <summary>Unit outward (flat) direction in XZ; for walls, fold from upright toward this.</summary>
    public Vector3 Outward { get; init; }
    /// <summary>Wall height; 0 for floor.</summary>
    public float Height { get; init; }
    /// <summary>Mutable XZ offset while dragging in the 3D preview.</summary>
    public Vector2 Offset { get; set; }
}

public readonly struct RoomQuad
{
    public required Vector3 C0 { get; init; }
    public required Vector3 C1 { get; init; }
    public required Vector3 C2 { get; init; }
    public required Vector3 C3 { get; init; }
    public required Vector3 Normal { get; init; }
    public required string ZoneId { get; init; }
}

/// <summary>
/// Builds a foldable 3D room from pixelmap zones. Floor lies in XZ at Y=0;
/// canvas pixels = world units; origin = floor top-left (NW).
/// </summary>
public static class RoomGeometry
{
    public const float SnapThresholdPx = 40f;

    public static List<RoomPanel> BuildPanels(IReadOnlyList<ZoneDefinition> zones)
    {
        var floor = Require(zones, "floor");
        float fw = floor.Width;
        float fh = floor.Height;
        var west = FloorReflection.BuildWestFloorPoly(floor, zones);

        Vector3 W(Vector2 canvas) => new(canvas.X - floor.X, 0f, canvas.Y - floor.Y);

        var panels = new List<RoomPanel>();

        panels.Add(new RoomPanel
        {
            ZoneId = floor.Id,
            Name = floor.Name,
            Kind = RoomPanelKind.Floor,
            ContentWidth = floor.ContentWidth > 0 ? floor.ContentWidth : fw,
            ContentHeight = floor.ContentHeight > 0 ? floor.ContentHeight : fh,
            HingeStart = Vector3.Zero,
            HingeDir = Vector3.UnitX,
            HingeLength = fw,
            Outward = Vector3.UnitZ,
            Height = 0f
        });

        var sud = Require(zones, "wall_sud");
        float sudH = sud.ContentHeight > 0 ? sud.ContentHeight : sud.Width;
        float sudL = sud.ContentWidth > 0 ? sud.ContentWidth : sud.Height;
        panels.Add(MakeWall(
            sud,
            hingeStart: Vector3.Zero,
            hingeEnd: new Vector3(0f, 0f, MathF.Min(sudL, fh)),
            outward: -Vector3.UnitX,
            height: sudH));

        // West outline: poly[1]=NW, [2]=west2 start, [3]=kink, [4]=east of west3
        var w1 = Find(zones, "wall_west_1");
        var w2 = Find(zones, "wall_west_2");
        var w3 = Find(zones, "wall_west_3");
        var p1 = W(west[1]);
        var p2 = W(west[2]);
        var p3 = W(west[3]);
        var p4 = W(west[4]);

        if (w1 is not null)
        {
            float h = w1.ContentHeight > 0 ? w1.ContentHeight : w1.Height;
            panels.Add(MakeWall(w1, p1, p2, -Vector3.UnitZ, h));
        }
        if (w2 is not null)
        {
            float h = w2.ContentHeight > 0 ? w2.ContentHeight : w2.Height;
            var edge = p3 - p2;
            var outward = Vector3.Normalize(new Vector3(-edge.Z, 0f, edge.X)); // left of edge along west→kink
            // Prefer outward pointing north-west (away from floor interior)
            var mid = (p2 + p3) * 0.5f;
            var toCenter = new Vector3(fw * 0.5f, 0f, fh * 0.5f) - mid;
            if (Vector3.Dot(outward, toCenter) > 0f)
                outward = -outward;
            panels.Add(MakeWall(w2, p2, p3, outward, h));
        }
        if (w3 is not null)
        {
            float h = w3.ContentHeight > 0 ? w3.ContentHeight : w3.Height;
            panels.Add(MakeWall(w3, p3, p4, -Vector3.UnitZ, h));
        }

        var est = Find(zones, "wall_est");
        if (est is not null)
        {
            float h = est.ContentHeight > 0 ? est.ContentHeight : est.Height;
            panels.Add(MakeWall(
                est,
                hingeStart: new Vector3(0f, 0f, fh),
                hingeEnd: new Vector3(fw, 0f, fh),
                outward: Vector3.UnitZ,
                height: h));
        }

        var stage = Find(zones, "wall_stage");
        if (stage is not null)
        {
            float h = stage.ContentHeight > 0 ? stage.ContentHeight : stage.Width;
            float z0 = stage.Y - floor.Y;
            float z1 = z0 + stage.Height;
            panels.Add(MakeWall(
                stage,
                hingeStart: new Vector3(fw, 0f, z0),
                hingeEnd: new Vector3(fw, 0f, z1),
                outward: Vector3.UnitX,
                height: h));
        }

        var nord = Find(zones, "wall_nord");
        if (nord is not null)
        {
            float h = nord.ContentHeight > 0 ? nord.ContentHeight : nord.Width;
            float z0 = nord.Y - floor.Y;
            float z1 = z0 + nord.Height;
            panels.Add(MakeWall(
                nord,
                hingeStart: new Vector3(fw, 0f, z0),
                hingeEnd: new Vector3(fw, 0f, z1),
                outward: Vector3.UnitX,
                height: h));
        }

        return panels;
    }

    private static RoomPanel MakeWall(
        ZoneDefinition zone,
        Vector3 hingeStart,
        Vector3 hingeEnd,
        Vector3 outward,
        float height)
    {
        var edge = hingeEnd - hingeStart;
        float len = edge.Length();
        var dir = len > 1e-3f ? edge / len : Vector3.UnitX;
        outward = new Vector3(outward.X, 0f, outward.Z);
        if (outward.LengthSquared() < 1e-6f)
            outward = Vector3.UnitX;
        else
            outward = Vector3.Normalize(outward);

        float cw = zone.ContentWidth > 0 ? zone.ContentWidth : len;
        float ch = zone.ContentHeight > 0 ? zone.ContentHeight : height;

        return new RoomPanel
        {
            ZoneId = zone.Id,
            Name = zone.Name,
            Kind = RoomPanelKind.Wall,
            ContentWidth = cw,
            ContentHeight = ch,
            HingeStart = hingeStart,
            HingeDir = dir,
            HingeLength = len,
            Outward = outward,
            Height = height
        };
    }

    public static RoomQuad Evaluate(RoomPanel panel, float assembleT)
    {
        assembleT = Math.Clamp(assembleT, 0f, 1f);
        var offset = new Vector3(panel.Offset.X, 0f, panel.Offset.Y);

        if (panel.Kind == RoomPanelKind.Floor)
        {
            float w = panel.ContentWidth;
            float d = panel.ContentHeight;
            var f0 = offset;
            var f1 = offset + new Vector3(w, 0f, 0f);
            var f2 = offset + new Vector3(w, 0f, d);
            var f3 = offset + new Vector3(0f, 0f, d);
            return new RoomQuad
            {
                C0 = f0, C1 = f1, C2 = f2, C3 = f3,
                Normal = Vector3.UnitY,
                ZoneId = panel.ZoneId
            };
        }

        var up = Vector3.UnitY;
        var hinge0 = panel.HingeStart + offset;
        var hinge1 = panel.HingeStart + panel.HingeDir * panel.HingeLength + offset;
        var u0 = hinge0;
        var u1 = hinge1;
        var u2 = hinge1 + up * panel.Height;
        var u3 = hinge0 + up * panel.Height;

        float fold = (1f - assembleT) * (MathF.PI * 0.5f);
        var axis = panel.HingeDir;
        var c0 = RotateAroundAxis(u0, hinge0, axis, fold, panel.Outward, up);
        var c1 = RotateAroundAxis(u1, hinge0, axis, fold, panel.Outward, up);
        var c2 = RotateAroundAxis(u2, hinge0, axis, fold, panel.Outward, up);
        var c3 = RotateAroundAxis(u3, hinge0, axis, fold, panel.Outward, up);

        var n = Vector3.Cross(c1 - c0, c3 - c0);
        if (n.LengthSquared() > 1e-8f)
            n = Vector3.Normalize(n);
        else
            n = -panel.Outward;

        // Face the room interior: reverse hinge winding so the textured side is front, not back.
        return new RoomQuad
        {
            C0 = c1, C1 = c0, C2 = c3, C3 = c2,
            Normal = -n,
            ZoneId = panel.ZoneId
        };
    }

    private static Vector3 RotateAroundAxis(
        Vector3 point,
        Vector3 pivot,
        Vector3 axis,
        float angle,
        Vector3 outward,
        Vector3 up)
    {
        // Determine signed rotation so +angle moves up toward outward
        var cross = Vector3.Cross(up, outward);
        float sign = MathF.Sign(Vector3.Dot(cross, axis));
        if (MathF.Abs(sign) < 0.1f)
            sign = 1f;
        angle *= sign;

        var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        return pivot + Vector3.Transform(point - pivot, q);
    }

    public static void SnapPanel(RoomPanel panel, IReadOnlyList<RoomPanel> all, float assembleT)
    {
        if (panel.Kind != RoomPanelKind.Wall)
            return;

        var self = Evaluate(panel, assembleT);
        // Vertical edges: (c0,c3) and (c1,c2) — use bottom endpoints c0/c1 in XZ
        var edges = new[] { Xz(self.C0), Xz(self.C1) };
        Vector2 bestDelta = default;
        float bestDist = SnapThresholdPx;

        foreach (var other in all)
        {
            if (ReferenceEquals(other, panel) || other.Kind != RoomPanelKind.Wall)
                continue;
            var oq = Evaluate(other, assembleT);
            var targets = new[] { Xz(oq.C0), Xz(oq.C1) };
            foreach (var e in edges)
            {
                foreach (var t in targets)
                {
                    var d = t - e;
                    float dist = d.Length();
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestDelta = d;
                    }
                }
            }
        }

        if (bestDist < SnapThresholdPx)
            panel.Offset += bestDelta;
    }

    public static bool TryPick(
        IReadOnlyList<RoomPanel> panels,
        float assembleT,
        Vector3 rayOrigin,
        Vector3 rayDir,
        out string zoneId,
        out float distance)
    {
        zoneId = "";
        distance = float.MaxValue;
        bool hit = false;

        foreach (var panel in panels)
        {
            var q = Evaluate(panel, assembleT);
            if (RayHitsQuad(rayOrigin, rayDir, q, out float t) && t < distance)
            {
                distance = t;
                zoneId = panel.ZoneId;
                hit = true;
            }
        }

        return hit;
    }

    private static bool RayHitsQuad(Vector3 origin, Vector3 dir, RoomQuad q, out float t)
    {
        t = 0f;
        return RayHitsTriangle(origin, dir, q.C0, q.C1, q.C2, out t)
               || RayHitsTriangle(origin, dir, q.C0, q.C2, q.C3, out t);
    }

    private static bool RayHitsTriangle(
        Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        const float eps = 1e-5f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps)
            return false;
        float inv = 1f / det;
        var s = origin - v0;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f)
            return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f)
            return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > eps;
    }

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    private static ZoneDefinition Require(IReadOnlyList<ZoneDefinition> zones, string id) =>
        Find(zones, id) ?? throw new InvalidOperationException($"Zone '{id}' missing from pixelmap.");

    private static ZoneDefinition? Find(IReadOnlyList<ZoneDefinition> zones, string id) =>
        zones.FirstOrDefault(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
