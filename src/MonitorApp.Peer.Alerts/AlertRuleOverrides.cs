using System.Globalization;

namespace MonitorApp.Peer.Alerts;

/// <summary>A requested override: enable or disable a rule kind and optionally change its threshold (null keeps the default).</summary>
public sealed record AlertRuleOverrideInput(string RuleId, bool Enabled, double? Threshold);

/// <summary>
/// A stored override (alert schema v6). <see cref="PeerId"/> is the peer the rule applies to: the local peer for the CPU, memory
/// and disk rules (the owner only evaluates its own signals) and the watched peer for peer.silent.
/// </summary>
public sealed record StoredRuleOverride(string PeerId, string RuleId, bool Enabled, double? Threshold, DateTimeOffset UpdatedAt);

/// <summary>One rule as a user sees it: the built-in default, the effective value and the range the server accepts.</summary>
public sealed record EffectiveAlertRule(
    string SubjectPeerId,
    string RuleId,
    string Title,
    string Unit,
    double DefaultThreshold,
    double Threshold,
    bool Enabled,
    bool Overridden,
    double Minimum,
    double Maximum,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// The editable rule kinds and their server-side ranges (T054). CPU, memory and disk thresholds are percentages in
/// [<see cref="MinimumPercent"/>, <see cref="MaximumPercent"/>]; "peer not seen" is minutes in
/// [<see cref="MinimumSilentMinutes"/>, <see cref="MaximumSilentMinutes"/>]. Every validation, local API and remote command alike, goes
/// through <see cref="Validate"/> so the two paths cannot disagree.
/// </summary>
public static class AlertRuleCatalog
{
    public const string CpuRuleId = "threshold.cpu_high";
    public const string MemoryRuleId = "threshold.memory_high";
    public const string DiskRuleId = "threshold.disk_free";
    public const double MinimumPercent = 1;
    public const double MaximumPercent = 99;
    public const double MinimumSilentMinutes = 1;
    public const double MaximumSilentMinutes = 10080;
    public const int MaximumOverrides = 8;
    public const int MaximumPeerIdLength = 128;

    public static IReadOnlyList<string> SignalRuleIds { get; } = [CpuRuleId, MemoryRuleId, DiskRuleId];

    public static bool IsKnown(string? ruleId) =>
        ruleId == AlertRulesConfig.PeerSilentRuleId || (ruleId is not null && SignalRuleIds.Contains(ruleId, StringComparer.Ordinal));

    public static string UnitOf(string ruleId) => ruleId == AlertRulesConfig.PeerSilentRuleId ? "minutes" : "percent";

    /// <summary>Returns a human-readable reason when the overrides are not acceptable, otherwise null.</summary>
    public static string? Validate(IReadOnlyCollection<AlertRuleOverrideInput>? overrides)
    {
        if (overrides is null)
        {
            return "Rule overrides are required.";
        }

        if (overrides.Count > MaximumOverrides)
        {
            return string.Create(CultureInfo.InvariantCulture, $"At most {MaximumOverrides} rule overrides are allowed.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in overrides)
        {
            if (item is null || !IsKnown(item.RuleId))
            {
                return $"Unknown rule '{Clip(item?.RuleId)}'. Known rules: {string.Join(", ", SignalRuleIds.Append(AlertRulesConfig.PeerSilentRuleId))}.";
            }

            if (!seen.Add(item.RuleId))
            {
                return $"Rule '{item.RuleId}' appears more than once.";
            }

            if (item.Threshold is { } value)
            {
                var (minimum, maximum) = item.RuleId == AlertRulesConfig.PeerSilentRuleId
                    ? (MinimumSilentMinutes, MaximumSilentMinutes)
                    : (MinimumPercent, MaximumPercent);
                if (!double.IsFinite(value) || value < minimum || value > maximum)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"The {UnitOf(item.RuleId)} threshold of '{item.RuleId}' must be between {minimum:0.##} and {maximum:0.##}.");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks that each rule is edited for a peer it can apply to: the signal rules only for the local peer, peer.silent only for an
    /// active trusted peer other than the local one. Returns a reason or null.
    /// </summary>
    public static string? ValidateSubject(
        string subjectPeerId, string localPeerId, IReadOnlySet<string> activePeerIds, IReadOnlyCollection<AlertRuleOverrideInput> overrides)
    {
        if (string.IsNullOrWhiteSpace(subjectPeerId) || subjectPeerId.Length > MaximumPeerIdLength)
        {
            return "A peer id is required.";
        }

        foreach (var item in overrides)
        {
            if (item.RuleId == AlertRulesConfig.PeerSilentRuleId)
            {
                if (subjectPeerId == localPeerId || !activePeerIds.Contains(subjectPeerId))
                {
                    return "peer.silent can only be edited for an active trusted peer.";
                }
            }
            else if (subjectPeerId != localPeerId)
            {
                return $"{item.RuleId} is evaluated by the peer that owns the signal; edit it on that peer.";
            }
        }

        return null;
    }

    /// <summary>Every editable rule with its default, effective value and accepted range: the signal rules for the local peer, peer.silent per active peer.</summary>
    public static IReadOnlyList<EffectiveAlertRule> Describe(
        AlertRulesConfig config,
        string localPeerId,
        IEnumerable<string> activePeerIds,
        IReadOnlyList<StoredRuleOverride> overrides)
    {
        var byKey = overrides.ToDictionary(item => (item.PeerId, item.RuleId));
        var states = new List<EffectiveAlertRule>();
        foreach (var rule in config.Thresholds.Where(item => SignalRuleIds.Contains(item.RuleId, StringComparer.Ordinal)))
        {
            byKey.TryGetValue((localPeerId, rule.RuleId), out var stored);
            states.Add(new EffectiveAlertRule(localPeerId, rule.RuleId, rule.Title, "percent", rule.ThresholdPercent,
                stored?.Threshold ?? rule.ThresholdPercent, stored?.Enabled ?? true, stored is not null,
                MinimumPercent, MaximumPercent, stored?.UpdatedAt));
        }

        foreach (var peer in activePeerIds.Where(id => id != localPeerId).Order(StringComparer.Ordinal))
        {
            byKey.TryGetValue((peer, AlertRulesConfig.PeerSilentRuleId), out var stored);
            var defaultMinutes = config.PeerSilentAfter.TotalMinutes;
            states.Add(new EffectiveAlertRule(peer, AlertRulesConfig.PeerSilentRuleId, "Peer not seen", "minutes", defaultMinutes,
                stored?.Threshold ?? defaultMinutes, stored?.Enabled ?? true, stored is not null,
                MinimumSilentMinutes, MaximumSilentMinutes, stored?.UpdatedAt));
        }

        return states;
    }

    private static string Clip(string? text) => text is null ? string.Empty : text.Length <= 64 ? text : text[..64];
}
