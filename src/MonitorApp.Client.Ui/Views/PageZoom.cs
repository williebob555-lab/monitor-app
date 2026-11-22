using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The Dive's page transition, shared by the Dive and Alerts: diving in zooms the new page out of the tapped element's bounds (scale and
/// fade, about 250 ms, cubic ease-out); climbing out settles the parent page back from slightly enlarged. Timed by the wall clock on a frame
/// timer, so it is the same length however long the app sat idle; <see cref="Complete"/> ends it at rest (tests call it).
/// </summary>
internal sealed class PageZoom(Control host)
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? timer;

    public bool IsZooming => timer is not null;

    public int Started { get; private set; }

    /// <summary>Plays the transition for a navigation inside <paramref name="stage"/>; <paramref name="from"/> is the tapped bounds in stage coordinates.</summary>
    public void Play(DiveDirection direction, Rect? from, Size stage)
    {
        if (stage.Width <= 0 || stage.Height <= 0)
        {
            return;
        }

        double startScale;
        RelativePoint center;
        if (direction == DiveDirection.In && from is { } rect)
        {
            startScale = Math.Clamp(Math.Max(rect.Width / stage.Width, rect.Height / stage.Height), 0.15, 0.95);
            center = new RelativePoint(rect.Center.X / stage.Width, rect.Center.Y / stage.Height, RelativeUnit.Relative);
        }
        else
        {
            startScale = direction == DiveDirection.In ? 0.92 : 1.06;
            center = RelativePoint.Center;
        }

        Start(startScale, center);
    }

    public void Complete()
    {
        if (timer is not null)
        {
            timer.Stop();
            timer = null;
            host.RenderTransform = null;
            host.Opacity = 1;
        }
    }

    private void Start(double startScale, RelativePoint center)
    {
        Complete();
        Started++;
        var elapsed = Stopwatch.StartNew();
        host.RenderTransformOrigin = center;
        void Apply(double progress)
        {
            var eased = 1 - Math.Pow(1 - progress, 3);
            var scale = startScale + ((1 - startScale) * eased);
            host.RenderTransform = progress >= 1 ? null : new ScaleTransform(scale, scale);
            host.Opacity = 0.15 + (0.85 * eased);
        }

        Apply(0);
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, elapsed.Elapsed / Duration);
            Apply(progress);
            if (progress >= 1)
            {
                Complete();
            }
        };
        timer.Start();
    }
}
