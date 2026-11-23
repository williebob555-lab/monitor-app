using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
        // Commit when a drag ends, a key is released, or the track is clicked; handled events included (the slider handles its own).
        Scrubber.AddHandler(Thumb.DragCompletedEvent, OnScrubEnded, RoutingStrategies.Bubble, handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, OnScrubEnded, RoutingStrategies.Bubble, handledEventsToo: true);
        Scrubber.AddHandler(KeyUpEvent, OnScrubKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnMarkerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TimelineViewModel model && (sender as Control)?.DataContext is TimelineMarker marker)
        {
            _ = model.JumpToAsync(marker.Event);
        }
    }

    private void OnScrubKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            OnScrubEnded(sender, e);
        }
    }

    private void OnScrubEnded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TimelineViewModel model && model.CommitCommand.CanExecute(null))
        {
            _ = model.CommitCommand.ExecuteAsync(null);
        }
    }
}
