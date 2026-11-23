namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// Whether paired phones can reach this computer. Off: the peer listener is on 127.0.0.1 and only this computer can use it.
/// On: the listener moves to this computer's Tailscale address (private, encrypted by Tailscale; never a public or wildcard
/// address). Changing it restarts the node.
/// </summary>
public sealed record PeerReachStatus(
    bool ReachableByPhone,
    string ListeningOn,
    string? TailscaleAddress,
    string ConfiguredBy,
    bool CanChange,
    bool RestartPending,
    string Explanation);

public sealed record PeerReachChange(bool Accepted, string? Error, PeerReachStatus Status);

/// <summary>Implemented by the node, which owns its bind address and can restart itself.</summary>
public interface IPeerReachController
{
    PeerReachStatus GetStatus();

    /// <summary>Saves the choice and schedules a restart so the peer listener moves. Refuses when no Tailscale address exists.</summary>
    PeerReachChange Change(bool reachableByPhone);
}

/// <summary>The current phone invitation for an app to draw: the QR as rows of '1' (dark) and '0' (light) modules.</summary>
public sealed record PhoneInvitationView(
    string Address, int Port, string ShortCode, string Payload, DateTimeOffset ExpiresAt, IReadOnlyList<string> QrRows);

public sealed record PhonePairingView(bool Configured, IReadOnlyList<string> Addresses, PhoneInvitationView? Invitation);
