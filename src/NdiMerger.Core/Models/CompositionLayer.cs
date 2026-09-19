using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace NdiMerger.Core.Models;

public sealed class CompositionLayer : INotifyPropertyChanged
{
    public const float DrawOpacityEpsilon = 0.01f;

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

    private float _room3DRotationDegrees;
    /// <summary>
    /// 3D-preview-only rotation on the wall mesh. Does not change how the layer is
    /// drawn onto the canvas (West ribbon stays continuous).
    /// </summary>
    public float Room3DRotationDegrees
    {
        get => _room3DRotationDegrees;
        set
        {
            float n = value % 360f;
            if (n < 0) n += 360f;
            SetField(ref _room3DRotationDegrees, n);
        }
    }

    private bool _room3DFlipVertical;
    /// <summary>3D-preview-only top↔bottom UV flip on the wall (independent of 2D).</summary>
    public bool Room3DFlipVertical
    {
        get => _room3DFlipVertical;
        set => SetField(ref _room3DFlipVertical, value);
    }

    private float _opacity = 1f;
    /// <summary>Target opacity (0–1). Persisted; animated via <see cref="DrawOpacity"/>.</summary>
    public float Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Runtime animated opacity toward <see cref="TargetDrawOpacity"/>. Not persisted.</summary>
    public float DrawOpacity { get; set; } = 1f;

    private FadeAnimator _fade;

    /// <summary>Synced from parent <see cref="LayerGroup.DrawOpacity"/> each fade tick.</summary>
    public float ParentGroupDrawOpacity { get; set; } = 1f;

    public ScaleMode ScaleMode { get; set; } = ScaleMode.Native;
    public string? ZoneId { get; set; }

    /// <summary>Optional folder group; null = ungrouped (root).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// Synced from parent <see cref="LayerGroup.Visible"/>. When the group is hidden,
    /// members stay individually Visible but are not selectable.
    /// </summary>
    private bool _parentGroupVisible = true;
    public bool ParentGroupVisible
    {
        get => _parentGroupVisible;
        set => SetField(ref _parentGroupVisible, value);
    }

    private bool _visible = true;
    public bool Visible { get => _visible; set => SetField(ref _visible, value); }

    /// <summary>Logical visibility for hit-test / selection (no fade).</summary>
    public bool IsLogicallyVisible => Visible && ParentGroupVisible;

    /// <summary>Target draw opacity for fade animation (Visible off → 0).</summary>
    public float TargetDrawOpacity => Visible ? Opacity : 0f;

    /// <summary>Opacity used when compositing (layer × group fade).</summary>
    public float EffectiveDrawOpacity => DrawOpacity * ParentGroupDrawOpacity;

    /// <summary>Drawn while fading out until opacity reaches ~0.</summary>
    public bool IsEffectivelyVisible => EffectiveDrawOpacity > DrawOpacityEpsilon;

    public int ZIndex { get; set; }

    private int _nativeWidth;
    public int NativeWidth
    {
        get => _nativeWidth;
        set
        {
            if (!SetField(ref _nativeWidth, value)) return;
            ClampCropAfterNativeChange();
            NotifyCropSliderLimits();
        }
    }

    private int _nativeHeight;
    public int NativeHeight
    {
        get => _nativeHeight;
        set
        {
            if (!SetField(ref _nativeHeight, value)) return;
            ClampCropAfterNativeChange();
            NotifyCropSliderLimits();
        }
    }

    public int CropXMax => Math.Max(0, Math.Max(NativeWidth, 0) - DesiredCropW());
    public int CropYMax => Math.Max(0, Math.Max(NativeHeight, 0) - DesiredCropH());
    public int CropWMax => Math.Max(1, NativeWidth);
    public int CropHMax => Math.Max(1, NativeHeight);

    private int _cropX;
    /// <summary>Left of this layer's crop in native pixels. Persisted per layer.</summary>
    public int CropX
    {
        get => _cropX;
        set => SetField(ref _cropX, Math.Clamp(value, 0, CropXMax));
    }

    private int _cropY;
    /// <summary>Top of this layer's crop in native pixels. Persisted per layer.</summary>
    public int CropY
    {
        get => _cropY;
        set => SetField(ref _cropY, Math.Clamp(value, 0, CropYMax));
    }

    private int _cropW;
    /// <summary>Crop width in native pixels. 0 = full width.</summary>
    public int CropW
    {
        get => DesiredCropW();
        set
        {
            int nw = Math.Max(NativeWidth, 1);
            int w = value <= 0 ? nw : Math.Clamp(value, 1, nw);
            if (!SetField(ref _cropW, w)) return;
            // Keep the crop window on-image when width shrinks.
            if (_cropX > CropXMax)
                CropX = CropXMax;
            NotifyCropPanLimits();
        }
    }

    private int _cropH;
    /// <summary>Crop height in native pixels. 0 = full height.</summary>
    public int CropH
    {
        get => DesiredCropH();
        set
        {
            int nh = Math.Max(NativeHeight, 1);
            int h = value <= 0 ? nh : Math.Clamp(value, 1, nh);
            if (!SetField(ref _cropH, h)) return;
            if (_cropY > CropYMax)
                CropY = CropYMax;
            NotifyCropPanLimits();
        }
    }

    private bool _blackKeyEnabled;
    public bool BlackKeyEnabled { get => _blackKeyEnabled; set => SetField(ref _blackKeyEnabled, value); }

    private float _blackKeyThreshold = 0.08f;
    public float BlackKeyThreshold
    {
        get => _blackKeyThreshold;
        set => SetField(ref _blackKeyThreshold, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>UI-only: layer is checked for multi-assign into a group. Not persisted.</summary>
    private bool _markedForGroup;
    public bool MarkedForGroup
    {
        get => _markedForGroup;
        set => SetField(ref _markedForGroup, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Set opacity and draw opacity immediately (no fade). Used by wall carousel.</summary>
    public void SetOpacityImmediate(float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        Opacity = opacity;
        float draw = Visible ? opacity : 0f;
        DrawOpacity = draw;
        _fade.Snap(draw);
    }

    /// <summary>Snap DrawOpacity to the current target (load / instant fade).</summary>
    public void SnapDrawOpacity()
    {
        DrawOpacity = TargetDrawOpacity;
        _fade.Snap(DrawOpacity);
    }

    /// <summary>Advance DrawOpacity toward target over <paramref name="durationSeconds"/>. Returns true if still animating.</summary>
    public bool TickFade(float dt, float durationSeconds)
    {
        float target = TargetDrawOpacity;
        if (!_fade.IsRunning && DrawOpacity == target)
            return false;
        DrawOpacity = _fade.Tick(DrawOpacity, target, dt, durationSeconds, out bool animating);
        return animating;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void NotifyCropSliderLimits()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropWMax)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropHMax)));
        NotifyCropPanLimits();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropW)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropH)));
    }

    private void NotifyCropPanLimits()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropXMax)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropYMax)));
    }

    private void ClampCropAfterNativeChange()
    {
        if (NativeWidth <= 0 || NativeHeight <= 0)
            return;

        if (_cropW > NativeWidth)
            _cropW = NativeWidth;
        if (_cropH > NativeHeight)
            _cropH = NativeHeight;

        int maxX = Math.Max(0, NativeWidth - DesiredCropW());
        int maxY = Math.Max(0, NativeHeight - DesiredCropH());
        if (_cropX > maxX)
            _cropX = maxX;
        if (_cropY > maxY)
            _cropY = maxY;
    }

    private int DesiredCropW()
    {
        int nw = Math.Max(NativeWidth, 0);
        if (nw <= 0) return 0;
        return _cropW <= 0 ? nw : Math.Clamp(_cropW, 1, nw);
    }

    private int DesiredCropH()
    {
        int nh = Math.Max(NativeHeight, 0);
        if (nh <= 0) return 0;
        return _cropH <= 0 ? nh : Math.Clamp(_cropH, 1, nh);
    }

    public Vector2 GetDrawSize()
    {
        var (srcW, srcH) = GetSourcePixelSize();
        if (srcW <= 0 || srcH <= 0)
            return Vector2.Zero;
        return new Vector2(srcW * Scale, srcH * Scale);
    }

    /// <summary>
    /// Crop rectangle in native pixels as a fixed W×H window.
    /// Position is clamped so the window stays on-image (W/H are not shrunk by X/Y).
    /// 0×0 stored size means the full frame.
    /// </summary>
    public (int X, int Y, int W, int H) GetClampedCrop()
    {
        int nw = Math.Max(NativeWidth, 0);
        int nh = Math.Max(NativeHeight, 0);
        if (nw <= 0 || nh <= 0)
            return (0, 0, 0, 0);

        int w = DesiredCropW();
        int h = DesiredCropH();
        int x = Math.Clamp(_cropX, 0, Math.Max(0, nw - w));
        int y = Math.Clamp(_cropY, 0, Math.Max(0, nh - h));
        return (x, y, w, h);
    }

    /// <summary>Pixel size of this layer's cropped region (placement and draw size).</summary>
    public (int Width, int Height) GetSourcePixelSize()
    {
        var (_, _, w, h) = GetClampedCrop();
        return (w, h);
    }

    /// <summary>Normalized UV rectangle for this layer's crop (0–1).</summary>
    public (float U0, float V0, float U1, float V1) GetCropUvRect()
    {
        if (NativeWidth <= 0 || NativeHeight <= 0)
            return (0f, 0f, 1f, 1f);

        var (x, y, w, h) = GetClampedCrop();
        float invW = 1f / NativeWidth;
        float invH = 1f / NativeHeight;
        return (x * invW, y * invH, (x + w) * invW, (y + h) * invH);
    }

    public Vector2 MapCropUv(float u, float v)
    {
        var (u0, v0, u1, v1) = GetCropUvRect();
        return new Vector2(u0 + u * (u1 - u0), v0 + v * (v1 - v0));
    }

    public bool HasCrop
    {
        get
        {
            if (NativeWidth <= 0 || NativeHeight <= 0)
                return false;
            var (x, y, w, h) = GetClampedCrop();
            return x != 0 || y != 0 || w != NativeWidth || h != NativeHeight;
        }
    }

    public void ResetCrop()
    {
        SetCrop(0, 0, Math.Max(NativeWidth, 0), Math.Max(NativeHeight, 0));
    }

    /// <summary>
    /// Apply crop in W/H-then-X/Y order so pan limits use the intended window size.
    /// Object initializers that set CropX before CropW would otherwise clamp X to 0.
    /// </summary>
    public void SetCrop(int x, int y, int w, int h)
    {
        int nw = Math.Max(NativeWidth, 0);
        int nh = Math.Max(NativeHeight, 0);
        if (nw <= 0 || nh <= 0)
        {
            _cropX = Math.Max(0, x);
            _cropY = Math.Max(0, y);
            _cropW = Math.Max(0, w);
            _cropH = Math.Max(0, h);
            NotifyCropFieldsChanged();
            return;
        }

        _cropW = w <= 0 ? nw : Math.Clamp(w, 1, nw);
        _cropH = h <= 0 ? nh : Math.Clamp(h, 1, nh);
        _cropX = Math.Clamp(x, 0, Math.Max(0, nw - DesiredCropW()));
        _cropY = Math.Clamp(y, 0, Math.Max(0, nh - DesiredCropH()));
        NotifyCropFieldsChanged();
    }

    private void NotifyCropFieldsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropW)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropH)));
        NotifyCropPanLimits();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CropY)));
    }

    /// <summary>
    /// Keep a pixel crop valid after the frame size changes.
    /// A full-frame crop follows the new size; a custom crop is clamped.
    /// </summary>
    public void SyncCropToNativeSize(int previousWidth, int previousHeight)
    {
        bool wasUnset = _cropW <= 0 || _cropH <= 0;
        bool wasFull = previousWidth > 0 && previousHeight > 0
                       && _cropX == 0 && _cropY == 0
                       && (wasUnset || (_cropW == previousWidth && _cropH == previousHeight));
        if (wasUnset || wasFull || NativeWidth <= 0 || NativeHeight <= 0)
        {
            ResetCrop();
            return;
        }

        var (x, y, w, h) = GetClampedCrop();
        SetCrop(x, y, w, h);
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

        var (srcW, srcH) = GetSourcePixelSize();
        if (srcW <= 0 || srcH <= 0)
        {
            Scale = 1f;
            X = zcx - targetW * 0.5f;
            Y = zcy - targetH * 0.5f;
            return;
        }

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
