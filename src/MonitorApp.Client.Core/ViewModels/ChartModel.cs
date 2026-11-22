namespace MonitorApp.Client.Core.ViewModels;

public readonly record struct ChartPoint(double X, double Y);

/// <summary>
/// Plot-ready numeric series. X is Unix seconds. Unavailable or non-numeric readings are not plotted as values;
/// they split the line into separate segments so a gap is visible instead of an interpolated or zero value.
/// </summary>
public sealed record ChartSeries(
    IReadOnlyList<IReadOnlyList<ChartPoint>> Segments,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    int PlottedCount,
    int GapCount)
{
    public static ChartSeries Empty { get; } = new([], 0, 1, 0, 1, 0, 0);

    public bool HasData => PlottedCount > 0;

    public static ChartSeries Build(IEnumerable<HistoryPointDto> points)
    {
        var segments = new List<IReadOnlyList<ChartPoint>>();
        var current = new List<ChartPoint>();
        var plotted = 0;
        var gaps = 0;
        foreach (var point in points.OrderBy(point => point.ObservedAt))
        {
            if (ValueFormatter.HasValue(point) && point.Value is { Kind: "number", Number: { } number } && double.IsFinite(number))
            {
                current.Add(new ChartPoint(point.ObservedAt.ToUnixTimeMilliseconds() / 1000.0, number));
                plotted++;
                continue;
            }

            gaps++;
            if (current.Count > 0)
            {
                segments.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        if (plotted == 0)
        {
            return Empty with { GapCount = gaps };
        }

        var all = segments.SelectMany(segment => segment).ToArray();
        var minY = all.Min(point => point.Y);
        var maxY = all.Max(point => point.Y);
        if (minY == maxY)
        {
            minY -= 1;
            maxY += 1;
        }

        var minX = all.Min(point => point.X);
        var maxX = all.Max(point => point.X);
        if (minX == maxX)
        {
            maxX = minX + 1;
        }

        return new ChartSeries(segments, minX, maxX, minY, maxY, plotted, gaps);
    }
}
