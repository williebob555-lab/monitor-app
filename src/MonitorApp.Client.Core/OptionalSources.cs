namespace MonitorApp.Client.Core;

// Optional capabilities of a data source. A view that needs one checks `Client is IAlertSource` and degrades with a
// plain message otherwise, so IPeerDataSource itself stays small and every head can implement what it really has.

/// <summary>Alerts a peer holds: the desktop node's active/recent alerts, or the phone's received inbox.</summary>
public interface IAlertSource
{
    Task<IReadOnlyList<AlertDto>> GetAlertsAsync(int limit = 100, CancellationToken ct = default);
}

/// <summary>
/// Declares that the UI is looking at live data for a peer (null = this device), so the peer can sample faster.
/// Dispose to stop. Sources without an adaptive-sampling channel do not implement it.
/// </summary>
public interface IViewingSource
{
    IDisposable BeginViewing(string? peerId);
}

/// <summary>Sources that can start pairing with another peer (a phone pairing with a desktop).</summary>
public interface IPairingInitiator
{
    /// <summary>Pairs using the desktop's private "host:port" address and the one-time code it displays. Returns the peer id.</summary>
    Task<string> PairWithCodeAsync(string address, string code, CancellationToken ct = default);

    /// <summary>Pairs using a pasted <c>monitor-pair1.</c> invitation. Returns the peer id.</summary>
    Task<string> PairWithInvitationAsync(string invitation, CancellationToken ct = default);
}

/// <summary>
/// Sources whose host process can be asked to start or stop background collection (the Android foreground service).
/// Heads that cannot do this do not implement it and the UI offers no buttons.
/// </summary>
public interface IHostServiceControl
{
    void RequestStart();

    void RequestStop();
}

/// <summary>The monitoring controls a client can ask a paired peer to perform. Monitoring only: no power, service or script actions.</summary>
public enum ControlActionKind
{
    PauseCollection,
    ResumeCollection,
    SetSamplingInterval,
    AcknowledgeAlert,
    MuteAlerts,
    UnmuteAlerts,
    ApprovePairing,
    RejectPairing,
    EnrollPresenceKey,
}

/// <summary>How much assurance a command needs beyond the signed session (client-side mirror of the peer's risk tiers).</summary>
public enum ControlTierDto
{
    Normal = 1,
    Elevated = 2,
    High = 3,
}

/// <summary>What a UI should show before sending: the tier and whether the person will be prompted for biometric verification.</summary>
/// <param name="Tier">The command's risk tier.</param>
/// <param name="RequiresPresence">True when sending triggers a biometric (user-presence) prompt.</param>
/// <param name="PresenceAvailable">True when this device can satisfy that prompt; false means the command will be refused locally.</param>
public sealed record ControlTierDescription(ControlTierDto Tier, bool RequiresPresence, bool PresenceAvailable);

/// <summary>
/// The peer's answer to a control command. A refusal is a normal outcome, not an exception.
/// Status is one of "Applied", "Refused", "PendingLocalApproval" or "Failed" (could not be delivered or signed locally).
/// </summary>
public sealed record ControlOutcomeDto(string Status, string ReasonCode, string Message, bool RequiresPresence)
{
    public bool IsApplied => Status == "Applied";
}

/// <summary>
/// Optional: sources that can send monitoring controls to a paired peer. Every peer denies control until its owner
/// grants this device on that computer, so a "not_permitted" outcome is the expected default. Methods never throw for
/// refusals or an unreachable peer; they return a <see cref="ControlOutcomeDto"/>.
/// </summary>
public interface IControlSource
{
    /// <summary>True when this device can show a biometric prompt (needed for elevated and high commands).</summary>
    bool CanVerifyUserPresence { get; }

    /// <summary>Tells the UI, before sending, the risk tier of an action and whether it will prompt for biometrics.</summary>
    ControlTierDescription DescribeTier(ControlActionKind action, TimeSpan? duration = null);

    Task<ControlOutcomeDto> PauseCollectionAsync(string peerId, TimeSpan? duration, CancellationToken ct = default);

    Task<ControlOutcomeDto> ResumeCollectionAsync(string peerId, CancellationToken ct = default);

    Task<ControlOutcomeDto> SetSamplingIntervalAsync(string peerId, TimeSpan? interval, TimeSpan? duration, CancellationToken ct = default);

    Task<ControlOutcomeDto> AcknowledgeAlertAsync(string peerId, string alertId, CancellationToken ct = default);

    Task<ControlOutcomeDto> MuteAlertsAsync(string peerId, TimeSpan duration, string? ruleId, CancellationToken ct = default);

    Task<ControlOutcomeDto> UnmuteAlertsAsync(string peerId, string? ruleId, CancellationToken ct = default);

    /// <summary>Pairing requests the peer holds for approval; empty when it does not let this device approve pairings or is unreachable.</summary>
    Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default);

    /// <summary><paramref name="fingerprint"/> is the hex SHA-256 the person saw (<see cref="PendingPairingDto.FingerprintSha256"/>).</summary>
    Task<ControlOutcomeDto> ResolvePairingAsync(string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default);

    Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default);
}
