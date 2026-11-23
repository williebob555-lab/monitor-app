using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class ThingsView : UserControl
{
    /// <summary>Moves shorter than this (in the drawing's own units) are taps, not drags.</summary>
    private const double DragThreshold = 3;

    private Control? dragged;
    private Visual? dragSurface;
    private Point dragStart;

    public ThingsView() => InitializeComponent();

    /// <summary>Starts dragging a pedal on a board drawing, or a piece of equipment in a rack drawing.</summary>
    private void OnPlacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || DataContext is not ThingsViewModel { CanChange: true }
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
            || control.DataContext is ThingRackRow { IsEmpty: true })
        {
            return;
        }

        dragSurface = Surface(control);
        if (dragSurface is null)
        {
            return;
        }

        dragged = control;
        dragStart = e.GetPosition(dragSurface);
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnPlaceMoved(object? sender, PointerEventArgs e)
    {
        if (sender != dragged || dragged is null || dragSurface is null)
        {
            return;
        }

        var delta = e.GetPosition(dragSurface) - dragStart;
        dragged.RenderTransform = dragged.DataContext is ThingRackRow
            ? new TranslateTransform(0, delta.Y)
            : new TranslateTransform(delta.X, delta.Y);
    }

    private async void OnPlaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender != dragged || dragged is not { } control || dragSurface is null)
        {
            return;
        }

        var delta = e.GetPosition(dragSurface) - dragStart;
        control.RenderTransform = null;
        e.Pointer.Capture(null);
        dragged = null;
        if ((Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) || DataContext is not ThingsViewModel model)
        {
            return;
        }

        var card = control.FindAncestorOfType<ItemsControl>()?.DataContext as ThingCard;
        switch (control.DataContext)
        {
            case ThingBoardItem item when card?.Board is { } board:
                await model.MoveOnBoardAsync(board, item, item.Left + delta.X, item.Top + delta.Y);
                break;
            case ThingRackRow row:
                // Up the screen is up the rack.
                await model.MoveInRackAsync(row, (int)Math.Round(-delta.Y / ThingRackRow.UnitHeight));
                break;
        }
    }

    /// <summary>What drag distances are measured against: a board's canvas (mm), or a rack's list of units (pixels).</summary>
    private static Visual? Surface(Control control) =>
        control.DataContext is ThingBoardItem ? control.FindAncestorOfType<Canvas>() : control.FindAncestorOfType<ItemsControl>();
}
