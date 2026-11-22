using Avalonia;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

public enum PortSide
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>A connection point on the edge of a node.</summary>
public readonly record struct Port(Point Position, PortSide Side);

/// <summary>Both ends of a link.</summary>
public readonly record struct LinkPorts(Port From, Port To);

public readonly record struct PolylinePoint(Point Position, double Angle);

public readonly record struct NodeBounds(string Id, Rect Bounds);

public readonly record struct LinkPath(string Id, IReadOnlyList<Point> Points);

/// <summary>A snapping guide: a vertical guide is the line x = Value, a horizontal one y = Value (world units).</summary>
public readonly record struct Guide(bool Vertical, double Value);

public sealed record SnapResult(Point Position, IReadOnlyList<Guide> Guides);

/// <summary>Pure routing and polyline math. Works in any consistent coordinate space.</summary>
public static class SchematicGeometry
{
    public static Vector Outward(PortSide side) => side switch
    {
        PortSide.Top => new Vector(0, -1),
        PortSide.Bottom => new Vector(0, 1),
        PortSide.Left => new Vector(-1, 0),
        _ => new Vector(1, 0),
    };

    /// <summary>Orthogonal port-to-port route with a short stub leaving each port; redundant points are removed.</summary>
    public static IReadOnlyList<Point> Route(Port a, Port b, double stub)
    {
        var a1 = a.Position + (Outward(a.Side) * stub);
        var b1 = b.Position + (Outward(b.Side) * stub);
        var aVertical = a.Side is PortSide.Top or PortSide.Bottom;
        var bVertical = b.Side is PortSide.Top or PortSide.Bottom;
        var raw = new List<Point>(8) { a.Position, a1 };
        if (aVertical && bVertical)
        {
            var my = (a1.Y + b1.Y) / 2;
            raw.Add(new Point(a1.X, my));
            raw.Add(new Point(b1.X, my));
        }
        else if (!aVertical && !bVertical)
        {
            var mx = (a1.X + b1.X) / 2;
            raw.Add(new Point(mx, a1.Y));
            raw.Add(new Point(mx, b1.Y));
        }
        else if (aVertical)
        {
            raw.Add(new Point(a1.X, b1.Y));
        }
        else
        {
            raw.Add(new Point(b1.X, a1.Y));
        }

        raw.Add(b1);
        raw.Add(b.Position);
        return Simplify(raw);
    }

    /// <summary>Drops repeated points and points in the middle of a straight run.</summary>
    public static IReadOnlyList<Point> Simplify(IReadOnlyList<Point> points)
    {
        var result = new List<Point>(points.Count);
        foreach (var p in points)
        {
            if (result.Count > 0 && Distance(result[^1], p) < 0.01)
            {
                continue;
            }

            while (result.Count >= 2 && Collinear(result[^2], result[^1], p))
            {
                result.RemoveAt(result.Count - 1);
            }

            result.Add(p);
        }

        return result;
    }

    private static bool Collinear(Point a, Point b, Point c) =>
        Math.Abs(((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X))) < 0.01;

    public static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    public static double Length(IReadOnlyList<Point> points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        return total;
    }

    /// <summary>The point at <paramref name="fraction"/> (0..1) of the polyline length, with the local heading.</summary>
    public static PolylinePoint Along(IReadOnlyList<Point> points, double fraction)
    {
        if (points.Count == 0)
        {
            return default;
        }

        if (points.Count == 1)
        {
            return new PolylinePoint(points[0], 0);
        }

        var remaining = Math.Clamp(fraction, 0, 1) * Length(points);
        for (var i = 1; i < points.Count; i++)
        {
            var segment = Distance(points[i - 1], points[i]);
            if (remaining <= segment || i == points.Count - 1)
            {
                var k = segment > 0 ? Math.Min(1, remaining / segment) : 0;
                var a = points[i - 1];
                var b = points[i];
                return new PolylinePoint(
                    new Point(a.X + ((b.X - a.X) * k), a.Y + ((b.Y - a.Y) * k)),
                    Math.Atan2(b.Y - a.Y, b.X - a.X));
            }

            remaining -= segment;
        }

        return new PolylinePoint(points[^1], 0);
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0, 1);
        return Distance(p, new Point(a.X + (dx * t), a.Y + (dy * t)));
    }

    public static double DistanceToPolyline(IReadOnlyList<Point> points, Point p)
    {
        var best = double.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
        }

        return best;
    }
}

/// <summary>Chooses the side each link leaves a node from and spreads several links on one side.</summary>
public static class PortPlanner
{
    private readonly record struct End(string LinkId, bool IsFrom, string NodeId, PortSide Side, double SortKey);

    public static IReadOnlyDictionary<string, LinkPorts> Plan(
        IReadOnlyDictionary<string, Rect> rects, IReadOnlyList<SceneLink> links)
    {
        var ends = new List<End>(links.Count * 2);
        foreach (var link in links)
        {
            if (!rects.TryGetValue(link.FromId, out var from) || !rects.TryGetValue(link.ToId, out var to))
            {
                continue;
            }

            ends.Add(MakeEnd(link.Id, true, link.FromId, from, to));
            ends.Add(MakeEnd(link.Id, false, link.ToId, to, from));
        }

        var positions = new Dictionary<(string, bool), Port>();
        foreach (var group in ends.GroupBy(e => (e.NodeId, e.Side)))
        {
            var ordered = group.OrderBy(e => e.SortKey).ThenBy(e => e.LinkId, StringComparer.Ordinal).ThenBy(e => e.IsFrom).ToList();
            var rect = rects[group.Key.NodeId];
            for (var i = 0; i < ordered.Count; i++)
            {
                var f = (i + 1.0) / (ordered.Count + 1);
                var position = group.Key.Side switch
                {
                    PortSide.Top => new Point(rect.X + (f * rect.Width), rect.Y),
                    PortSide.Bottom => new Point(rect.X + (f * rect.Width), rect.Bottom),
                    PortSide.Left => new Point(rect.X, rect.Y + (f * rect.Height)),
                    _ => new Point(rect.Right, rect.Y + (f * rect.Height)),
                };
                positions[(ordered[i].LinkId, ordered[i].IsFrom)] = new Port(position, group.Key.Side);
            }
        }

        var result = new Dictionary<string, LinkPorts>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (positions.TryGetValue((link.Id, true), out var a) && positions.TryGetValue((link.Id, false), out var b))
            {
                result[link.Id] = new LinkPorts(a, b);
            }
        }

        return result;
    }

    private static End MakeEnd(string linkId, bool isFrom, string nodeId, Rect self, Rect other)
    {
        var c = self.Center;
        var o = other.Center;
        double dx = o.X - c.X, dy = o.Y - c.Y;
        var horizontal = Math.Abs(dx) / Math.Max(1e-6, self.Width) > Math.Abs(dy) / Math.Max(1e-6, self.Height);
        var side = horizontal ? (dx >= 0 ? PortSide.Right : PortSide.Left) : (dy >= 0 ? PortSide.Bottom : PortSide.Top);
        return new End(linkId, isFrom, nodeId, side, horizontal ? o.Y : o.X);
    }
}

public static class SchematicHitTest
{
    /// <summary>The top-most (last in paint order) node containing the point, with <paramref name="slop"/> padding.</summary>
    public static string? Node(IReadOnlyList<NodeBounds> paintOrder, Point p, double slop)
    {
        for (var i = paintOrder.Count - 1; i >= 0; i--)
        {
            if (paintOrder[i].Bounds.Inflate(slop).Contains(p))
            {
                return paintOrder[i].Id;
            }
        }

        return null;
    }

    /// <summary>The nearest link within <paramref name="tolerance"/> of the point.</summary>
    public static string? Link(IReadOnlyList<LinkPath> links, Point p, double tolerance)
    {
        string? best = null;
        var bestDistance = tolerance;
        foreach (var link in links)
        {
            var d = SchematicGeometry.DistanceToPolyline(link.Points, p);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = link.Id;
            }
        }

        return best;
    }
}

/// <summary>Alignment guides with other nodes, otherwise an eighth-unit grid, per axis (world units).</summary>
public static class SchematicSnapper
{
    public const double GridStep = 0.125;
    public const double DefaultThreshold = 0.1;

    public static SnapResult Snap(Point proposed, IEnumerable<Point> others, double threshold = DefaultThreshold)
    {
        var guides = new List<Guide>(2);
        var x = proposed.X;
        var y = proposed.Y;
        double bestX = threshold, bestY = threshold;
        double? snapX = null, snapY = null;
        foreach (var o in others)
        {
            if (Math.Abs(o.X - proposed.X) < bestX)
            {
                bestX = Math.Abs(o.X - proposed.X);
                snapX = o.X;
            }

            if (Math.Abs(o.Y - proposed.Y) < bestY)
            {
                bestY = Math.Abs(o.Y - proposed.Y);
                snapY = o.Y;
            }
        }

        if (snapX is { } sx)
        {
            x = sx;
            guides.Add(new Guide(true, sx));
        }
        else
        {
            x = Math.Round(x / GridStep) * GridStep;
        }

        if (snapY is { } sy)
        {
            y = sy;
            guides.Add(new Guide(false, sy));
        }
        else
        {
            y = Math.Round(y / GridStep) * GridStep;
        }

        return new SnapResult(new Point(x, y), guides);
    }
}

/// <summary>Pulses on a link are only ever produced from real, non-zero activity.</summary>
public static class PulsePlanner
{
    public static bool ShouldPulse(SceneLink link, SceneHealth from, SceneHealth to)
    {
        if (!(link.Activity > 0) || double.IsNaN(link.Activity))
        {
            return false;
        }

        return !(IsSilent(from) || IsSilent(to) || IsSilent(link.Health));
    }

    private static bool IsSilent(SceneHealth h) => h is SceneHealth.Offline or SceneHealth.Stale;

    public static int Count(double activity) =>
        activity > 0 ? Math.Clamp((int)Math.Round(Math.Min(1, activity) * 5), 1, 6) : 0;

    /// <summary>Position (0..1) of pulse <paramref name="index"/> along the path. Static spacing under reduced motion.</summary>
    public static double Fraction(int index, int count, double seconds, double activity, bool reducedMotion)
    {
        if (reducedMotion)
        {
            return (index + 0.5) / count;
        }

        var f = ((seconds * (0.06 + (0.22 * Math.Min(1, activity)))) + ((double)index / count)) % 1.0;
        return f < 0 ? f + 1 : f;
    }
}

/// <summary>Isometric projection used by the exploded view.</summary>
public readonly record struct IsoProjector(Point Origin, double Unit)
{
    public Point Project(double x, double y, double z) =>
        new(Origin.X + ((x - y) * Unit * 0.866), Origin.Y + (((x + y) * Unit * 0.5) - (z * Unit)));
}

public static class LabelLayout
{
    /// <summary>Moves values apart so neighbours are at least <paramref name="minGap"/> apart, keeping order, within [min,max].</summary>
    public static double[] Spread(IReadOnlyList<double> values, double minGap, double min, double max)
    {
        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToArray();
        var result = new double[values.Count];
        var last = double.NegativeInfinity;
        foreach (var i in order)
        {
            var v = Math.Max(Math.Max(values[i], min), last + minGap);
            result[i] = v;
            last = v;
        }

        var next = max + minGap;
        for (var k = order.Length - 1; k >= 0; k--)
        {
            var i = order[k];
            result[i] = Math.Min(result[i], next - minGap);
            next = result[i];
        }

        return result;
    }
}
