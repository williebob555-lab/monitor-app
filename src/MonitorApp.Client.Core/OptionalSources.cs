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
