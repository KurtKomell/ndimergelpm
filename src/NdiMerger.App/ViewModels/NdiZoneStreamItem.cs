using CommunityToolkit.Mvvm.ComponentModel;
using NdiMerger.Output;

namespace NdiMerger.App.ViewModels;

/// <summary>
/// Checkbox row for enabling a per-zone NDI crop stream.
/// </summary>
public sealed partial class NdiZoneStreamItem : ObservableObject
{
    private readonly Action<NdiZoneStreamItem, bool> _onEnabledChanged;

    public NdiZoneStream Stream { get; }
    public string ZoneId => Stream.ZoneId;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _enabled;

    public NdiZoneStreamItem(NdiZoneStream stream, Action<NdiZoneStreamItem, bool> onEnabledChanged)
    {
        Stream = stream;
        _onEnabledChanged = onEnabledChanged;
        _label = stream.CheckboxLabel;
    }

    public void RefreshLabel() => Label = Stream.CheckboxLabel;

    partial void OnEnabledChanged(bool value) => _onEnabledChanged(this, value);
}
