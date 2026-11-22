using System.Security.Cryptography;
using Grpc.Core;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
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
    LiveInterestPolicy? InterestPolicy = null)
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

/// <summary>
/// Portable, lifecycle-agnostic phone peer: local store, signed origin collection, explicit outbound pairing, and a
/// phone-initiated sync loop. It never listens; replication in both directions rides the sessions it dials.
/// The host (an Android foreground service) only calls <see cref="Start"/> and <see cref="StopAsync"/>.
/// </summary>
public sealed class MobilePeerRuntime : IAsyncDisposable
{
    private readonly MobilePeerOptions options;
    private readonly ILocalPeerIdentityProvider identity;
    private readonly PeerStore store;
    private readonly OriginReplicationCoordinator replication;
    private readonly PeerSystemMetricsIngestionBridge collection;
    private readonly AlertStore alerts;
    private readonly LiveInterestRegistry interests;
    private readonly object gate = new();
    private readonly Dictionary<string, MobilePeerSyncStatus> peerStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> notBefore = new(StringComparer.Ordinal);
    private CancellationTokenSource? loopCancellation;
    private Task[] loops = [];
    private string? pendingPairingId;
    private MobileCollectionStatus collectionStatus = new(null, 0, null);

    private MobilePeerRuntime(
        MobilePeerOptions options,
        ILocalPeerIdentityProvider identity,
        PeerStore store,
        AlertStore alerts,
        PeerSystemMetricsIngestionBridge collection)
    {
        this.alerts = alerts;
        this.options = options;
        this.identity = identity;
        this.store = store;
        this.collection = collection;
        replication = new OriginReplicationCoordinator(store);
        interests = new LiveInterestRegistry(options.InterestPolicy);
    }

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public static async Task<MobilePeerRuntime> CreateAsync(
        MobilePeerOptions options,
        ILocalPeerIdentityProvider identity,
        IMobileDeviceReader reader,
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
        var bridge = new PeerSystemMetricsIngestionBridge(
            store,
            identity,
            new AndroidDeviceSnapshotCollector(reader),
            new PeerCollectionOptions(
                $"peer:{identity.PublicIdentity.PeerId}",
                AndroidDeviceSnapshotCollector.ModuleId,
                Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(30),
                256));
        return new MobilePeerRuntime(options, identity, store, alertStore, bridge);
    }

    public PeerStore Store => store;

    /// <summary>
    /// Newly stored observations (own readings and replicated desktop data) as they commit, for the app UI.
    /// </summary>
    public IAsyncEnumerable<StoredOriginRecord> LiveObservations(CancellationToken cancellationToken = default) =>
        store.Live.StreamAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// The UI is viewing live data: for a paired desktop peer id the held-open session declares and renews interest so
    /// that desktop samples fast; with no id the phone itself samples fast. Dispose to stop viewing.
    /// </summary>
    public IDisposable BeginViewing(string? remotePeerId = null) =>
        remotePeerId is null ? interests.WatchLocal(identity.PublicIdentity.PeerId) : interests.WatchRemote(remotePeerId);

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
    public Task<IReadOnlyList<InboxAlert>> ReadAlertsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        alerts.ReadInboxAsync(limit, false, cancellationToken);

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
                new PeerInitialPairingOptions(90, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
            var result = await client.PairAsync(
                new PeerInitialPairingRequest(id, InitiatingUserApproved: true, invitation.FingerprintSha256),
                cancellationToken).ConfigureAwait(false);
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

            return result.Remote.Identity.PeerId;
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or TimeoutException or PeerPersistenceException or InvalidOperationException or FormatException)
        {
            throw new MobilePairingFailedException(
                "Pairing did not complete. Verify the address, desktop identity, and desktop approval.", exception);
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
    public Task RevokeAsync(string peerId, CancellationToken cancellationToken = default) =>
        store.RevokeTrustedPeerAsync(peerId, DateTimeOffset.UtcNow, cancellationToken);

    public async Task<PeerCollectionRunResult> CollectOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await collection.RunOnceAsync(cancellationToken).ConfigureAwait(false);
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

            await SyncPeerAsync(peer, locators[0], cancellationToken).ConfigureAwait(false);
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

    private async Task SyncPeerAsync(TrustedPeer peer, PeerLocator locator, CancellationToken cancellationToken)
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
                new PeerSyncApplication(store, replication, alerts: alerts));
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
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or InvalidOperationException or PeerSyncException or OperationCanceledException)
        {
            RecordFailure(peer.PeerId, exception);
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
                        await CollectOnceAsync(token).ConfigureAwait(false);
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

                    await Task.Delay(options.SyncInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
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

                if (wait <= TimeSpan.Zero && locators.Count > 0)
                {
                    await RunLiveSessionAsync(peer, locators[0], cancellationToken).ConfigureAwait(false);
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

                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One held-open session (with transport-level reconnect) until cancellation or a permanent refusal.</summary>
    private async Task RunLiveSessionAsync(TrustedPeer peer, PeerLocator locator, CancellationToken cancellationToken)
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
            var transport = PeerTransportOptions.Create(endpoint) with
            {
                HeartbeatInterval = options.HeartbeatInterval ?? TimeSpan.FromSeconds(15),
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
                    live: new PeerSyncLiveOptions(interests, RequestRemotePush: true, AlertsStored: RaiseNewAlertsAsync)));
            connector.StateChanged += state =>
            {
                if (state == PeerTransportState.Authenticated)
                {
                    lock (gate)
                    {
                        failures.Remove(peer.PeerId);
                        notBefore.Remove(peer.PeerId);
                        peerStatus[peer.PeerId] = new MobilePeerSyncStatus(
                            peer.PeerId, MobileSyncState.Synced, DateTimeOffset.UtcNow, null);
                    }
                }
            };
            connector.SessionFailed += exception => RecordFailure(peer.PeerId, exception);
            await connector.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PeerTransportException or RpcException or HttpRequestException
            or IOException or TimeoutException or InvalidOperationException or PeerSyncException)
        {
            RecordFailure(peer.PeerId, exception);
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
            var lastSuccess = previous?.State == MobileSyncState.Synced ? DateTimeOffset.UtcNow : previous?.LastSuccess;
            peerStatus[peerId] = new MobilePeerSyncStatus(peerId, state, lastSuccess, exception.Message);
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
            await store.UpsertTrustedPeerAsync(new TrustedPeer(
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
