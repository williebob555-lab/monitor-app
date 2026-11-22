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
/// Dispose to stop. Sources without an adaptive-sampling channel do not implement it. The source keeps the interest
/// alive (renewing its lease) until the returned scope is disposed. <paramref name="signalIds"/> limits fast sampling to
/// those signals; null or empty means all. The desktop HTTP client has no such channel yet and does not implement this.
/// </summary>
public interface IViewingSource
{
    IDisposable BeginViewing(string? peerId, IReadOnlyCollection<string>? signalIds = null);
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
/// Sources that can pair by scanning the QR code on a computer's admin page with the device camera.
/// The UI shows a "Scan QR" button only when <see cref="CanScanQr"/> is true and keeps the typed-code path otherwise.
/// </summary>
public interface IQrPairingSource
{
    /// <summary>True when a camera scanner is available on this device.</summary>
    bool CanScanQr { get; }

    /// <summary>
    /// Scans a pairing QR and pairs with the desktop it names. Returns the peer id, or <see langword="null"/> when the
    /// person cancelled the scanner. Throws <see cref="ApiValidationException"/> with a user-safe message when the camera is
    /// unavailable or denied (the UI should then point at the typed code) or the scanned code is not a valid invitation.
    /// </summary>
    Task<string?> ScanAndPairAsync(CancellationToken ct = default);
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
    SetAlertPreferences,
    ApprovePairing,
    RejectPairing,
    EnrollPresenceKey,
    ApproveUpdate,
}

/// <summary>
/// A paired computer's update state as the phone may see it (T044). <see cref="Permitted"/> is false until the owner grants this
/// device update approval on that computer; every other field is then empty. <see cref="StagedDigestHex"/> binds an approval to
/// exactly the verified package the computer staged.
/// </summary>
public sealed record RemoteUpdateStatusDto(
    bool Permitted,
    bool Available,
    string InstalledVersion,
    string? StagedVersion,
    string? StagedDigestHex,
    string State,
    string? LastError,
    bool ApprovalPending);

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

    /// <summary>
    /// Sets this device's own alert preferences on the peer that owns the alerts (T049): <paramref name="minSeverity"/> is "All"
    /// (or null), "Info", "Warning" or "Critical"; <paramref name="mutedRuleKinds"/> are rule ids to opt out of. Replaces the previous
    /// preferences. Only this device's preferences can be changed.
    /// </summary>
    Task<ControlOutcomeDto> SetAlertPreferencesAsync(
        string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default);

    /// <summary>Pairing requests the peer holds for approval; empty when it does not let this device approve pairings or is unreachable.</summary>
    Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default);

    /// <summary><paramref name="fingerprint"/> is the hex SHA-256 the person saw (<see cref="PendingPairingDto.FingerprintSha256"/>).</summary>
    Task<ControlOutcomeDto> ResolvePairingAsync(string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default);

    Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default);

    /// <summary>Reads a paired computer's update state; null when it cannot be reached.</summary>
    Task<RemoteUpdateStatusDto?> ReadUpdateStatusAsync(string peerId, CancellationToken ct = default);

    /// <summary>Approves installing the staged update read by <see cref="ReadUpdateStatusAsync"/>. High tier: needs the fingerprint.</summary>
    Task<ControlOutcomeDto> ApproveUpdateAsync(string peerId, string stagedVersion, string stagedDigestHex, CancellationToken ct = default);
}

/// <summary>
/// Optional: sources talking to this computer's node that can manage desktop fingerprint (local presence) and phone
/// control grants. The in-process phone runtime does not implement it, and the UI hides these controls there.
/// </summary>
public interface ISecuritySource
{
    Task<LocalPresenceStatusDto> GetLocalPresenceStatusAsync(CancellationToken ct = default);

    Task<LocalPresenceStatusDto> EnrollLocalPresenceAsync(CancellationToken ct = default);

    Task<LocalPresenceStatusDto> RemoveLocalPresenceAsync(CancellationToken ct = default);

    /// <summary>Sets what a paired phone may do on this computer. Granting needs fingerprint verification when enforced.</summary>
    Task SetControlGrantAsync(string peerId, bool monitoringControls, bool pairingApproval, bool updateApproval, CancellationToken ct = default);

    Task ApprovePhonePresenceEnrollmentAsync(string enrollmentId, CancellationToken ct = default);

    /// <summary>The stored grants for phones that have one; a paired phone absent from the list has none (denied everything).</summary>
    Task<IReadOnlyList<ControlGrantDto>> GetControlGrantsAsync(CancellationToken ct = default);

    /// <summary>Phone fingerprint-key enrollments waiting for this computer's owner to approve.</summary>
    Task<IReadOnlyList<PresenceEnrollmentDto>> ListPendingPresenceEnrollmentsAsync(CancellationToken ct = default);
}
