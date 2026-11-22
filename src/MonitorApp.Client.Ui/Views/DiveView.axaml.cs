using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(250);

    private DiveViewModel? model;
    private Rect? origin;
    private DispatcherTimer? zoom;

    public DiveView()
    {
        InitializeComponent();
        // Click is raised before the button's command runs, so the tile's bounds are known when the Dive moves.
        AddHandler(Button.ClickEvent, OnAnyClick, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>When true the page changes without animation; otherwise the shell's setting decides.</summary>
    public bool? ReducedMotion { get; set; }

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
        if (IsReducedMotion || Stage.Bounds.Width <= 0 || Stage.Bounds.Height <= 0)
        {
            return;
        }

        double startScale;
        RelativePoint center;
        if (e.Direction == DiveDirection.In && from is { } rect)
        {
            startScale = Math.Clamp(Math.Max(rect.Width / Stage.Bounds.Width, rect.Height / Stage.Bounds.Height), 0.15, 0.95);
            center = new RelativePoint(rect.Center.X / Stage.Bounds.Width, rect.Center.Y / Stage.Bounds.Height, RelativeUnit.Relative);
        }
        else
        {
            startScale = e.Direction == DiveDirection.In ? 0.92 : 1.06;
            center = RelativePoint.Center;
        }

        StartZoom(startScale, center);
    }

    /// <summary>
    /// Scale and fade from the start state to the page at rest over <see cref="ZoomDuration"/> (cubic ease-out), timed by wall clock on
    /// a frame timer, so the zoom is the same length however long the app sat idle before it.
    /// </summary>
    private void StartZoom(double startScale, RelativePoint center)
    {
        StopZoom();
        var elapsed = Stopwatch.StartNew();
        PageHost.RenderTransformOrigin = center;
        void Apply(double progress)
        {
            var eased = 1 - Math.Pow(1 - progress, 3);
            var scale = startScale + ((1 - startScale) * eased);
            PageHost.RenderTransform = progress >= 1 ? null : new ScaleTransform(scale, scale);
            PageHost.Opacity = 0.15 + (0.85 * eased);
        }

        Apply(0);
        zoom = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        zoom.Tick += (_, _) =>
        {
            var progress = Math.Min(1, elapsed.Elapsed / ZoomDuration);
            Apply(progress);
            if (progress >= 1)
            {
                StopZoom();
            }
        };
        zoom.Start();
    }

    private void StopZoom()
    {
        if (zoom is not null)
        {
            zoom.Stop();
            zoom = null;
            PageHost.RenderTransform = null;
            PageHost.Opacity = 1;
        }
    }
}
