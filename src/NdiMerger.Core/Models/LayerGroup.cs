using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NdiMerger.Core.Models;

/// <summary>
/// Named folder in the layers panel. Visibility gates all member layers.
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

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public int SortOrder { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
