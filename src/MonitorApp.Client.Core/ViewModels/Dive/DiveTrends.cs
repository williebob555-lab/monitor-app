using System.Globalization;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Dive;

/// <summary>What a sparkline shows.</summary>
public enum DiveTrendKind
{
    /// <summary>CPU utilisation, drawn on a fixed 0..100 % scale.</summary>
    Cpu,

    /// <summary>Network bytes sent plus received per second, derived from the counters and scaled to the window's peak.</summary>
    Network,
}

/// <summary>
/// A small sparkline of the recent window: <see cref="Points"/> are 0..1 heights in time order, <see cref="Text"/> says the range and the
/// latest value in words (the line never stands alone). Built only from real readings: history read from the node plus live refreshes.
/// </summary>
public sealed record DiveTrend(DiveTrendKind Kind, string Label, IReadOnlyList<double> Points, string NowText, string Text)
{
    public string AccessibleText => Text;
}

/// <summary>
/// Rolling buffers of recent CPU and network-counter readings per subject, seeded from the node's history and appended by every live
/// refresh. Nothing is interpolated or invented: a series with fewer than two usable points has no trend, and network rates come only from
/// two consecutive counter readings that did not go backwards.
/// </summary>
public sealed class DiveTrendBuffer
{
    /// <summary>How far back a sparkline reaches.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The signals a sparkline can be built from.</summary>
    public static readonly IReadOnlyList<string> Signals = [SignalIds.CpuUtilization, SignalIds.NetworkBytesSent, SignalIds.NetworkBytesReceived];

    /// <summary>At most this many points are drawn; longer series are averaged into this many time buckets.</summary>
    public const int MaxDrawnPoints = 60;

    private const int MaxStoredPoints = 1200;

    private readonly Dictionary<(string Subject, string Signal), SortedList<DateTimeOffset, double>> series = [];

    /// <summary>Total stored points (tests and diagnostics).</summary>
    public int Count => series.Values.Sum(s => s.Count);

    public void Clear() => series.Clear();

    /// <summary>Adds numeric readings of the tracked signals from history (unavailable or non-numeric points are skipped).</summary>
    public void AddHistory(IEnumerable<HistoryPointDto> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (var p in points)
        {
            if (p.Value is { Kind: "number", Number: { } n } && p.Freshness is not "Unavailable" && Signals.Contains(p.SignalId))
            {
                Add(p.SubjectEntityId, p.SignalId, p.ObservedAt, Normalize(p.SignalId, p.Unit, n));
            }
        }
    }

    /// <summary>Adds the current readings a live scene carries (one point per reading; a repeated reading is stored once).</summary>
    public void AddScene(SceneGraph scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        foreach (var reading in scene.Nodes.SelectMany(n => n.Layers).SelectMany(l => l.Readings))
        {
            if (reading.Number is { } n && reading.Freshness is not "Unavailable" && Signals.Contains(reading.SignalId))
            {
                Add(reading.SubjectEntityId, reading.SignalId, reading.ObservedAt, Normalize(reading.SignalId, reading.Unit, n));
            }
        }
    }

    /// <summary>The CPU sparkline for a node, from the subject its current CPU reading names; null without two points.</summary>
    public DiveTrend? Cpu(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var subject = SubjectOf(node, SignalIds.CpuUtilization);
        if (subject is null || !series.TryGetValue((subject, SignalIds.CpuUtilization), out var cpu))
        {
            return null;
        }

        var points = Recent(cpu).ToList();
        if (points.Count < 2)
        {
            return null;
        }

        string Pct(double v) => Math.Round(v * 100).ToString("0", CultureInfo.InvariantCulture);
        var now = Pct(points[^1].Value);
        return new DiveTrend(
            DiveTrendKind.Cpu,
            "CPU",
            Draw(points).Select(v => Math.Clamp(v, 0, 1)).ToList(),
            now + " %",
            $"CPU over the last {Minutes(points)}: {Pct(points.Min(p => p.Value))} to {Pct(points.Max(p => p.Value))} %, now {now} %");
    }

    /// <summary>The network sparkline (sent plus received per second) for a node; null without two consecutive counter readings.</summary>
    public DiveTrend? Network(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var rates = new List<List<(DateTimeOffset At, double Value)>>();
        foreach (var signal in new[] { SignalIds.NetworkBytesSent, SignalIds.NetworkBytesReceived })
        {
            if (SubjectOf(node, signal) is { } subject && series.TryGetValue((subject, signal), out var counter))
            {
                var list = Rates(Recent(counter).ToList());
                if (list.Count > 0)
                {
                    rates.Add(list);
                }
            }
        }

        if (rates.Count == 0)
        {
            return null;
        }

        // Sum the counters at the end times of the densest one, each counter contributing the rate of the interval covering that time.
        var spine = rates.OrderByDescending(r => r.Count).First();
        var combined = spine.Select(point => (point.At, Value: rates.Sum(r => RateAt(r, point.At)))).ToList();
        if (combined.Count < 2)
        {
            return null;
        }

        var peak = combined.Max(p => p.Value);
        var now = SignalCatalog.Bytes(combined[^1].Value) + "/s";
        return new DiveTrend(
            DiveTrendKind.Network,
            "Network",
            Draw(combined).Select(v => peak > 0 ? Math.Clamp(v / peak, 0, 1) : 0).ToList(),
            now,
            $"Network over the last {Minutes(combined)}: peak {SignalCatalog.Bytes(peak)}/s, now {now}");
    }

    private static double Normalize(string signal, string? unit, double value) =>
        signal == SignalIds.CpuUtilization && string.Equals(unit, "Percent", StringComparison.OrdinalIgnoreCase) ? value / 100 : value;

    /// <summary>The subject a node's readings of <paramref name="signal"/> name, else the subject of its other readings (the node itself).</summary>
    private static string? SubjectOf(SceneNode node, string signal)
    {
        var readings = node.Layers.SelectMany(l => l.Readings).ToList();
        return (readings.FirstOrDefault(r => r.SignalId == signal) ?? readings.FirstOrDefault(r => r.OriginPeerId == node.Id) ?? readings.FirstOrDefault())
            ?.SubjectEntityId;
    }

    private static string Minutes(List<(DateTimeOffset At, double Value)> points)
    {
        var minutes = Math.Max(1, (int)Math.Round((points[^1].At - points[0].At).TotalMinutes));
        return minutes == 1 ? "minute" : minutes.ToString(CultureInfo.InvariantCulture) + " min";
    }

    private static IEnumerable<(DateTimeOffset At, double Value)> Recent(SortedList<DateTimeOffset, double> points)
    {
        if (points.Count == 0)
        {
            yield break;
        }

        var from = points.Keys[^1] - Window;
        for (var i = 0; i < points.Count; i++)
        {
            if (points.Keys[i] >= from)
            {
                yield return (points.Keys[i], points.Values[i]);
            }
        }
    }

    /// <summary>Bytes per second between consecutive counter readings, at the later reading; a reset (counter went down) gives no rate.</summary>
    private static List<(DateTimeOffset At, double Value)> Rates(List<(DateTimeOffset At, double Value)> counter)
    {
        var rates = new List<(DateTimeOffset, double)>();
        for (var i = 1; i < counter.Count; i++)
        {
            var seconds = (counter[i].At - counter[i - 1].At).TotalSeconds;
            var delta = counter[i].Value - counter[i - 1].Value;
            if (seconds > 0 && delta >= 0)
            {
                rates.Add((counter[i].At, delta / seconds));
            }
        }

        return rates;
    }

    private static double RateAt(List<(DateTimeOffset At, double Value)> rates, DateTimeOffset at)
    {
        foreach (var rate in rates)
        {
            if (rate.At >= at)
            {
                return rate.Value;
            }
        }

        return rates[^1].Value;
    }

    /// <summary>The values to draw: all of them, or time-bucket averages when there are more than <see cref="MaxDrawnPoints"/>.</summary>
    private static List<double> Draw(List<(DateTimeOffset At, double Value)> points)
    {
        if (points.Count <= MaxDrawnPoints)
        {
            return points.Select(p => p.Value).ToList();
        }

        var buckets = new List<double>();
        var per = (double)points.Count / MaxDrawnPoints;
        for (var b = 0; b < MaxDrawnPoints; b++)
        {
            var start = (int)(b * per);
            var end = Math.Max(start + 1, (int)((b + 1) * per));
            buckets.Add(points.Skip(start).Take(end - start).Average(p => p.Value));
        }

        return buckets;
    }

    private void Add(string subject, string signal, DateTimeOffset at, double value)
    {
        if (!series.TryGetValue((subject, signal), out var points))
        {
            points = [];
            series[(subject, signal)] = points;
        }

        points[at] = value;
        var from = points.Keys[^1] - Window;
        while (points.Count > 0 && (points.Keys[0] < from || points.Count > MaxStoredPoints))
        {
            points.RemoveAt(0);
        }
    }
}
