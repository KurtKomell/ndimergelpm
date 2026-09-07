using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NdiMerger.Core.Models;

/// <summary>
/// Named folder in the layers panel. Visibility and opacity gate all member layers.
/// </summary>
public sealed class LayerGroup : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();

    private string _name = "Group";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set => SetField(ref _visible, value);
    }

    private float _opacity = 1f;
    /// <summary>Target group opacity (0–1). Persisted; animated via <see cref="DrawOpacity"/>.</summary>
    public float Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, Math.Clamp(value, 0f, 1f));
    }

    /// <summary>Runtime animated opacity toward <see cref="TargetDrawOpacity"/>. Not persisted.</summary>
    public float DrawOpacity { get; set; } = 1f;

    private FadeAnimator _fade;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public int SortOrder { get; set; }

    /// <summary>Target draw opacity for fade animation (Visible off → 0).</summary>
    public float TargetDrawOpacity => Visible ? Opacity : 0f;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetOpacityImmediate(float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        Opacity = opacity;
        float draw = Visible ? opacity : 0f;
        DrawOpacity = draw;
        _fade.Snap(draw);
    }

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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
