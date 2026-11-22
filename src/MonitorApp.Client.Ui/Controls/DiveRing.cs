using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>A ring gauge: a thin track with an arc from twelve o'clock for <see cref="Value"/> (0..1). The number sits on top in markup.</summary>
public sealed class DiveRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DiveRing, double>(nameof(Value));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DiveRing, double>(nameof(Thickness), 8);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<DiveRing, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<DiveRing, IBrush?>(nameof(Track));

    static DiveRing()
    {
        AffectsRender<DiveRing>(ValueProperty, ThicknessProperty, StrokeProperty, TrackProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (size - Thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Track is { } track)
        {
            context.DrawEllipse(null, new Pen(track, Thickness), center, radius, radius);
        }

        var value = Math.Clamp(double.IsFinite(Value) ? Value : 0, 0, 1);
        if (Stroke is not { } stroke || value <= 0)
        {
            return;
        }

        var pen = new Pen(stroke, Thickness, lineCap: PenLineCap.Round);
        if (value >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
