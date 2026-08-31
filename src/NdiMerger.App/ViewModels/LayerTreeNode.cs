using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NdiMerger.Core.Models;

namespace NdiMerger.App.ViewModels;

/// <summary>
/// Tree node for the layers panel (group folder or layer row).
/// </summary>
public sealed partial class LayerTreeNode : ObservableObject
{
    public LayerTreeNode(LayerGroup group)
    {
        IsGroup = true;
        Group = group;
        Layer = null;
        Children = [];
        Name = group.Name;
        Visible = group.Visible;
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LayerGroup.Name))
                Name = group.Name;
            if (e.PropertyName == nameof(LayerGroup.Visible))
                Visible = group.Visible;
        };
    }

    public LayerTreeNode(CompositionLayer layer)
    {
        IsGroup = false;
        Group = null;
        Layer = layer;
        Children = [];
        Name = layer.Name;
        Visible = layer.Visible;
        Marked = layer.MarkedForGroup;
        layer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompositionLayer.Name))
                Name = layer.Name;
            if (e.PropertyName == nameof(CompositionLayer.Visible))
                Visible = layer.Visible;
            if (e.PropertyName == nameof(CompositionLayer.MarkedForGroup))
                Marked = layer.MarkedForGroup;
        };
    }

    public bool IsGroup { get; }
    public LayerGroup? Group { get; }
    public CompositionLayer? Layer { get; }
    public ObservableCollection<LayerTreeNode> Children { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _visible = true;
    [ObservableProperty] private bool _marked;

    partial void OnVisibleChanged(bool value)
    {
        if (IsGroup && Group is not null && Group.Visible != value)
            Group.Visible = value;
        else if (!IsGroup && Layer is not null && Layer.Visible != value)
            Layer.Visible = value;
    }

    partial void OnMarkedChanged(bool value)
    {
        if (IsGroup)
        {
            foreach (var child in Children)
                child.Marked = value;
            return;
        }

        if (Layer is not null && Layer.MarkedForGroup != value)
            Layer.MarkedForGroup = value;
    }

    public string DisplayLabel => IsGroup ? $"Group: {Name}" : Name;
}
