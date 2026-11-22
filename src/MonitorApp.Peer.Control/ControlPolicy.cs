using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;

namespace MonitorApp.Peer.Control;

/// <summary>How much assurance a command needs beyond the authenticated, signed session.</summary>
public enum ControlRiskTier
{
    /// <summary>Authenticated, signed and authorised by the peer's grant.</summary>
    Normal = 1,

    /// <summary>Long-lived reductions in monitoring (long pause or long mute): needs a user-presence signature.</summary>
    Elevated = 2,

    /// <summary>Changes who is trusted (pairing approval): needs a user-presence signature.</summary>
    High = 3,
}

/// <summary>The control surface and its bounds. Monitoring controls only: no power, service or script actions exist.</summary>
public static class ControlPolicy
{
    public static readonly TimeSpan DefaultCommandLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumCommandLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>A pause longer than this (or without an expiry) needs a user-presence signature.</summary>
    public static readonly TimeSpan ElevatedPauseThreshold = TimeSpan.FromHours(1);

    /// <summary>A mute longer than this needs a user-presence signature.</summary>
    public static readonly TimeSpan ElevatedMuteThreshold = TimeSpan.FromHours(24);

    public static readonly TimeSpan MaximumPause = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumMute = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumIntervalOverrideDuration = TimeSpan.FromDays(7);
    public static readonly TimeSpan MinimumSamplingInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaximumSamplingInterval = TimeSpan.FromHours(1);

    public const int MaximumIdLength = 128;
    public const int MaximumMutedRuleKinds = 32;
    public const int MaximumVersionLength = 64;
    public const int MinimumNonceBytes = 16;
    public const int MaximumNonceBytes = 64;

    public static string ActionName(ControlCommand command) => command.PayloadCase switch
    {
        ControlCommand.PayloadOneofCase.PauseCollection => "pause_collection",
        ControlCommand.PayloadOneofCase.ResumeCollection => "resume_collection",
        ControlCommand.PayloadOneofCase.SetSamplingInterval => "set_sampling_interval",
        ControlCommand.PayloadOneofCase.AcknowledgeAlert => "acknowledge_alert",
        ControlCommand.PayloadOneofCase.MuteAlerts => "mute_alerts",
        ControlCommand.PayloadOneofCase.UnmuteAlerts => "unmute_alerts",
        ControlCommand.PayloadOneofCase.SetAlertPreferences => "set_alert_preferences",
        ControlCommand.PayloadOneofCase.SetAlertRuleOverrides => "set_alert_rule_overrides",
        ControlCommand.PayloadOneofCase.ResolvePairing =>
            command.ResolvePairing.Approve ? "approve_pairing" : "reject_pairing",
        ControlCommand.PayloadOneofCase.EnrollPresenceKey => "enroll_presence_key",
        ControlCommand.PayloadOneofCase.ApproveUpdate => "approve_update",
        _ => "unknown",
    };

    /// <summary>
    /// Classifies a structurally valid command. Approving a pairing is high risk (it grants a new device trust);
    /// rejecting is the safe direction and stays normal. Only the permanent or long forms of pause/mute are elevated.
    /// </summary>
    public static ControlRiskTier Classify(ControlCommand command) => command.PayloadCase switch
    {
        ControlCommand.PayloadOneofCase.ResolvePairing when command.ResolvePairing.Approve => ControlRiskTier.High,
        ControlCommand.PayloadOneofCase.EnrollPresenceKey => ControlRiskTier.High,
        ControlCommand.PayloadOneofCase.ApproveUpdate => ControlRiskTier.High,
        // Changes (or disables) what alerts fire for every recipient, unlike a phone's own preferences, so it needs the biometric.
        ControlCommand.PayloadOneofCase.SetAlertRuleOverrides => ControlRiskTier.Elevated,
        ControlCommand.PayloadOneofCase.PauseCollection =>
            command.PauseCollection.DurationSeconds == 0 ||
            TimeSpan.FromSeconds(command.PauseCollection.DurationSeconds) > ElevatedPauseThreshold
                ? ControlRiskTier.Elevated
                : ControlRiskTier.Normal,
        ControlCommand.PayloadOneofCase.MuteAlerts =>
            TimeSpan.FromSeconds(command.MuteAlerts.DurationSeconds) > ElevatedMuteThreshold
                ? ControlRiskTier.Elevated
                : ControlRiskTier.Normal,
        _ => ControlRiskTier.Normal,
    };

    /// <summary>True when the command must carry a user-presence signature (enrollment proves possession instead).</summary>
    public static bool RequiresPresence(ControlCommand command) =>
        Classify(command) >= ControlRiskTier.Elevated &&
        command.PayloadCase != ControlCommand.PayloadOneofCase.EnrollPresenceKey;

    /// <summary>Returns a stable reason code when the payload is out of bounds, otherwise null.</summary>
    public static string? ValidatePayload(ControlCommand command)
    {
        switch (command.PayloadCase)
        {
            case ControlCommand.PayloadOneofCase.PauseCollection:
                var pause = command.PauseCollection;
                return pause.CapabilityId.Length > MaximumIdLength ||
                    TimeSpan.FromSeconds(pause.DurationSeconds) > MaximumPause
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.ResumeCollection:
                return command.ResumeCollection.CapabilityId.Length > MaximumIdLength ? "invalid_payload" : null;
            case ControlCommand.PayloadOneofCase.SetSamplingInterval:
                var interval = command.SetSamplingInterval;
                if (interval.IntervalSeconds == 0)
                {
                    return null;
                }

                var value = TimeSpan.FromSeconds(interval.IntervalSeconds);
                return value < MinimumSamplingInterval || value > MaximumSamplingInterval ||
                    TimeSpan.FromSeconds(interval.DurationSeconds) > MaximumIntervalOverrideDuration
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.AcknowledgeAlert:
                return string.IsNullOrWhiteSpace(command.AcknowledgeAlert.AlertId) ||
                    command.AcknowledgeAlert.AlertId.Length > MaximumIdLength
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.MuteAlerts:
                var mute = command.MuteAlerts;
                return mute.RuleId.Length > MaximumIdLength || mute.DurationSeconds == 0 ||
                    TimeSpan.FromSeconds(mute.DurationSeconds) > MaximumMute
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.UnmuteAlerts:
                return command.UnmuteAlerts.RuleId.Length > MaximumIdLength ? "invalid_payload" : null;
            case ControlCommand.PayloadOneofCase.SetAlertPreferences:
                var prefs = command.SetAlertPreferences;
                return prefs.MinSeverity is < AlertSeverity.Unspecified or > AlertSeverity.Critical ||
                    prefs.MutedRuleKinds.Count > MaximumMutedRuleKinds ||
                    prefs.MutedRuleKinds.Any(kind => string.IsNullOrWhiteSpace(kind) || kind.Length > MaximumIdLength)
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.SetAlertRuleOverrides:
                var rules = command.SetAlertRuleOverrides;
                return rules.SubjectPeerId.Length > MaximumIdLength ||
                    rules.Overrides.Any(item => item.RuleId.Length > MaximumIdLength) ||
                    AlertRuleCatalog.Validate(rules.Overrides
                        .Select(item => new AlertRuleOverrideInput(item.RuleId, item.Enabled, item.HasThreshold ? item.Threshold : null))
                        .ToArray()) is not null
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.ResolvePairing:
                return string.IsNullOrWhiteSpace(command.ResolvePairing.PairingId) ||
                    command.ResolvePairing.PairingId.Length > MaximumIdLength ||
                    command.ResolvePairing.RemotePublicKeySha256.Length != 32
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.EnrollPresenceKey:
                return command.EnrollPresenceKey.PresencePublicKeySpki.Length is 0 or > 256 ||
                    command.EnrollPresenceKey.ProofOfPossession.Length is 0 or > 128
                    ? "invalid_payload"
                    : null;
            case ControlCommand.PayloadOneofCase.ApproveUpdate:
                return string.IsNullOrWhiteSpace(command.ApproveUpdate.Version) ||
                    command.ApproveUpdate.Version.Length > MaximumVersionLength ||
                    command.ApproveUpdate.ArtifactSha256.Length != 32
                    ? "invalid_payload"
                    : null;
            default:
                return "invalid_command";
        }
    }
}
