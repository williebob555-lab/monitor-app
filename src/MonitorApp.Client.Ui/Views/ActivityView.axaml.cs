using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The History mode's view (DataContext: <see cref="ActivityViewModel"/>). Animates moves between the story, an event page and a history tool
/// with the Dive's zoom (<see cref="PageZoom"/>); all logic lives in the view model.
/// </summary>
public sealed partial class ActivityView : UserControl
{
    private readonly PageZoom zoom;
    private ActivityViewModel? model;
    private Rect? origin;

    public ActivityView()
    {
        InitializeComponent();
        zoom = new PageZoom(PageHost);
        AddHandler(Button.ClickEvent, OnAnyClick, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>When true the page changes without animation; otherwise the shell's setting decides.</summary>
    public bool? ReducedMotion { get; set; }

    public bool IsZooming => zoom.IsZooming;

    /// <summary>How many zooms have started since this view was created (tests check that a tap animated).</summary>
    public int ZoomsStarted => zoom.Started;

    /// <summary>Ends a running zoom at once, at rest (as <see cref="DiveView.CompleteAnimations"/>).</summary>
    public void CompleteAnimations() => zoom.Complete();

    private bool IsReducedMotion => ReducedMotion ?? this.FindAncestorOfType<WorkspaceShell>()?.ReducedMotion ?? false;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unhook();
        model = DataContext as ActivityViewModel;
        if (this.GetVisualRoot() is not null)
        {
            Hook();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        zoom.Complete();
        Unhook();
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook()
    {
        if (model is not null)
        {
            model.Navigated -= OnNavigated;
            model.Navigated += OnNavigated;
        }
    }

    private void Unhook()
    {
        if (model is not null)
        {
            model.Navigated -= OnNavigated;
        }
    }

    private void OnAnyClick(object? sender, RoutedEventArgs e) =>
        origin = e.Source is Button { } button && button.Classes.Contains("tile") && button.TranslatePoint(default, Stage) is { } topLeft
            ? new Rect(topLeft, button.Bounds.Size)
            : null;

    private void OnNavigated(object? sender, DiveNavigatedEventArgs e)
    {
        var from = origin;
        origin = null;
        if (!IsReducedMotion)
        {
            zoom.Play(e.Direction, from, Stage.Bounds.Size);
        }
    }
}
