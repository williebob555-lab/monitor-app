using Avalonia;
using Avalonia.Controls;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// The Dive's responsive tile grid: up to <see cref="MaxColumns"/> columns when wide (4), 2 at medium widths, 1 on a phone.
/// Tiles in a row share the row's height; a tile whose data is a <see cref="DiveTile"/> with <see cref="DiveTile.SpansRow"/> takes a whole row.
/// </summary>
public sealed class DiveTileGrid : Panel
{
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<DiveTileGrid, int>(nameof(MaxColumns), 4);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<DiveTileGrid, double>(nameof(Gap), 16);

    /// <summary>Below this width per column the grid drops to fewer columns.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<DiveTileGrid, double>(nameof(MinColumnWidth), 240);

    static DiveTileGrid()
    {
        AffectsMeasure<DiveTileGrid>(MaxColumnsProperty, GapProperty, MinColumnWidthProperty);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The column count last used for layout (4, 2 or 1); tests read it.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>4 (or the maximum) when every column gets at least the minimum width, else 2, else 1.</summary>
    public static int ColumnsFor(double width, int maxColumns, double minColumnWidth, double gap)
    {
        foreach (var candidate in new[] { maxColumns, 2, 1 })
        {
            if (candidate <= maxColumns && (candidate == 1 || (width - (gap * (candidate - 1))) / candidate >= minColumnWidth))
            {
                return Math.Max(1, candidate);
            }
        }

        return 1;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinColumnWidth * MaxColumns;
        Columns = ColumnsFor(width, MaxColumns, MinColumnWidth, Gap);
        var height = 0.0;
        foreach (var row in Rows())
        {
            var rowHeight = 0.0;
            foreach (var (child, _, span) in row)
            {
                child.Measure(new Size(SpanWidth(width, span), double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            height += rowHeight + (height > 0 ? Gap : 0);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var y = 0.0;
        foreach (var row in Rows())
        {
            var rowHeight = row.Max(c => c.Child.DesiredSize.Height);
            foreach (var (child, column, span) in row)
            {
                var x = column * (ColumnWidth(width) + Gap);
                child.Arrange(new Rect(x, y, SpanWidth(width, span), rowHeight));
            }

            y += rowHeight + Gap;
        }

        return finalSize;
    }

    private double ColumnWidth(double width) => Math.Max(0, (width - (Gap * (Columns - 1))) / Columns);

    private double SpanWidth(double width, int span) => (ColumnWidth(width) * span) + (Gap * (span - 1));

    private List<List<(Control Child, int Column, int Span)>> Rows()
    {
        var rows = new List<List<(Control, int, int)>>();
        var current = new List<(Control, int, int)>();
        var column = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            var span = child.DataContext is DiveTile { SpansRow: true } ? Columns : 1;
            if (column + span > Columns && current.Count > 0)
            {
                rows.Add(current);
                current = [];
                column = 0;
            }

            current.Add((child, column, span));
            column += span;
            if (column >= Columns)
            {
                rows.Add(current);
                current = [];
                column = 0;
            }
        }

        if (current.Count > 0)
        {
            rows.Add(current);
        }

        return rows;
    }
}
