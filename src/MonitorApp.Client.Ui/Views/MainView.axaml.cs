using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The whole client UI for any head: side navigation on wide windows, bottom bar plus menu overlay on narrow ones.
/// </summary>
public sealed partial class MainView : UserControl
{
    /// <summary>Widths below this use the compact (phone) layout.</summary>
    public const double CompactBelowWidth = 720;

    public MainView() => InitializeComponent();

    /// <summary>True while the compact layout is active.</summary>
    public bool IsCompact { get; private set; }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyLayout(Bounds.Width);
    }

    private void ApplyLayout(double width)
    {
        // Zero means "not measured yet"; keep the wide default until there is a real width.
        var compact = width > 0 && width < CompactBelowWidth;
        IsCompact = compact;
        Split.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Split.IsPaneOpen = !compact;
        MenuButton.IsVisible = compact;
        BottomBar.IsVisible = compact;
    }

    private void OnMenuClick(object? sender, RoutedEventArgs e) => Split.IsPaneOpen = !Split.IsPaneOpen;

    /// <summary>Tapping the entry that is already selected raises no selection change, but should still close the overlay.</summary>
    private void OnNavTapped(object? sender, TappedEventArgs e)
    {
        if (IsCompact)
        {
            Split.IsPaneOpen = false;
        }
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel model
            && e.AddedItems.Count == 1
            && e.AddedItems[0] is SectionViewModel section
            && !ReferenceEquals(model.SelectedSection, section))
        {
            model.SelectedSection = section;
        }

        if (IsCompact)
        {
            Split.IsPaneOpen = false;
        }
    }
}
