using System.Globalization;

namespace MonitorApp.Client.Core.Scene;

/// <summary>Tunable limits for <see cref="HealthClassifier"/>.</summary>
/// <param name="StaleAfter">
/// Readings the node still calls Fresh but older than this are shown Stale. The node applies its own limit
/// first; this one only tightens what the client shows.
/// </param>
public sealed record HealthOptions(TimeSpan StaleAfter)
{
    public static HealthOptions Default { get; } = new(TimeSpan.FromMinutes(10));
}

/// <summary>The classified health of a node and the plain-text reason to show beside it.</summary>
public sealed record NodeHealth(SceneHealth Health, string Reason, DateTimeOffset? LastSeen);

/// <summary>
/// Derives <see cref="SceneHealth"/> from the quality, freshness and age the node reported. Rules:
/// <list type="bullet">
/// <item>Offline only on explicit evidence: a reading whose freshness is "Offline". Silence, age, or a revoked
/// peer never produce Offline (revoked peers are simply not shown by the projector).</item>
/// <item>Stale: freshness "Stale", or "Fresh" with an age over <see cref="HealthOptions.StaleAfter"/>. Contact
/// may merely be delayed; it says nothing about the device being off.</item>
/// <item>Degraded: the collector explicitly reported the reading unavailable (freshness "Unavailable" or quality
/// Unavailable, PermissionDenied or Error).</item>
/// <item>Healthy: quality Valid or Estimated, freshness "Fresh", age within the limit.</item>
/// <item>Unknown: anything unrecognised, or no readings at all.</item>
/// </list>
/// A node aggregates its readings: Offline when every reading is Offline; otherwise Degraded if any live reading
/// is Degraded; else Healthy if any reading is Healthy; else Stale if any is Stale; else Unknown.
/// </summary>
public sealed class HealthClassifier(HealthOptions? options = null)
{
    private readonly HealthOptions options = options ?? HealthOptions.Default;

    public SceneHealth Reading(string quality, string freshness, double ageSeconds)
    {
        if (string.Equals(freshness, "Offline", StringComparison.Ordinal))
        {
            return SceneHealth.Offline;
        }

        if (string.Equals(freshness, "Unavailable", StringComparison.Ordinal) || IsExplicitFailure(quality))
        {
            return SceneHealth.Degraded;
        }

        if (string.Equals(freshness, "Stale", StringComparison.Ordinal))
        {
            return SceneHealth.Stale;
        }

        if (string.Equals(freshness, "Fresh", StringComparison.Ordinal)
            && (string.Equals(quality, "Valid", StringComparison.Ordinal) || string.Equals(quality, "Estimated", StringComparison.Ordinal)))
        {
            return ageSeconds > options.StaleAfter.TotalSeconds ? SceneHealth.Stale : SceneHealth.Healthy;
        }

        return SceneHealth.Unknown;
    }

    public SceneHealth Reading(SceneReading reading) => Reading(reading.Quality, reading.Freshness, reading.AgeSeconds);

    /// <summary>Combines several health values by the aggregation rules in the type remarks.</summary>
    public static SceneHealth Aggregate(IEnumerable<SceneHealth> healths)
    {
        var list = healths.ToList();
        if (list.Count == 0)
        {
            return SceneHealth.Unknown;
        }

        if (list.All(h => h == SceneHealth.Offline))
        {
            return SceneHealth.Offline;
        }

        if (list.Contains(SceneHealth.Degraded))
        {
            return SceneHealth.Degraded;
        }

        if (list.Contains(SceneHealth.Healthy))
        {
            return SceneHealth.Healthy;
        }

        return list.Contains(SceneHealth.Stale) ? SceneHealth.Stale : SceneHealth.Unknown;
    }

    /// <summary>Classifies a node from all of its readings and explains the result in plain text.</summary>
    public NodeHealth Node(IReadOnlyList<SceneReading> readings)
    {
        if (readings.Count == 0)
        {
            return new NodeHealth(SceneHealth.Unknown, "No readings received", null);
        }

        var classified = readings.Select(r => (Reading: r, Health: Reading(r))).ToList();
        var health = Aggregate(classified.Select(c => c.Health));
        var newest = readings.Max(r => r.ObservedAt);
        var minAge = readings.Min(r => r.AgeSeconds);
        var reason = health switch
        {
            SceneHealth.Offline => $"Reported offline; last reading {AgeText(minAge)} ago",
            SceneHealth.Degraded => DegradedReason(classified.Where(c => c.Health == SceneHealth.Degraded).Select(c => c.Reading).ToList()),
            SceneHealth.Stale => $"Last reading {AgeText(minAge)} ago",
            SceneHealth.Healthy => $"Updated {AgeText(minAge)} ago",
            _ => "Readings could not be classified",
        };
        return new NodeHealth(health, reason, newest);
    }

    /// <summary>Plain-text reason for a single reading.</summary>
    public string ReadingReason(SceneReading reading) => Reading(reading) switch
    {
        SceneHealth.Degraded => DegradedReason([reading]),
        SceneHealth.Offline => $"Reported offline; last reading {AgeText(reading.AgeSeconds)} ago",
        SceneHealth.Stale => $"Last reading {AgeText(reading.AgeSeconds)} ago",
        SceneHealth.Healthy => $"Updated {AgeText(reading.AgeSeconds)} ago",
        _ => "Not classified",
    };

    public static string AgeText(double seconds) => seconds switch
    {
        < 0 => "0 s",
        < 90 => seconds.ToString("0", CultureInfo.InvariantCulture) + " s",
        < 5400 => Math.Round(seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min",
        < 129600 => Math.Round(seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + " h",
        _ => Math.Round(seconds / 86400).ToString("0", CultureInfo.InvariantCulture) + " d",
    };

    private static bool IsExplicitFailure(string quality) =>
        quality is "Unavailable" or "PermissionDenied" or "Error";

    private static string DegradedReason(List<SceneReading> degraded)
    {
        var first = degraded[0];
        var text = $"{first.Label} reading unavailable";
        text += first.Quality switch
        {
            "PermissionDenied" => " (permission denied)",
            "Error" => " (collector error)",
            _ => string.Empty,
        };
        return degraded.Count > 1 ? text + $" and {degraded.Count - 1} more" : text;
    }
}
