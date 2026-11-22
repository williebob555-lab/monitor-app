using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// Minimal line chart over a <see cref="ChartSeries"/>. Each segment is drawn as its own polyline, so
/// unavailable readings appear as gaps. The values are also in the table and described by the chart note.
/// </summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<ChartSeries?> SeriesProperty =
        AvaloniaProperty.Register<LineChart, ChartSeries?>(nameof(Series));

    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush>(nameof(LineBrush), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush> AxisBrushProperty =
        AvaloniaProperty.Register<LineChart, IBrush>(nameof(AxisBrush), Brushes.Gray);

    static LineChart()
    {
        AffectsRender<LineChart>(SeriesProperty, LineBrushProperty, AxisBrushProperty);
    }

    public ChartSeries? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IBrush LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double left = 64, bottom = 22, top = 6, right = 8;
        var plot = new Rect(left, top, Math.Max(1, Bounds.Width - left - right), Math.Max(1, Bounds.Height - top - bottom));
        context.DrawRectangle(null, new Pen(AxisBrush, 1), plot);
        var series = Series;
        if (series is not { HasData: true })
        {
            return;
        }

        DrawLabel(context, series.MaxY.ToString("G5", CultureInfo.InvariantCulture), new Point(2, top));
        DrawLabel(context, series.MinY.ToString("G5", CultureInfo.InvariantCulture), new Point(2, plot.Bottom - 14));
        DrawLabel(context, Time(series.MinX), new Point(plot.Left, plot.Bottom + 4));
        var end = Time(series.MaxX);
        DrawLabel(context, end, new Point(plot.Right - end.Length * 7, plot.Bottom + 4));

        var linePen = new Pen(LineBrush, 2);
        foreach (var segment in series.Segments)
        {
            var mapped = segment.Select(point => Map(point, series, plot)).ToArray();
            if (mapped.Length == 1)
            {
                context.DrawEllipse(LineBrush, null, mapped[0], 3, 3);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(mapped[0], false);
                foreach (var point in mapped.Skip(1))
                {
                    g.LineTo(point);
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, linePen, geometry);
        }
    }

    private void DrawLabel(DrawingContext context, string text, Point origin)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, AxisBrush);
        context.DrawText(formatted, origin);
    }

    private static Point Map(ChartPoint point, ChartSeries series, Rect plot) => new(
        plot.Left + (point.X - series.MinX) / (series.MaxX - series.MinX) * plot.Width,
        plot.Bottom - (point.Y - series.MinY) / (series.MaxY - series.MinY) * plot.Height);

    private static string Time(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
}
