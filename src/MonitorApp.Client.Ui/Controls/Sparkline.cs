using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// A small line of recent values (0..1 heights in time order, oldest left), with a faint area under it and a dot on the latest value.
/// Draws nothing with fewer than two points: a trend is never implied from one reading. Brushes come from the semantic tokens.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 1.5);

    static Sparkline()
    {
        AffectsRender<Sparkline>(PointsProperty, StrokeProperty, StrokeThicknessProperty);
    }

    public IReadOnlyList<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var points = Points;
        if (points is not { Count: >= 2 } || Stroke is not { } stroke || Bounds.Width <= 4 || Bounds.Height <= 4)
        {
            return;
        }

        var inset = Math.Max(StrokeThickness, 2);
        var width = Bounds.Width - (inset * 2);
        var height = Bounds.Height - (inset * 2);
        Point At(int i) => new(
            inset + (width * i / (points.Count - 1)),
            inset + (height * (1 - Math.Clamp(double.IsFinite(points[i]) ? points[i] : 0, 0, 1))));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (var i = 1; i < points.Count; i++)
            {
                g.LineTo(At(i));
            }

            g.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(inset, inset + height), true);
            for (var i = 0; i < points.Count; i++)
            {
                g.LineTo(At(i));
            }

            g.LineTo(new Point(inset + width, inset + height));
            g.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(stroke is ISolidColorBrush solid ? solid.Color : Colors.Gray, 0.14), null, area);
        context.DrawGeometry(null, new Pen(stroke, StrokeThickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), line);
        var last = At(points.Count - 1);
        context.DrawEllipse(stroke, null, last, StrokeThickness + 1, StrokeThickness + 1);
    }
}
