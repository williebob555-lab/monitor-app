using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// "Open the machine": the first child is the device body, drawn in the middle; every other child is one of its parts, pulled
/// out to the left and right like an exploded technical drawing and joined to the body by a leader line. When the panel appears,
/// the parts slide out from the body (<see cref="Explode"/> animates from 0 to 1). On a narrow screen the parts stack under the
/// body instead, joined by a spine on the left.
/// </summary>
public sealed class ExplodedPanel : Panel
{
    public static readonly StyledProperty<double> ExplodeProperty =
        AvaloniaProperty.Register<ExplodedPanel, double>(nameof(Explode));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<ExplodedPanel, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<ExplodedPanel, double>(nameof(Gap), 16);

    /// <summary>Below this width the parts stack under the body.</summary>
    public const double StackBelowWidth = 720;

    private const double BodyMaxWidth = 340;
    private const double PartMaxWidth = 300;
    private const double LineGap = 48;

    static ExplodedPanel()
    {
        AffectsArrange<ExplodedPanel>(ExplodeProperty);
        ExplodeProperty.Changed.AddClassHandler<ExplodedPanel>((panel, _) => panel.lines.InvalidateVisual());
        LineBrushProperty.Changed.AddClassHandler<ExplodedPanel>((panel, _) => panel.lines.InvalidateVisual());
        AffectsMeasure<ExplodedPanel>(GapProperty);
    }

    private readonly LineLayer lines;

    public ExplodedPanel()
    {
        // A visual-only child under the parts (not in Children, so layout ignores it); Panel.Render is sealed.
        lines = new LineLayer(this) { ZIndex = -1, IsHitTestVisible = false };
        VisualChildren.Add(lines);
    }

    /// <summary>0: every part sits on the body; 1: fully pulled apart.</summary>
    public double Explode
    {
        get => GetValue(ExplodeProperty);
        set => SetValue(ExplodeProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private readonly List<Rect> finalRects = [];
    private double partWidth = PartMaxWidth;
    private bool stacked;

    /// <summary>How long the parts take to slide out.</summary>
    public static TimeSpan OpenDuration { get; } = TimeSpan.FromMilliseconds(650);

    private readonly CubicEaseOut easing = new();
    private long openedAt;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Start closed and open frame by frame (frames, not a timer, so it follows the display and stops when hidden).
        SetCurrentValue(ExplodeProperty, 0d);
        openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            SetCurrentValue(ExplodeProperty, 1d);
            return;
        }

        top.RequestAnimationFrame(_ =>
        {
            var progress = System.Diagnostics.Stopwatch.GetElapsedTime(openedAt) / OpenDuration;
            SetCurrentValue(ExplodeProperty, progress >= 1 ? 1d : easing.Ease(progress));
            if (progress < 1 && this.IsAttachedToVisualTree())
            {
                RequestFrame();
            }
        });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        finalRects.Clear();
        var width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        lines.Measure(new Size(width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height));
        if (Children.Count == 0)
        {
            return default;
        }

        stacked = width < StackBelowWidth;
        var body = Children[0];
        var parts = Children.Skip(1).ToList();
        if (stacked)
        {
            var inner = Math.Max(0, width - LineGap / 2);
            body.Measure(new Size(width, double.PositiveInfinity));
            var y = body.DesiredSize.Height + Gap;
            foreach (var part in parts)
            {
                part.Measure(new Size(inner, double.PositiveInfinity));
                y += part.DesiredSize.Height + Gap;
            }

            return new Size(width, Math.Max(0, y - Gap));
        }

        var bodyWidth = Math.Min(BodyMaxWidth, width * 0.36);
        partWidth = Math.Min(PartMaxWidth, (width - bodyWidth - 2 * LineGap) / 2);
        body.Measure(new Size(bodyWidth, double.PositiveInfinity));
        foreach (var part in parts)
        {
            part.Measure(new Size(partWidth, double.PositiveInfinity));
        }

        var (left, right) = Split(parts);
        var height = Math.Max(body.DesiredSize.Height, Math.Max(ColumnHeight(left), ColumnHeight(right)));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        lines.Arrange(new Rect(finalSize));
        lines.InvalidateVisual();
        finalRects.Clear();
        if (Children.Count == 0)
        {
            return finalSize;
        }

        var t = Math.Clamp(Explode, 0, 1);
        var body = Children[0];
        // The body stays on top while the parts are still tucked behind it.
        body.ZIndex = 1;
        var parts = Children.Skip(1).ToList();
        if (stacked)
        {
            var bodyRect = new Rect(0, 0, finalSize.Width, body.DesiredSize.Height);
            body.Arrange(bodyRect);
            finalRects.Add(bodyRect);
            var y = bodyRect.Bottom + Gap;
            foreach (var part in parts)
            {
                var target = new Rect(LineGap / 2, y, Math.Max(0, finalSize.Width - LineGap / 2), part.DesiredSize.Height);
                var from = new Rect(target.X, bodyRect.Bottom - part.DesiredSize.Height, target.Width, target.Height);
                Place(part, from, target, t);
                y += part.DesiredSize.Height + Gap;
            }

            return finalSize;
        }

        var bodySize = body.DesiredSize;
        var centre = new Rect((finalSize.Width - bodySize.Width) / 2, (finalSize.Height - bodySize.Height) / 2, bodySize.Width, bodySize.Height);
        body.Arrange(centre);
        finalRects.Add(centre);
        var (left, right) = Split(parts);
        ArrangeColumn(left, 0, finalSize.Height, centre, t);
        ArrangeColumn(right, finalSize.Width, finalSize.Height, centre, t);
        return finalSize;
    }

    /// <summary>Draws the leader lines (called by the line layer, which sits under the parts).</summary>
    private void DrawLines(DrawingContext context)
    {
        if (finalRects.Count < 2 || LineBrush is not { } brush)
        {
            return;
        }

        var pen = new Pen(brush, 1.25, dashStyle: new DashStyle([4, 3], 0));
        var body = finalRects[0];
        var opacity = Math.Clamp(Explode, 0, 1);
        using var fade = context.PushOpacity(opacity);
        foreach (var part in finalRects.Skip(1))
        {
            Point start;
            Point end;
            if (stacked)
            {
                var spineX = LineGap / 4;
                start = new Point(spineX, body.Bottom);
                end = new Point(part.X, part.Y + Math.Min(22, part.Height / 2));
                context.DrawLine(pen, start, new Point(spineX, end.Y));
                context.DrawLine(pen, new Point(spineX, end.Y), end);
            }
            else
            {
                var onLeft = part.Center.X < body.Center.X;
                start = new Point(onLeft ? body.X : body.Right, Math.Clamp(part.Center.Y, body.Y + 12, body.Bottom - 12));
                end = new Point(onLeft ? part.Right : part.X, part.Y + Math.Min(22, part.Height / 2));
                var elbow = new Point((start.X + end.X) / 2, end.Y);
                context.DrawLine(pen, start, new Point(elbow.X, start.Y));
                context.DrawLine(pen, new Point(elbow.X, start.Y), elbow);
                context.DrawLine(pen, elbow, end);
            }

            context.DrawEllipse(brush, null, start, 2.5, 2.5);
            context.DrawEllipse(brush, null, end, 2.5, 2.5);
        }
    }

    private void ArrangeColumn(List<Control> column, double edgeX, double height, Rect body, double t)
    {
        var total = ColumnHeight(column);
        var y = (height - total) / 2;
        foreach (var part in column)
        {
            // Every part in a column gets the same width, so the drawing reads as two clean rows of parts.
            var size = new Size(partWidth, part.DesiredSize.Height);
            var x = edgeX == 0 ? 0 : edgeX - size.Width;
            var target = new Rect(x, y, size.Width, size.Height);
            var from = new Rect(body.Center.X - size.Width / 2, body.Center.Y - size.Height / 2, size.Width, size.Height);
            Place(part, from, target, t);
            y += size.Height + Gap;
        }
    }

    private void Place(Control part, Rect from, Rect target, double t)
    {
        var rect = new Rect(
            from.X + (target.X - from.X) * t,
            from.Y + (target.Y - from.Y) * t,
            target.Width,
            target.Height);
        part.Arrange(rect);
        part.Opacity = 0.15 + 0.85 * t;
        finalRects.Add(rect);
    }

    private double ColumnHeight(List<Control> column) =>
        column.Count == 0 ? 0 : column.Sum(c => c.DesiredSize.Height) + Gap * (column.Count - 1);

    /// <summary>Alternates parts left and right so the first ones (processor, memory) sit near the top on both sides.</summary>
    private static (List<Control> Left, List<Control> Right) Split(List<Control> parts)
    {
        var left = new List<Control>();
        var right = new List<Control>();
        for (var i = 0; i < parts.Count; i++)
        {
            (i % 2 == 0 ? left : right).Add(parts[i]);
        }

        return (left, right);
    }

    private sealed class LineLayer(ExplodedPanel owner) : Control
    {
        public override void Render(DrawingContext context) => owner.DrawLines(context);
    }
}
