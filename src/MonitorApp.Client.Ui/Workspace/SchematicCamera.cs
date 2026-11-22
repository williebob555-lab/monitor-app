using Avalonia;

namespace MonitorApp.Client.Ui.Workspace;

/// <summary>World-to-screen mapping with pan, zoom about a point, fit-to-content and eased animation.</summary>
public sealed class SchematicCamera
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 3.0;

    private Point? targetCenter;
    private double targetZoom;

    /// <summary>World point shown at the middle of the viewport.</summary>
    public Point Center { get; private set; }

    public double Zoom { get; private set; } = 1;

    public Size Viewport { get; set; } = new(800, 600);

    /// <summary>Pixels per world unit at zoom 1.</summary>
    public double Unit { get; set; } = 100;

    /// <summary>Incremented on every change so callers can cache derived geometry.</summary>
    public int Version { get; private set; }

    public double Scale => Unit * Zoom;

    public bool IsAnimating => targetCenter is not null;

    public Point ToScreen(Point world) =>
        new((Viewport.Width / 2) + ((world.X - Center.X) * Scale), (Viewport.Height / 2) + ((world.Y - Center.Y) * Scale));

    public Point ToWorld(Point screen) =>
        new(Center.X + ((screen.X - (Viewport.Width / 2)) / Scale), Center.Y + ((screen.Y - (Viewport.Height / 2)) / Scale));

    public void Jump(Point center, double zoom)
    {
        targetCenter = null;
        Center = center;
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Version++;
    }

    public void Pan(Vector screenDelta)
    {
        targetCenter = null;
        Center = new Point(Center.X - (screenDelta.X / Scale), Center.Y - (screenDelta.Y / Scale));
        Version++;
    }

    /// <summary>Zooms so the world point under <paramref name="screen"/> stays under it.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        targetCenter = null;
        var anchor = ToWorld(screen);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        Center = new Point(
            anchor.X - ((screen.X - (Viewport.Width / 2)) / Scale),
            anchor.Y - ((screen.Y - (Viewport.Height / 2)) / Scale));
        Version++;
    }

    /// <summary>The centre and zoom that show <paramref name="world"/> with <paddingPx> around it.</summary>
    public (Point Center, double Zoom) FitTarget(Rect world, double paddingPx)
    {
        var w = Math.Max(0.01, world.Width) * Unit;
        var h = Math.Max(0.01, world.Height) * Unit;
        var zoom = Math.Min(
            Math.Max(10, Viewport.Width - (2 * paddingPx)) / w,
            Math.Max(10, Viewport.Height - (2 * paddingPx)) / h);
        return (world.Center, Math.Clamp(zoom, MinZoom, MaxZoom));
    }

    public void Fit(Rect world, double paddingPx, bool animated)
    {
        var (c, z) = FitTarget(world, paddingPx);
        if (animated)
        {
            AnimateTo(c, z);
        }
        else
        {
            Jump(c, z);
        }
    }

    public void AnimateTo(Point center, double zoom)
    {
        targetCenter = center;
        targetZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Version++;
    }

    /// <summary>Eases toward the animation target. Returns true while still moving.</summary>
    public bool Step(double seconds, bool reducedMotion)
    {
        if (targetCenter is not { } tc)
        {
            return false;
        }

        var k = reducedMotion ? 1 : 1 - Math.Exp(-Math.Max(0, seconds) * 8);
        Center = new Point(Center.X + ((tc.X - Center.X) * k), Center.Y + ((tc.Y - Center.Y) * k));
        Zoom += (targetZoom - Zoom) * k;
        Version++;
        if (Math.Abs(tc.X - Center.X) * Scale < 0.3 && Math.Abs(tc.Y - Center.Y) * Scale < 0.3 && Math.Abs(targetZoom - Zoom) < 0.002)
        {
            Center = tc;
            Zoom = targetZoom;
            targetCenter = null;
        }

        return targetCenter is not null;
    }
}

/// <summary>Semantic zoom levels (UI spec 6).</summary>
public enum SemanticLevel
{
    /// <summary>Header only.</summary>
    Overview,

    /// <summary>Picture and two key facts.</summary>
    Ecosystem,

    /// <summary>More facts, port and link labels.</summary>
    Device,
}

public static class SemanticZoom
{
    public static SemanticLevel For(double zoom) => zoom < 0.6 ? SemanticLevel.Overview : zoom < 1.4 ? SemanticLevel.Ecosystem : SemanticLevel.Device;

    public static int FactCount(SemanticLevel level) => level switch
    {
        SemanticLevel.Overview => 0,
        SemanticLevel.Ecosystem => 2,
        _ => 4,
    };
}
