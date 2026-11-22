using Avalonia.Controls;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// The environment as an accessible structured tree: every object with its health in words, nested under its host.
/// This is the keyboard and screen-reader alternative to the schematic and must stay available beside it.
/// </summary>
public sealed partial class OutlineTree : UserControl
{
    private EnvironmentViewModel? model;
    private bool syncing;

    public OutlineTree()
    {
        InitializeComponent();
        Tree.SelectionChanged += OnSelectionChanged;
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (model is not null)
        {
            model.PropertyChanged -= OnModelChanged;
            model.Outline.CollectionChanged -= OnOutlineChanged;
        }

        model = DataContext as EnvironmentViewModel;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
            model.Outline.CollectionChanged += OnOutlineChanged;
            Mirror();
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || model is null || e.AddedItems.Count != 1 || e.AddedItems[0] is not OutlineItem item)
        {
            return;
        }

        model.SelectedNodeId = item.NodeId;
    }

    private void OnOutlineChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Mirror();

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvironmentViewModel.SelectedNodeId) or nameof(EnvironmentViewModel.Scene))
        {
            Mirror();
        }
    }

    /// <summary>Makes the tree's selection follow the model (search jumps, inspector links, breadcrumbs).</summary>
    private void Mirror()
    {
        if (model is null)
        {
            return;
        }

        syncing = true;
        try
        {
            Tree.SelectedItem = model.SelectedNodeId is { } id ? Find(model.Outline, id) : null;
        }
        finally
        {
            syncing = false;
        }
    }

    private static OutlineItem? Find(IEnumerable<OutlineItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.NodeId == id)
            {
                return item;
            }

            if (Find(item.Children, id) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }
}
