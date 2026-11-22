using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The Dive's view (DataContext: <see cref="DiveViewModel"/>). All navigation logic lives in the view model; this only animates it:
/// diving in zooms the new page out of the tapped tile's bounds (scale and fade, about 250 ms), climbing out settles the parent page
/// back from slightly enlarged. Reduced motion (<see cref="WorkspaceShell.ReducedMotion"/>) switches the animation off.
/// </summary>
public sealed partial class DiveView : UserControl
{
    private readonly PageZoom zoom;
    private DiveViewModel? model;
    private Rect? origin;

    public DiveView()
    {
        InitializeComponent();
        zoom = new PageZoom(PageHost);
        // Click is raised before the button's command runs, so the tile's bounds are known when the Dive moves.
        AddHandler(Button.ClickEvent, OnAnyClick, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>When true the page changes without animation; otherwise the shell's setting decides.</summary>
    public bool? ReducedMotion { get; set; }

    /// <summary>True while a zoom is running.</summary>
    public bool IsZooming => zoom.IsZooming;

    /// <summary>How many zooms have started since this view was created (tests check that a tap animated).</summary>
    public int ZoomsStarted => zoom.Started;

    /// <summary>
    /// Ends a running zoom at once, at rest (full size, full opacity). The zoom is timed by the wall clock, so tests call this instead of
    /// waiting for frames, which a slow machine may not deliver within any fixed time.
    /// </summary>
    public void CompleteAnimations() => StopZoom();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null)
        {
            model.Navigated -= OnNavigated;
        }

        model = DataContext as DiveViewModel;
        if (model is not null && this.GetVisualRoot() is not null)
        {
            model.Navigated += OnNavigated;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (model is not null)
        {
            model.Navigated -= OnNavigated;
            model.Navigated += OnNavigated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopZoom();
        if (model is not null)
        {
            model.Navigated -= OnNavigated;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private bool IsReducedMotion => ReducedMotion ?? this.FindAncestorOfType<WorkspaceShell>()?.ReducedMotion ?? false;

    private void OnAnyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { } button && button.Classes.Contains("tile") && button.TranslatePoint(default, Stage) is { } topLeft)
        {
            origin = new Rect(topLeft, button.Bounds.Size);
        }
        else
        {
            origin = null;
        }
    }

    private void OnNavigated(object? sender, DiveNavigatedEventArgs e)
    {
        var from = origin;
        origin = null;
        if (!IsReducedMotion)
        {
            zoom.Play(e.Direction, from, Stage.Bounds.Size);
        }
    }

    private void StopZoom() => zoom.Complete();
}
