using System.Numerics;

namespace NdiMerger.Core.Models;

/// <summary>
/// Canonical 2D pixelmap wall orientations and the matching unwrap so 3D / NDI
/// always have the floor-adjacent edge at the bottom (mesh v=0 / frame bottom).
/// 2D AABB rotations stay as on the map; 3D sampling is derived from zone id.
/// </summary>
public static class WallFloorMapping
{
    /// <summary>
    /// 2D zone rotation on the pixelmap:
    /// West → down (0), Est → up (180), Sud → right (270), Stage/Nord → left (90).
    /// </summary>
    public static float CanonicalRotationDegrees(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return 0f;

        if (zoneId.StartsWith("wall_west", StringComparison.OrdinalIgnoreCase))
            return 0f;
        if (zoneId.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
            return 180f;
        if (zoneId.Equals("wall_sud", StringComparison.OrdinalIgnoreCase))
            return 270f;
        if (zoneId.Equals("wall_stage", StringComparison.OrdinalIgnoreCase) ||
            zoneId.Equals("wall_nord", StringComparison.OrdinalIgnoreCase))
            return 90f;
        if (zoneId.Equals("floor", StringComparison.OrdinalIgnoreCase))
            return 0f;
        return 0f;
    }

    public static void ApplyCanonicalRotations(IEnumerable<ZoneDefinition> zones)
    {
        foreach (var zone in zones)
        {
            if (string.IsNullOrEmpty(zone.Id) ||
                zone.Id.Equals("floor", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!zone.Id.StartsWith("wall_", StringComparison.OrdinalIgnoreCase))
                continue;
            zone.RotationDegrees = CanonicalRotationDegrees(zone.Id);
        }
    }

    /// <summary>
    /// CanvasOrient codes shared with Room3D shader / NDI blit:
    /// 0 none, 1 180, 2 90 CW, 3 90 CCW.
    /// </summary>
    public static int FloorBottomOrientCode(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return 0;
        if (zoneId.Equals("floor", StringComparison.OrdinalIgnoreCase))
            return 2; // nord edge → bottom
        if (zoneId.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (zoneId.Equals("wall_sud", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (zoneId.Equals("wall_stage", StringComparison.OrdinalIgnoreCase) ||
            zoneId.Equals("wall_nord", StringComparison.OrdinalIgnoreCase))
            return 3;
        // West: AABB already upright with floor at bottom.
        return 0;
    }

    /// <summary>
    /// Sample rectangle + orient so Room3D local UV has v=0 at the floor hinge
    /// and content reads left→right along the hinge.
    /// </summary>
    public static void GetRoom3DCanvasUv(
        ZoneDefinition zone,
        float canvasW,
        float canvasH,
        out Vector2 uvMin,
        out Vector2 uvMax,
        out int canvasOrient)
    {
        float u0 = zone.X / canvasW;
        float v0 = zone.Y / canvasH;
        float u1 = (zone.X + MathF.Max(zone.Width, 1f)) / canvasW;
        float v1 = (zone.Y + MathF.Max(zone.Height, 1f)) / canvasH;

        canvasOrient = FloorBottomOrientCode(zone.Id);

        if (zone.Id.StartsWith("wall_west", StringComparison.OrdinalIgnoreCase))
        {
            // Floor at AABB bottom; mesh v=0 must sample that edge.
            (v0, v1) = (v1, v0);
            // Wall quads reverse hinge U (front-face winding). Undo per panel so the
            // west ribbon stays one continuous strip across west_1/2/3.
            (u0, u1) = (u1, u0);
        }
        else if (zone.Id.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
        {
            // After 180° unwrap, hinge direction and vertical still need mirroring.
            // Geometry front-face winding already reverses U — only flip V here.
            (v0, v1) = (v1, v0);
        }

        uvMin = new Vector2(u0, v0);
        uvMax = new Vector2(u1, v1);
    }

    /// <summary>
    /// Rotate/flip a cropped AABB photo so it matches Room3D floor-at-bottom mapping.
    /// </summary>
    public static void ApplyPhotoOrientation(System.Drawing.Bitmap crop, string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return;

        if (zoneId.StartsWith("wall_west", StringComparison.OrdinalIgnoreCase))
        {
            // Match live west UV: flip Y for floor hinge + flip X to undo mesh U reverse.
            crop.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
            crop.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
            return;
        }

        if (zoneId.Equals("wall_est", StringComparison.OrdinalIgnoreCase))
        {
            // Match live Est: V flip only (U already reversed by mesh winding).
            crop.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
            return;
        }

        var flip = FloorBottomOrientCode(zoneId) switch
        {
            2 => System.Drawing.RotateFlipType.Rotate90FlipNone,
            3 => System.Drawing.RotateFlipType.Rotate270FlipNone,
            1 => System.Drawing.RotateFlipType.Rotate180FlipNone,
            _ => System.Drawing.RotateFlipType.RotateNoneFlipNone
        };
        if (flip != System.Drawing.RotateFlipType.RotateNoneFlipNone)
            crop.RotateFlip(flip);
    }
}
