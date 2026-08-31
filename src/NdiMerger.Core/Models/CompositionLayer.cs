using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace NdiMerger.Core.Models;

public sealed class CompositionLayer : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();

    private string _name = "Layer";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public SourceKind SourceKind { get; set; }
    public string SourceKey { get; set; } = "";

    private float _x;
    public float X { get => _x; set => SetField(ref _x, value); }

    private float _y;
    public float Y { get => _y; set => SetField(ref _y, value); }

    private float _scale = 1f;
    public float Scale { get => _scale; set => SetField(ref _scale, value); }

    private float _rotationDegrees;
    public float RotationDegrees { get => _rotationDegrees; set => SetField(ref _rotationDegrees, value); }

    private float _opacity = 1f;
    public float Opacity { get => _opacity; set => SetField(ref _opacity, value); }

    public ScaleMode ScaleMode { get; set; } = ScaleMode.Native;
    public string? ZoneId { get; set; }

    private bool _visible = true;
    public bool Visible { get => _visible; set => SetField(ref _visible, value); }

    public int ZIndex { get; set; }

    private int _nativeWidth;
    public int NativeWidth { get => _nativeWidth; set => SetField(ref _nativeWidth, value); }

    private int _nativeHeight;
    public int NativeHeight { get => _nativeHeight; set => SetField(ref _nativeHeight, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public Vector2 GetDrawSize()
    {
        if (NativeWidth <= 0 || NativeHeight <= 0)
            return Vector2.Zero;
        return new Vector2(NativeWidth * Scale, NativeHeight * Scale);
    }

    /// <summary>
    /// Axis-aligned bounds on the pixelmap after rotation (for hit-testing / overlays).
    /// </summary>
    public (float X, float Y, float W, float H) GetMapBounds()
    {
        var size = GetDrawSize();
        if (size.X <= 0 || size.Y <= 0)
            return (X, Y, 0, 0);

        float rot = ((RotationDegrees % 360) + 360) % 360;
        bool swap = MathF.Abs(rot - 90) < 1 || MathF.Abs(rot - 270) < 1;
        float bw = swap ? size.Y : size.X;
        float bh = swap ? size.X : size.Y;
        float cx = X + size.X * 0.5f;
        float cy = Y + size.Y * 0.5f;
        return (cx - bw * 0.5f, cy - bh * 0.5f, bw, bh);
    }

    /// <summary>
    /// Snap into a zone. Content is fitted to the zone's content size, then centered
    /// so that after rotation it sits in the zone's map rectangle.
    /// </summary>
    /// <param name="applyZoneRotation">
    /// When true (manual snap), uses the zone's rotation.
    /// When false (auto refit after size change), keeps the layer's current rotation.
    /// </param>
    public void ApplyZone(ZoneDefinition zone, ScaleMode mode, bool applyZoneRotation = true)
    {
        ZoneId = zone.Id;
        ScaleMode = mode;
        if (applyZoneRotation)
            RotationDegrees = zone.RotationDegrees;

        float targetW = zone.ContentWidth > 0 ? zone.ContentWidth : zone.Width;
        float targetH = zone.ContentHeight > 0 ? zone.ContentHeight : zone.Height;
        float zcx = zone.X + zone.Width * 0.5f;
        float zcy = zone.Y + zone.Height * 0.5f;

        if (NativeWidth <= 0 || NativeHeight <= 0)
        {
            Scale = 1f;
            X = zcx - targetW * 0.5f;
            Y = zcy - targetH * 0.5f;
            return;
        }

        float srcW = NativeWidth;
        float srcH = NativeHeight;

        Scale = mode switch
        {
            ScaleMode.FitZone => MathF.Min(targetW / srcW, targetH / srcH),
            ScaleMode.FillZone => MathF.Max(targetW / srcW, targetH / srcH),
            _ => 1f // Native
        };

        float dw = srcW * Scale;
        float dh = srcH * Scale;

        // Unrotated quad centered on zone center → after rotation it fills the map AABB.
        X = zcx - dw * 0.5f;
        Y = zcy - dh * 0.5f;
    }
}
