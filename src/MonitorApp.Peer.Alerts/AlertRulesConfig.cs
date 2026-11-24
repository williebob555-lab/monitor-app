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
    AlertSeverity Severity)
{
    /// <summary>
    /// What the alert says in plain words, for rules whose percentage means nothing to a person (a service being off). When set,
    /// the detail is this sentence and when it started, not the computed percentage.
    /// </summary>
    public string? Explain { get; init; }
}

/// <summary>
/// Typed configuration for the built-in alert rules. Per-peer user overrides (T054, <see cref="AlertRuleCatalog"/>) are stored
/// in the alert store and applied on top of these defaults by the evaluator.
/// </summary>
public sealed record AlertRulesConfig(
    TimeSpan PeerSilentAfter,
    TimeSpan UnavailableAfter,
    IReadOnlyList<ThresholdRule> Thresholds,
    TimeSpan OutboxTtl)
{
    /// <summary>
    /// Debounce, flap side: after an episode of a rule and subject resolves, a new episode of the same rule and
    /// subject is not raised until this long has passed (the breach is simply delayed, not lost, and keeps its
    /// original episode start). The hold side is <see cref="ThresholdRule.Sustained"/>,
    /// <see cref="UnavailableAfter"/> and <see cref="PeerSilentAfter"/>. Working default, to be revisited.
    /// </summary>
    public TimeSpan RefireCooldown { get; init; } = TimeSpan.FromMinutes(2); // Owner decision 2026-11-22.

    /// <summary>
    /// Escalation (T051): a Critical alert still raised and unacknowledged this long after it was raised is delivered once
    /// more to every recipient whose preferences allow it, marked as an escalation. Zero disables escalation.
    /// </summary>
    public TimeSpan EscalationAfter { get; init; } = TimeSpan.FromMinutes(15);

    public const string PeerSilentRuleId = "peer.silent";
    public const string ObservationUnavailableRuleId = "observation.unavailable";

    public static AlertRulesConfig Default { get; } = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(2),
        [
            new ThresholdRule("threshold.disk_free", "Low free disk space", "system.storage.free",
                "system.storage.total", ThresholdComparison.Below, 10, TimeSpan.FromMinutes(5), AlertSeverity.Critical),
            new ThresholdRule("threshold.cpu_high", "Sustained high CPU", "system.cpu.utilization", null,
                ThresholdComparison.Above, 90, TimeSpan.FromMinutes(10), AlertSeverity.Warning),
            new ThresholdRule("threshold.memory_high", "Sustained high memory", "system.memory.used",
                "system.memory.total", ThresholdComparison.Above, 90, TimeSpan.FromMinutes(10), AlertSeverity.Warning),

            // Service diagnosis (D6). Values are booleans or counts, so the "percentage" is value x 100 and the thresholds below
            // read as: not connected (below 50), any folder with errors (above 0), fewer folders up to date than shared (below 100).
            new ThresholdRule(TailscaleDisconnectedRuleId, "Tailscale is not connected", "service.tailscale.connected", null,
                ThresholdComparison.Below, 50, TimeSpan.FromMinutes(5), AlertSeverity.Warning)
            {
                Explain = "Tailscale is installed on this computer but not connected to your tailnet, so other devices cannot reach it " +
                    "through Tailscale. Open Tailscale on this computer and sign in or turn it on.",
            },
            new ThresholdRule(SyncthingErrorsRuleId, "Syncthing folder has errors", "service.syncthing.folders_with_errors", null,
                ThresholdComparison.Above, 0, TimeSpan.FromMinutes(5), AlertSeverity.Warning)
            {
                Explain = "At least one Syncthing folder on this computer reports errors, so some files are not syncing. " +
                    "Rescanning often clears it; if not, Syncthing's own page lists the failing files.",
            },
            new ThresholdRule(SyncthingBehindRuleId, "Syncthing folders not in sync", "service.syncthing.folders_up_to_date",
                "service.syncthing.folders", ThresholdComparison.Below, 100, TimeSpan.FromHours(2), AlertSeverity.Warning)
            {
                Explain = "Some Syncthing folders on this computer have not been up to date for two hours. Usually the other device " +
                    "is off or unreachable; a rescan helps when files changed without Syncthing noticing.",
            },
        ],
        TimeSpan.FromDays(7));

    public const string TailscaleDisconnectedRuleId = "service.tailscale_disconnected";
    public const string SyncthingErrorsRuleId = "service.syncthing_errors";
    public const string SyncthingBehindRuleId = "service.syncthing_behind";

    public void Validate()
    {
        if (PeerSilentAfter <= TimeSpan.Zero || UnavailableAfter < TimeSpan.Zero || OutboxTtl <= TimeSpan.Zero || RefireCooldown < TimeSpan.Zero || EscalationAfter < TimeSpan.Zero)
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
