using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Alerts;

public enum ThresholdComparison
{
    Below = 1,
    Above = 2,
}

/// <summary>
/// A numeric threshold on one locally collected signal. The compared value is a percentage:
/// <c>signal / denominator * 100</c> when a denominator signal is set (disk free of total), otherwise
/// <c>signal * 100</c> (a ratio such as CPU utilization). Breach must hold continuously for
/// <see cref="Sustained"/> measured in observation time, so it is deterministic from stored data.
/// </summary>
public sealed record ThresholdRule(
    string RuleId,
    string Title,
    string SignalId,
    string? DenominatorSignalId,
    ThresholdComparison Comparison,
    double ThresholdPercent,
    TimeSpan Sustained,
    AlertSeverity Severity);

/// <summary>Typed configuration for the built-in alert rules. There is intentionally no user rule editor yet.</summary>
public sealed record AlertRulesConfig(
    TimeSpan PeerSilentAfter,
    TimeSpan UnavailableAfter,
    IReadOnlyList<ThresholdRule> Thresholds,
    TimeSpan OutboxTtl)
{
    public const string PeerSilentRuleId = "peer.silent";
    public const string ObservationUnavailableRuleId = "observation.unavailable";

    public static AlertRulesConfig Default { get; } = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(2),
        [
            new ThresholdRule("threshold.disk_free", "Low free disk space", "system.storage.free",
                "system.storage.total", ThresholdComparison.Below, 10, TimeSpan.Zero, AlertSeverity.Critical),
            new ThresholdRule("threshold.cpu_high", "Sustained high CPU", "system.cpu.utilization", null,
                ThresholdComparison.Above, 90, TimeSpan.FromMinutes(10), AlertSeverity.Warning),
        ],
        TimeSpan.FromDays(7));

    public void Validate()
    {
        if (PeerSilentAfter <= TimeSpan.Zero || UnavailableAfter < TimeSpan.Zero || OutboxTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AlertRulesConfig));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Thresholds)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleId) || string.IsNullOrWhiteSpace(rule.SignalId) ||
                !double.IsFinite(rule.ThresholdPercent) || rule.Sustained < TimeSpan.Zero || !ids.Add(rule.RuleId))
            {
                throw new ArgumentException("Threshold rules need unique ids, signals, and finite thresholds.", nameof(Thresholds));
            }
        }
    }
}
