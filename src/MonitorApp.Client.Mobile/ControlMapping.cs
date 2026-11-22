using MonitorApp.Client.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Control;

namespace MonitorApp.Client.Mobile;

/// <summary>Pure mapping between the peer's control policy/results and the client's DTOs, so Client.Core stays free of Peer.Control.</summary>
internal static class ControlMapping
{
    public static ControlTierDescription Describe(ControlActionKind action, TimeSpan? duration, bool presenceAvailable)
    {
        // Mirror the real policy by classifying the same request the source would send, not a re-implemented rule table.
        var request = action switch
        {
            ControlActionKind.PauseCollection => ControlRequest.PauseCollection(duration),
            ControlActionKind.ResumeCollection => ControlRequest.ResumeCollection(),
            ControlActionKind.SetSamplingInterval => ControlRequest.SetSamplingInterval(null),
            ControlActionKind.AcknowledgeAlert => ControlRequest.AcknowledgeAlert("alert"),
            ControlActionKind.MuteAlerts => ControlRequest.MuteAlerts(
                duration is { } span && span > TimeSpan.Zero ? span : ControlPolicy.MaximumMute),
            ControlActionKind.UnmuteAlerts => ControlRequest.UnmuteAlerts(),
            ControlActionKind.ApprovePairing => ControlRequest.ResolvePairing("pairing", new byte[32], approve: true),
            ControlActionKind.RejectPairing => ControlRequest.ResolvePairing("pairing", new byte[32], approve: false),
            ControlActionKind.EnrollPresenceKey => ControlRequest.EnrollPresenceKey(),
            ControlActionKind.ApproveUpdate => ControlRequest.ApproveUpdate("0.0.0", new byte[32]),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var tier = (ControlTierDto)(int)ControlCommandSigner.TierOf(request);
        return new ControlTierDescription(tier, tier >= ControlTierDto.Elevated, presenceAvailable);
    }

    public static ControlOutcomeDto ToOutcome(ControlResult result, bool requiresPresence) => new(
        result.Status switch
        {
            ControlStatus.Applied => "Applied",
            ControlStatus.PendingLocalApproval => "PendingLocalApproval",
            _ => "Refused",
        },
        result.ReasonCode,
        result.SafeMessage,
        requiresPresence);
}
