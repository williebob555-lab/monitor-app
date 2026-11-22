namespace MonitorApp.Client.Core.Scene;

/// <summary>
/// Measured network throughput per subject entity, derived from the network byte counters over a short window.
/// Used only to scale broad activity (0..1); never shown as an exact value.
/// </summary>
public sealed record NetworkRates(IReadOnlyDictionary<string, double> BytesPerSecondBySubject)
{
    public static NetworkRates None { get; } = new(new Dictionary<string, double>());

    /// <summary>
    /// Sent plus received bytes per second per subject: per counter, the newest minus the oldest valid reading
    /// divided by the time between them. A counter that went backwards (reset) or has fewer than two valid
    /// readings contributes nothing, so no rate is invented.
    /// </summary>
    public static NetworkRates FromHistory(IEnumerable<HistoryPointDto> points)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var counters = points
            .Where(p => p.SignalId is SignalIds.NetworkBytesSent or SignalIds.NetworkBytesReceived
                && p.Value is { Kind: "number", Number: not null }
                && p.Freshness is not "Unavailable")
            .GroupBy(p => (p.SubjectEntityId, p.SignalId));
        foreach (var counter in counters)
        {
            var ordered = counter.OrderBy(p => p.ObservedAt).ToList();
            var first = ordered[0];
            var last = ordered[^1];
            var seconds = (last.ObservedAt - first.ObservedAt).TotalSeconds;
            var delta = last.Value!.Number!.Value - first.Value!.Number!.Value;
            if (seconds <= 0 || delta < 0)
            {
                continue;
            }

            totals[counter.Key.SubjectEntityId] = totals.GetValueOrDefault(counter.Key.SubjectEntityId) + (delta / seconds);
        }

        return new NetworkRates(totals);
    }

    /// <summary>Log scale: about 1 KB/s maps to 0.1 and 100 MB/s to 1.0; below roughly 280 B/s is 0.</summary>
    public static double ToActivity(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return 0;
        }

        return Math.Clamp(0.1 + ((Math.Log10(bytesPerSecond) - 3) * 0.9 / 5), 0, 1);
    }

    public double ActivityFor(string subjectEntityId) =>
        BytesPerSecondBySubject.TryGetValue(subjectEntityId, out var rate) ? ToActivity(rate) : 0;
}
