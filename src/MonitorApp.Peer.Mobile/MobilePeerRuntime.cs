using System.Security.Cryptography;
using Grpc.Core;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Sync;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Peer.Mobile;

public sealed record MobilePeerOptions(
    string DataDirectory,
    string DisplayName,
    TimeSpan CollectionInterval,
    TimeSpan SyncInterval,
    TimeSpan MaximumSyncBackoff,
    TimeSpan? HeartbeatInterval = null,
    LiveInterestPolicy? InterestPolicy = null,
    TimeSpan? RelayPullInterval = null)
{
    public static MobilePeerOptions Default(string dataDirectory, string displayName) => new(
        dataDirectory, displayName, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5));

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
        if (CollectionInterval <= TimeSpan.Zero || SyncInterval <= TimeSpan.Zero || MaximumSyncBackoff < SyncInterval)
            throw new ArgumentOutOfRangeException(nameof(MobilePeerOptions));
    }
}

public enum MobileSyncState
{
    NeverSynced = 1,
    Synced = 2,
    Offline = 3,
    Refused = 4,
}

public sealed record MobilePeerSyncStatus(
    string PeerId,
    MobileSyncState State,
    DateTimeOffset? LastSuccess,
    string? LastError);

public sealed record MobileCollectionStatus(DateTimeOffset? LastObservedAt, int LastAppendedRecords, string? LastError);

public sealed record MobileStatus(
    string PeerId,
    string FingerprintHex,
    bool Running,
    string? PendingPairingId,
    MobileCollectionStatus Collection,
    IReadOnlyList<MobilePeerSyncStatus> Peers);

public sealed class MobilePairingFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A control command or query could not be delivered (not refused: a refusal is a <see cref="ControlResult"/>).</summary>
public sealed class MobileControlFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Portable, lifecycle-agnostic phone peer: local store, signed origin collection, explicit outbound pairing, and a
/// phone-initiated sync loop. It never listens; replication in both directions rides the sessions it dials.
/// The host (an Android foreground service) only calls <see cref="Start"/> and <see cref="StopAsync"/>.
/// </summary>
public sealed partial class MobilePeerRuntime : IAsyncDisposable
{
    private readonly MobilePeerOptions options;
    private readonly ILocalPeerIdentityProvider identity;
    private readonly PeerStore store;
    private readonly OriginReplicationCoordinator replication;
    private readonly PeerSystemMetricsIngestionBridge collection;
    private readonly PhoneLanCollection lan;
    private readonly AlertStore alerts;
    private readonly LiveInterestRegistry interests;
    private readonly IUserPresenceSigner? presence;
    private readonly object gate = new();
    private TaskCompletionSource sessionWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, MobilePeerSyncStatus> peerStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> notBefore = new(StringComparer.Ordinal);
    private CancellationTokenSource? loopCancellation;
    private Task[] loops = [];
    private string? pendingPairingId;
    private long lastFullCollection = Environment.TickCount64;
    private MobileCollectionStatus collectionStatus = new(null, 0, null);

    private MobilePeerRuntime(
        MobilePeerOptions options,
        ILocalPeerIdentityProvider identity,
        PeerStore store,
        AlertStore alerts,
        PeerSystemMetricsIngestionBridge collection,
        PhoneLanCollection lan,
        IUserPresenceSigner? presence)
    {
        this.lan = lan;
        this.presence = presence;
        this.alerts = alerts;
        this.options = options;
        this.identity = identity;
        this.store = store;
        this.collection = collection;
        replication = new OriginReplicationCoordinator(store);
        Mesh = new MeshMembership(store, identity.PublicIdentity, identity.IdentityKey);
        Mesh.TrustChanged += _ => WakeSessions();
        interests = new LiveInterestRegistry(options.InterestPolicy);
        PeerStates = new PeerStateTracker(store, new SystemPeerClock(), TimeSpan.FromMinutes(5), peerId =>
        {
            lock (gate)
            {
                return peerStatus.TryGetValue(peerId, out var status) && status.State == MobileSyncState.Synced;
            }
        });
    }

    /// <summary>Mesh membership (T080): devices any member admitted are trusted here too; removals spread.</summary>
    public MeshMembership Mesh { get; }

    /// <summary>The phone's own record of when each paired desktop became reachable or unreachable.</summary>
    public PeerStateTracker PeerStates { get; }

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public static async Task<MobilePeerRuntime> CreateAsync(
        MobilePeerOptions options,
        ILocalPeerIdentityProvider identity,
        IMobileDeviceReader reader,
        IUserPresenceSigner? presence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(reader);
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(options.DataDirectory, "peer.db")));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var alertStore = new AlertStore(AlertStoreOptions.Create(Path.Combine(options.DataDirectory, "peer.db")));
        await alertStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await MobilePeerIdentity.EnsureRegisteredAsync(store, identity, cancellationToken).ConfigureAwait(false);
        await new PeerCapabilityRegistry(store).RegisterAsync(AndroidCapabilityDeclaration.Create(), cancellationToken)
            .ConfigureAwait(false);
        var recordingReader = new RecordingDeviceReader(reader);
        var bridge = new PeerSystemMetricsIngestionBridge(
            store,
            identity,
            new AndroidDeviceSnapshotCollector(recordingReader),
            new PeerCollectionOptions(
                $"peer:{identity.PublicIdentity.PeerId}",
                AndroidDeviceSnapshotCollector.ModuleId,
                Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(30),
                256));
        var lan = PhoneLanCollection.Create(store, identity, recordingReader);
        return new MobilePeerRuntime(options, identity, store, alertStore, bridge, lan, presence);
    }

    public PeerStore Store => store;

    /// <summary>Where this phone keeps its databases (peer.db, model.db).</summary>
    public string DataDirectory => options.DataDirectory;

    /// <summary>The name this phone pairs under.</summary>
    public string DisplayName => options.DisplayName;

    /// <summary>Exception type of the last failed phone-transport LAN run (null after a success); it never fails ordinary collection.</summary>
    public string? LastLanError => lan.LastError;

    /// <summary>
    /// Newly stored observations (own readings and replicated desktop data) as they commit, for the app UI.
    /// </summary>
    public IAsyncEnumerable<StoredOriginRecord> LiveObservations(CancellationToken cancellationToken = default) =>
        store.Live.StreamAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// The UI is viewing live data: for a paired desktop peer id the held-open session declares and renews interest so
    /// that desktop samples fast; with no id the phone itself samples fast. Dispose to stop viewing.
    /// </summary>
    public IDisposable BeginViewing(string? remotePeerId = null, IReadOnlyCollection<string>? signalIds = null) =>
        remotePeerId is null
            ? interests.WatchLocal(identity.PublicIdentity.PeerId, signalIds)
            : interests.WatchRemote(remotePeerId, signalIds);

    /// <summary>The phone's current collection interval (fast while a desktop is viewing it).</summary>
    public TimeSpan CurrentCollectionInterval =>
        interests.EffectiveInterval(identity.PublicIdentity.PeerId, options.CollectionInterval);

    /// <summary>
    /// Raised after a sync (even a partially failed one) when stored alerts still need a user notification.
    /// The host posts the notification, then calls <see cref="MarkAlertNotifiedAsync"/>; unmarked alerts are
    /// raised again after the next sync or restart, so a missed notification is never lost.
    /// </summary>
    public event Action<IReadOnlyList<InboxAlert>>? NewAlerts;

    /// <summary>Alerts persisted on this phone, active first then most recent; bounded.</summary>
    public async Task<IReadOnlyList<InboxAlert>> ReadAlertsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        // Every computer announces a newly joined device; show it once.
        MeshJoinAlerts.Collapse(await alerts.ReadInboxAsync(limit, false, cancellationToken).ConfigureAwait(false), identity.PublicIdentity.PeerId);

    public Task<IReadOnlyList<InboxAlert>> ReadPendingNotificationsAsync(CancellationToken cancellationToken = default) =>
        alerts.ReadInboxAsync(100, true, cancellationToken);

    public Task MarkAlertNotifiedAsync(InboxAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alerts.MarkNotifiedAsync(alert.Alert.AlertId, alert.Alert.Revision, cancellationToken);
    }

    public MobileStatus GetStatus()
    {
        lock (gate)
        {
            return new MobileStatus(
                identity.PublicIdentity.PeerId,
                Convert.ToHexString(identity.PublicIdentity.PublicKeyFingerprint),
                loopCancellation is not null,
                pendingPairingId,
                collectionStatus,
                peerStatus.Values.OrderBy(item => item.PeerId, StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    /// Explicit user-initiated pairing with a desktop peer. Blocks until the desktop user approves (bounded polls)
    /// and pins the desktop identity from the invitation; persists bilateral trust and the desktop locator.
    /// </summary>
    public async Task<string> PairAsync(
        PeerPairingInvitation invitation,
        string? pairingId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (invitation.IsExpired(DateTimeOffset.UtcNow))
        {
            throw new MobilePairingFailedException("The pairing invitation has expired.");
        }

        var id = pairingId ?? Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (pendingPairingId is not null)
            {
                throw new MobilePairingFailedException("Another pairing is already in progress.");
            }

            pendingPairingId = id;
        }

        try
        {
            var pairing = new P256PeerInitialPairingCoordinator(
                identity.PublicIdentity,
                identity.IdentityKey,
                options.DisplayName,
                new InitiatingUserApproval(),
                new StoreTrustSink(store));
            await using var client = new PeerPairingClient(
                PeerTransportOptions.Create(invitation.ToEndpoint()),
                pairing,
                PeerInitialPairingOptions.UntilRequestExpires);
            DiagnosticLog.Write("pairing", $"asking {invitation.Address}:{invitation.Port} to add this device ({id})");
            var result = await client.PairAsync(
                new PeerInitialPairingRequest(id, InitiatingUserApproved: true, invitation.FingerprintSha256),
                cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Write("pairing", $"paired with {result.Remote.DisplayName} ({result.Remote.Identity.PeerId})");
            await store.UpsertPeerLocatorAsync(
                new PeerLocator(result.Remote.Identity.PeerId, invitation.Address.ToString(), invitation.Port,
                    DateTimeOffset.UtcNow, null),
                cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                peerStatus[result.Remote.Identity.PeerId] =
                    new MobilePeerSyncStatus(result.Remote.Identity.PeerId, MobileSyncState.NeverSynced, null, null);
                failures.Remove(result.Remote.Identity.PeerId);
                notBefore.Remove(result.Remote.Identity.PeerId);
            }

            // Start syncing with the new device now rather than at the next pass (Wilson saw a minute's stall).
            WakeSessions();
            return result.Remote.Identity.PeerId;
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or TimeoutException or PeerPersistenceException or InvalidOperationException or FormatException)
        {
            DiagnosticLog.Write("pairing", $"pairing {id} did not complete", exception);
            throw new MobilePairingFailedException(
                exception is TimeoutException
                    ? "Nobody tapped Accept on the computer within 5 minutes. Scan the code again and tap Accept there."
                    : "Pairing did not complete. Verify the address, desktop identity, and desktop approval.", exception);
        }
        finally
        {
            lock (gate)
            {
                pendingPairingId = null;
            }
        }
    }

    /// <summary>
    /// Typed-code fallback: the user enters the desktop's private address, port, and the one-time code shown on the
    /// desktop. The code only locates and authenticates the desktop identity (see <see cref="PeerPairingCodeClient"/>);
    /// the desktop user must still approve the resulting pairing request.
    /// </summary>
    public async Task<string> PairWithCodeAsync(
        System.Net.IPAddress address,
        int port,
        string typedCode,
        string? pairingId = null,
        CancellationToken cancellationToken = default)
    {
        PeerPairingInvitation invitation;
        try
        {
            invitation = await PeerPairingCodeClient.RedeemAsync(address, port, typedCode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or TimeoutException or InvalidOperationException or FormatException)
        {
            throw new MobilePairingFailedException(
                "The pairing code could not be verified. Check the address and code, and ask for a fresh code if it expired.",
                exception);
        }

        return await PairAsync(invitation, pairingId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops trusting a paired peer on this phone; the next sync will not dial it.</summary>
    /// <summary>True when this phone can produce biometric (user-presence) signatures for high-risk commands.</summary>
    public bool CanVerifyUserPresence => presence?.IsAvailable == true;

    /// <summary>How removing a device will be confirmed here, in words for the screen.</summary>
    public string RemovalCheck => presence switch
    {
        { IsAvailable: true } => "Removing asks for your fingerprint.",
        IDeviceLockConfirmation { CanConfirmWithDeviceLock: true } =>
            "No fingerprint is set up on this phone, so removing asks for your screen lock (PIN, pattern or password) instead. " +
            "To use a fingerprint, add one in Android Settings > Security.",
        _ => "This phone has no fingerprint or screen lock, so removing is not double-checked. Set a screen lock in Android Settings > Security.",
    };

    /// <summary>
    /// Hex SHA-256 of this phone's presence public key (what the desktop administrator compares when approving the
    /// enrollment), or null when the device has no biometric signer.
    /// </summary>
    public async Task<string?> GetPresenceKeyFingerprintAsync(CancellationToken cancellationToken = default)
    {
        if (presence is not { IsAvailable: true })
        {
            return null;
        }

        var spki = await presence.GetPublicKeySpkiAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(spki));
    }

    /// <summary>
    /// Sends one monitoring control to a paired desktop over a short authenticated session and returns the desktop's
    /// durable result (applied, refused with a reason, or pending local approval). Commands needing biometric
    /// verification prompt the person first (<see cref="UserPresenceDeniedException"/> when declined). Reusing a
    /// <paramref name="commandId"/> is an idempotent retry: the desktop returns the stored result.
    /// </summary>
    public async Task<ControlResult> SendControlAsync(
        string targetPeerId,
        ControlRequest request,
        string? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        ArgumentNullException.ThrowIfNull(request);
        var signer = new ControlCommandSigner(identity.PublicIdentity, identity.IdentityKey, presence);
        try
        {
            await using var connector = await ConnectAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
            return await connector.SendControlAsync(
                (sessionId, token) => signer.CreateAsync(targetPeerId, sessionId, request, commandId, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or PeerSyncException)
        {
            throw new MobileControlFailedException("The desktop could not be reached to deliver the command.", exception);
        }
    }

    /// <summary>
    /// Pairing requests the desktop is holding for approval (read-only: name and fingerprint to show the user).
    /// Empty when the desktop does not let this phone approve pairings.
    /// </summary>
    public async Task<IReadOnlyList<PendingPairingSummary>> ReadPendingPairingsAsync(
        string targetPeerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        try
        {
            await using var connector = await ConnectAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
            return (await connector.ReadPendingPairingsAsync(cancellationToken).ConfigureAwait(false)).Pairings.ToArray();
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or PeerSyncException)
        {
            throw new MobileControlFailedException("The desktop could not be reached.", exception);
        }
    }

    /// <summary>
    /// This phone's own alert preferences on the desktop. Read-only; the response has <c>Permitted = false</c> and nothing
    /// else when the desktop has not granted this phone monitoring controls.
    /// </summary>
    public async Task<AlertPreferencesResponse> ReadAlertPreferencesAsync(
        string targetPeerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        try
        {
            await using var connector = await ConnectAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
            return await connector.ReadAlertPreferencesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or PeerSyncException)
        {
            throw new MobileControlFailedException("The desktop could not be reached.", exception);
        }
    }

    /// <summary>
    /// The desktop's effective alert rules (defaults plus overrides). Read-only; the response has <c>Permitted = false</c> and
    /// nothing else when the desktop has not granted this phone monitoring controls.
    /// </summary>
    public async Task<AlertRulesResponse> ReadAlertRulesAsync(
        string targetPeerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        try
        {
            await using var connector = await ConnectAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
            return await connector.ReadAlertRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or PeerSyncException)
        {
            throw new MobileControlFailedException("The desktop could not be reached.", exception);
        }
    }

    /// <summary>
    /// The desktop's update state (installed version, staged version and digest, state, last error). Read-only; the response
    /// has <c>Permitted = false</c> and nothing else when the desktop has not granted this phone update approval.
    /// </summary>
    public async Task<UpdateStatusResponse> ReadUpdateStatusAsync(
        string targetPeerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPeerId);
        try
        {
            await using var connector = await ConnectAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
            return await connector.ReadUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or PeerSyncException)
        {
            throw new MobileControlFailedException("The desktop could not be reached.", exception);
        }
    }

    /// <summary>
    /// Approves installing the update a desktop has staged, bound to the exact version and digest from
    /// <paramref name="status"/> (a prior <see cref="ReadUpdateStatusAsync"/>). Needs a biometric confirmation; the desktop
    /// refuses when its staged update has changed since the status was read.
    /// </summary>
    public Task<ControlResult> ApproveUpdateAsync(
        string targetPeerId,
        UpdateStatusResponse status,
        string? commandId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.StagedVersion.Length == 0 || status.StagedArtifactSha256.Length != 32)
        {
            throw new MobileControlFailedException("The desktop has no staged update to approve.");
        }

        return SendControlAsync(
            targetPeerId,
            ControlRequest.ApproveUpdate(status.StagedVersion, status.StagedArtifactSha256.ToByteArray()),
            commandId,
            cancellationToken);
    }

    private async Task<PeerGrpcConnector> ConnectAsync(string targetPeerId, CancellationToken cancellationToken)
    {
        var peer = await store.ReadTrustedPeerAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
        if (peer is null || peer.TrustState != PeerTrustState.Active)
        {
            throw new MobileControlFailedException("That desktop is not an actively trusted peer.");
        }

        var locators = await store.ReadPeerLocatorsAsync(targetPeerId, cancellationToken).ConfigureAwait(false);
        if (locators.Count == 0 || !System.Net.IPAddress.TryParse(locators[0].Host, out var address))
        {
            throw new MobileControlFailedException("No usable address is known for that desktop.");
        }

        var endpoint = new PeerPairingInvitation(address, locators[0].Port, peer.PeerId, peer.PublicKeyFingerprint, null).ToEndpoint();
        var remote = new PeerPublicIdentity(peer.PeerId, peer.PublicKeySpki, peer.PublicKeyFingerprint);
        return new PeerGrpcConnector(
            PeerTransportOptions.Create(endpoint),
            new P256PeerSessionAuthenticationProvider(identity.PublicIdentity, remote, identity.IdentityKey),
            new PeerSyncApplication(store, replication, mesh: Mesh));
    }

    /// <summary>Revokes a device here and, through the mesh, on every other member.</summary>
    public async Task RevokeAsync(string peerId, CancellationToken cancellationToken = default)
    {
        // Removing a device removes it from every device in the mesh: ask for the fingerprint when one is set up, as
        // computers do, and otherwise for the screen lock. With neither, removal still works, so a lost device can
        // always be removed; the screen says beforehand which check will be used (RemovalCheck).
        var peer = await store.ReadTrustedPeerAsync(peerId, cancellationToken).ConfigureAwait(false);
        var name = string.IsNullOrWhiteSpace(peer?.DisplayName) ? "this device" : peer.DisplayName;
        var challenge = new UserPresenceChallenge($"Remove {name}", $"{name} will be removed from all your devices.");
        if (presence is { IsAvailable: true } signer)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(
                $"monitor-mesh-local-removal-v1|{identity.PublicIdentity.PeerId}|{peerId}|{Guid.NewGuid():N}");
            await signer.SignAsync(payload, challenge, cancellationToken).ConfigureAwait(false);
        }
        else if (presence is IDeviceLockConfirmation { CanConfirmWithDeviceLock: true } deviceLock)
        {
            await deviceLock.ConfirmWithDeviceLockAsync(challenge, cancellationToken).ConfigureAwait(false);
        }

        DiagnosticLog.Write("removal", $"removing {name} ({peerId})");
        if (peer is { TrustState: PeerTrustState.Active })
        {
            await store.RevokeTrustedPeerAsync(peerId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        await Mesh.RefreshAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticLog.Write("removal", $"removed {name} ({peerId}) here; other devices learn it when they next connect");
    }

    /// <summary>One full collection (every signal plus the LAN model), as at the normal interval.</summary>
    public Task<PeerCollectionRunResult> CollectOnceAsync(CancellationToken cancellationToken = default) =>
        CollectAsync(null, cancellationToken);

    private async Task<PeerCollectionRunResult> CollectAsync(
        IReadOnlySet<string>? signalFilter, CancellationToken cancellationToken)
    {
        try
        {
            var result = await collection.RunOnceAsync(signalFilter, cancellationToken).ConfigureAwait(false);
            if (signalFilter is null)
            {
                // The LAN model is not signal-addressed, so it rides on full collections only.
                Interlocked.Exchange(ref lastFullCollection, Environment.TickCount64);
                await lan.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (gate)
            {
                collectionStatus = new MobileCollectionStatus(result.ObservedAt ?? collectionStatus.LastObservedAt,
                    result.AppendedRecords, null);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (gate)
            {
                collectionStatus = collectionStatus with { LastError = exception.GetType().Name };
            }

            throw;
        }
    }

    /// <summary>One sync pass over every active trusted peer that has a locator. Never throws for peer failures.</summary>
    public async Task<IReadOnlyList<MobilePeerSyncStatus>> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in peers.Where(item => item.TrustState == PeerTrustState.Active))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locators = await store.ReadPeerLocatorsAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
            if (locators.Count == 0)
            {
                continue;
            }

            lock (gate)
            {
                if (notBefore.TryGetValue(peer.PeerId, out var until) && DateTimeOffset.UtcNow < until)
                {
                    continue;
                }
            }

            foreach (var locator in locators)
            {
                if (await SyncPeerAsync(peer, locator, cancellationToken).ConfigureAwait(false)) break;
            }
        }

        lock (gate)
        {
            return peerStatus.Values.OrderBy(item => item.PeerId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Starts the collection and sync loops. Idempotent.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (loopCancellation is not null)
            {
                return;
            }

            loopCancellation = new CancellationTokenSource();
            var token = loopCancellation.Token;
            DiagnosticLog.Write("node", $"{options.DisplayName} started collecting and connecting");
            loops =
            [
                Task.Run(() => CollectionLoopAsync(token), CancellationToken.None),
                Task.Run(() => SuperviseSessionsAsync(token), CancellationToken.None),
            ];
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task[] running;
        lock (gate)
        {
            cancellation = loopCancellation;
            running = loops;
            loopCancellation = null;
            loops = [];
        }

        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task<bool> SyncPeerAsync(TrustedPeer peer, PeerLocator locator, CancellationToken cancellationToken)
    {
        try
        {
            if (!System.Net.IPAddress.TryParse(locator.Host, out var address))
            {
                throw new InvalidOperationException("A persisted peer locator is not an IP address.");
            }

            var endpoint = new PeerPairingInvitation(address, locator.Port, peer.PeerId, peer.PublicKeyFingerprint, null)
                .ToEndpoint();
            var remote = new PeerPublicIdentity(peer.PeerId, peer.PublicKeySpki, peer.PublicKeyFingerprint);
            await using var connector = new PeerGrpcConnector(
                PeerTransportOptions.Create(endpoint),
                new P256PeerSessionAuthenticationProvider(identity.PublicIdentity, remote, identity.IdentityKey),
                new PeerSyncApplication(store, replication, alerts: alerts, mesh: Mesh));
            try
            {
                await connector.SyncOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RaiseNewAlertsAsync().ConfigureAwait(false);
            }

            lock (gate)
            {
                failures.Remove(peer.PeerId);
                notBefore.Remove(peer.PeerId);
                peerStatus[peer.PeerId] = new MobilePeerSyncStatus(peer.PeerId, MobileSyncState.Synced, DateTimeOffset.UtcNow, null);
                PeerStates.NotifySession(peer.PeerId, true);
            }

            await MarkConnectedAsync(peer.PeerId, locator).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or InvalidOperationException or PeerSyncException or OperationCanceledException)
        {
            RecordFailure(peer.PeerId, exception);
            return false;
        }
    }

    private async Task RaiseNewAlertsAsync()
    {
        var handler = NewAlerts;
        if (handler is null)
        {
            return;
        }

        var pending = await alerts.ReadInboxAsync(100, true, CancellationToken.None).ConfigureAwait(false);
        if (pending.Any(item => item.Alert.RuleId == MeshJoinAlerts.RuleId))
        {
            // Notify a newly joined device once, however many computers announce it.
            var shown = MeshJoinAlerts.Collapse(await alerts.ReadInboxAsync(500, false, CancellationToken.None).ConfigureAwait(false), identity.PublicIdentity.PeerId)
                .Select(item => item.Alert.AlertId).ToHashSet(StringComparer.Ordinal);
            foreach (var duplicate in pending.Where(item => item.Alert.RuleId == MeshJoinAlerts.RuleId && !shown.Contains(item.Alert.AlertId)))
                await alerts.MarkNotifiedAsync(duplicate.Alert.AlertId, duplicate.Alert.Revision, CancellationToken.None).ConfigureAwait(false);
            pending = pending.Where(item => item.Alert.RuleId != MeshJoinAlerts.RuleId || shown.Contains(item.Alert.AlertId)).ToArray();
        }

        if (pending.Count > 0)
        {
            handler(pending);
        }
    }

    private async Task CollectionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AdaptiveSampler.RunAsync(
                async token =>
                {
                    try
                    {
                        // Same rule as the desktop scheduler: viewers that named signals narrow the fast loop to
                        // them, but a full collection still runs once per normal interval.
                        var filter = interests.FastSignalFilter(identity.PublicIdentity.PeerId);
                        var fullDue = Environment.TickCount64 - Interlocked.Read(ref lastFullCollection) >=
                            options.CollectionInterval.TotalMilliseconds;
                        await CollectAsync(fullDue ? null : filter, token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // Recorded in status by CollectOnceAsync; the loop must outlive any single failure.
                    }
                },
                identity.PublicIdentity.PeerId,
                options.CollectionInterval,
                interests,
                collectImmediately: true,
                onInterval: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Keeps exactly one long-lived live session per active paired peer: collection data and the desktop's data stream
    /// both ways over the phone-initiated connection, and a revoked peer's session is dropped.
    /// </summary>
    private async Task SuperviseSessionsAsync(CancellationToken cancellationToken)
    {
        var running = new Dictionary<string, (CancellationTokenSource Cancellation, Task Session)>(StringComparer.Ordinal);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
                    var active = peers.Where(item => item.TrustState == PeerTrustState.Active)
                        .ToDictionary(item => item.PeerId, StringComparer.Ordinal);
                    foreach (var stale in running.Keys.Where(id => !active.ContainsKey(id)).ToArray())
                    {
                        await StopSessionAsync(running[stale]).ConfigureAwait(false);
                        running.Remove(stale);
                    }

                    foreach (var peer in active.Values)
                    {
                        if (running.TryGetValue(peer.PeerId, out var existing) && !existing.Session.IsCompleted)
                        {
                            continue;
                        }

                        existing.Cancellation?.Dispose();
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        running[peer.PeerId] = (cancellation,
                            Task.Run(() => PeerSessionLoopAsync(peer.PeerId, cancellation.Token), CancellationToken.None));
                    }

                    await WaitForWakeAsync(options.SyncInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    DiagnosticLog.Write("link", "could not start connections", exception);
                    await Task.Delay(options.SyncInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var session in running.Values)
            {
                await StopSessionAsync(session).ConfigureAwait(false);
            }
        }
    }

    private static async Task StopSessionAsync((CancellationTokenSource Cancellation, Task Session) session)
    {
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await session.Session.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        session.Cancellation.Dispose();
    }

    private async Task PeerSessionLoopAsync(string peerId, CancellationToken cancellationToken)
    {
        var attempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var peer = await store.ReadTrustedPeerAsync(peerId, cancellationToken).ConfigureAwait(false);
                if (peer is null || peer.TrustState != PeerTrustState.Active)
                {
                    return;
                }

                var locators = await store.ReadPeerLocatorsAsync(peerId, cancellationToken).ConfigureAwait(false);
                TimeSpan wait;
                lock (gate)
                {
                    peerStatus.TryAdd(peerId, new MobilePeerSyncStatus(peerId, MobileSyncState.NeverSynced, null, null));
                    wait = notBefore.TryGetValue(peerId, out var until) && DateTimeOffset.UtcNow < until
                        ? until - DateTimeOffset.UtcNow
                        : TimeSpan.Zero;
                }

                if (locators.Count == 0 && attempt++ == 0)
                {
                    DiagnosticLog.Write("link", $"{peerId} ({peer.DisplayName}): no address known for it, so this device waits for it to connect");
                }

                if (wait <= TimeSpan.Zero && locators.Count > 0)
                {
                    // Best address first; one that never authenticates gives way to the next (T080 hotfix: an advertised
                    // address the phone cannot reach must not cut it off from a peer it reached before).
                    await RunLiveSessionAsync(peer, locators[attempt % locators.Count], locators.Count > 1, cancellationToken)
                        .ConfigureAwait(false);
                    attempt++;
                    lock (gate)
                    {
                        wait = notBefore.TryGetValue(peerId, out var until) && DateTimeOffset.UtcNow < until
                            ? until - DateTimeOffset.UtcNow
                            : options.SyncInterval;
                    }
                }
                else if (wait <= TimeSpan.Zero)
                {
                    wait = options.SyncInterval;
                }

                await WaitForWakeAsync(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The supervisor starts it again; say why it stopped instead of failing silently.
            DiagnosticLog.Write("link", $"{peerId} connection loop stopped", exception);
        }
    }

    /// <summary>Ends every session wait early: a device was just paired or the mesh changed who is trusted.</summary>
    private void WakeSessions()
    {
        TaskCompletionSource woken;
        lock (gate)
        {
            woken = sessionWake;
            sessionWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            failures.Clear();
            notBefore.Clear();
        }

        woken.TrySetResult();
    }

    private async Task WaitForWakeAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        Task woken;
        lock (gate)
        {
            woken = sessionWake.Task;
        }

        using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(woken, Task.Delay(wait, delay.Token)).ConfigureAwait(false);
        await delay.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>One held-open session (with transport-level reconnect) until cancellation or a permanent refusal.</summary>
    private async Task<bool> RunLiveSessionAsync(TrustedPeer peer, PeerLocator locator, bool hasAlternatives, CancellationToken cancellationToken)
    {
        var authenticated = false;
        var failuresSinceAuthentication = 0;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (!System.Net.IPAddress.TryParse(locator.Host, out var address))
            {
                throw new InvalidOperationException("A persisted peer locator is not an IP address.");
            }

            var endpoint = new PeerPairingInvitation(address, locator.Port, peer.PeerId, peer.PublicKeyFingerprint, null)
                .ToEndpoint();
            DiagnosticLog.Write("link", $"{peer.PeerId} ({peer.DisplayName}) connecting to {locator.Host}:{locator.Port}");
            var remote = new PeerPublicIdentity(peer.PeerId, peer.PublicKeySpki, peer.PublicKeyFingerprint);
            var transport = PeerTransportOptions.Create(endpoint) with
            {
                HeartbeatInterval = options.HeartbeatInterval ?? TimeSpan.FromSeconds(15),
                RelayPullInterval = options.RelayPullInterval ?? TimeSpan.FromSeconds(15),
                InitialReconnectDelay = options.SyncInterval,
                MaximumReconnectDelay = options.MaximumSyncBackoff,
            };
            await using var connector = new PeerGrpcConnector(
                transport,
                new P256PeerSessionAuthenticationProvider(identity.PublicIdentity, remote, identity.IdentityKey),
                new PeerSyncApplication(
                    store,
                    replication,
                    alerts: alerts,
                    live: new PeerSyncLiveOptions(interests, RequestRemotePush: true, AlertsStored: RaiseNewAlertsAsync),
                    mesh: Mesh));
            connector.StateChanged += state =>
            {
                if (state is PeerTransportState.Connecting or PeerTransportState.Authenticating)
                {
                    DiagnosticLog.Write("link", $"{peer.PeerId} ({peer.DisplayName}) {state.ToString().ToLowerInvariant()}");
                }

                if (state == PeerTransportState.Authenticated)
                {
                    failuresSinceAuthentication = 0;
                    if (!authenticated)
                    {
                        authenticated = true;
                        DiagnosticLog.Write("link", $"{peer.PeerId} ({peer.DisplayName}) connected via {locator.Host}:{locator.Port}");
                        _ = MarkConnectedAsync(peer.PeerId, locator);
                    }

                    lock (gate)
                    {
                        failures.Remove(peer.PeerId);
                        notBefore.Remove(peer.PeerId);
                        peerStatus[peer.PeerId] = new MobilePeerSyncStatus(
                            peer.PeerId, MobileSyncState.Synced, DateTimeOffset.UtcNow, null);
                        PeerStates.NotifySession(peer.PeerId, true);
                    }
                }
            };
            connector.SessionFailed += exception =>
            {
                RecordFailure(peer.PeerId, exception);
                if (hasAlternatives && ++failuresSinceAuthentication >= 3)
                {
                    session.Cancel();
                }
            };
            await connector.RunAsync(session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // This address kept failing; the caller tries the next one.
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or InvalidOperationException or PeerSyncException)
        {
            RecordFailure(peer.PeerId, exception);
        }

        return authenticated;
    }

    private async Task MarkConnectedAsync(string peerId, PeerLocator locator)
    {
        try
        {
            await store.MarkPeerLocatorConnectedAsync(peerId, locator.Host, locator.Port, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or System.Data.Common.DbException)
        {
            // Only a dialing preference; the next session records it again.
        }
    }

    private void RecordFailure(string peerId, Exception exception)
    {
        lock (gate)
        {
            var count = failures.GetValueOrDefault(peerId) + 1;
            failures[peerId] = count;
            var backoff = TimeSpan.FromTicks(Math.Min(
                options.MaximumSyncBackoff.Ticks,
                options.SyncInterval.Ticks * (1L << Math.Min(count, 10))));
            notBefore[peerId] = DateTimeOffset.UtcNow + backoff;
            var previous = peerStatus.GetValueOrDefault(peerId);
            var state = exception is PeerAuthenticationException or PeerCompatibilityException
                ? MobileSyncState.Refused
                : MobileSyncState.Offline;
            DiagnosticLog.Write("link", $"{peerId} {state} (try {count}, next in {backoff.TotalSeconds:0} s)", exception);
            var lastSuccess = previous?.State == MobileSyncState.Synced ? DateTimeOffset.UtcNow : previous?.LastSuccess;
            peerStatus[peerId] = new MobilePeerSyncStatus(peerId, state, lastSuccess, PeerLinkErrors.Plain(exception));
            PeerStates.NotifySession(peerId, false, state == MobileSyncState.Refused ? "refused: " + exception.Message : exception.Message);
        }
    }

    private sealed class InitiatingUserApproval : IRemotePairingApproval
    {
        public ValueTask<bool> ApproveAsync(PairingRemoteCandidate candidate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }
    }

    private sealed class StoreTrustSink(PeerStore store) : IPeerTrustSink
    {
        public async ValueTask PersistTrustedPeerAsync(
            PairingRemoteCandidate candidate,
            DateTimeOffset pairedAt,
            CancellationToken cancellationToken) =>
            await store.PairTrustedPeerAsync(new TrustedPeer(
                candidate.Identity.PeerId,
                candidate.Identity.PublicKeySpki,
                candidate.Identity.PublicKeyFingerprint,
                candidate.DisplayName,
                PeerTrustState.Active,
                pairedAt,
                null), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Peer-id derivation and store registration shared by every identity provider (Keystore, software).</summary>
public static class MobilePeerIdentity
{
    public static PeerPublicIdentity CreatePublicIdentity(byte[] publicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        var fingerprint = SHA256.HashData(publicKeySpki);
        return new PeerPublicIdentity(PeerIdFor(fingerprint), publicKeySpki, fingerprint);
    }

    public static string PeerIdFor(byte[] fingerprint) => "peer-" + Suffix(fingerprint);

    public static async Task EnsureRegisteredAsync(
        PeerStore store,
        ILocalPeerIdentityProvider provider,
        CancellationToken cancellationToken)
    {
        var publicIdentity = provider.PublicIdentity;
        var existing = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await store.SetLocalIdentityAsync(new LocalPeerIdentity(
                publicIdentity.PeerId,
                publicIdentity.PublicKeySpki,
                publicIdentity.PublicKeyFingerprint,
                "mobile-provider",
                "origin-" + Suffix(publicIdentity.PublicKeyFingerprint),
                1,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        else if (existing.PeerId != publicIdentity.PeerId ||
                 !CryptographicOperations.FixedTimeEquals(existing.PublicKeyFingerprint, publicIdentity.PublicKeyFingerprint))
        {
            throw new CryptographicException("The device identity key does not match the peer database.");
        }
    }

    private static string Suffix(byte[] fingerprint) =>
        Convert.ToHexString(fingerprint.AsSpan(0, 10)).ToLowerInvariant();
}
