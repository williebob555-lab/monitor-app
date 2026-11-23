using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Peer.Sync;

public sealed record PeerSyncOptions(int MaximumBatchRecords, int MaximumBatchBytes, int MaximumOperationsPerSession, int MaximumCapabilities)
{
    public static PeerSyncOptions Default { get; } = new(256, 1024 * 1024, 1024, 1024);

    internal void Validate()
    {
        if (MaximumBatchRecords is < 1 or > 10_000 || MaximumBatchBytes < 4096 ||
            MaximumOperationsPerSession is < 1 or > 100_000 || MaximumCapabilities is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(PeerSyncOptions));
    }
}

/// <summary>
/// Opts a held-open client session into live origin push. <paramref name="RequestRemotePush"/> is for outbound-only
/// peers (phones) whose remote cannot dial back; peers that dial each other push their own origin on their own session.
/// </summary>
public sealed record PeerSyncLiveOptions(
    LiveInterestRegistry? Interests,
    bool RequestRemotePush,
    int MaximumInFlightBatches = 4,
    Func<Task>? AlertsStored = null);

public sealed class PeerSyncException(string message) : Exception(message);

public sealed class PeerSyncApplication : IPeerSessionApplication, IPeerAdvertisementProvider
{
    private readonly PeerStore store;
    private readonly OriginReplicationCoordinator replication;
    private readonly PeerSyncOptions options;
    private readonly PeerSyncLiveOptions? live;
    private readonly AlertStore? alerts;
    private readonly MeshMembership? mesh;
    private PeerHello? authenticatedRemoteHello;
    // The membership version the statements sent in this session reflect; a later change renews the session.
    private long exchangedMeshVersion;
    // Requests for other devices' readings this session asked the remote to forward, by request id.
    private readonly ConcurrentDictionary<string, (string OriginPeerId, string OriginStreamId)> relayRequests = new(StringComparer.Ordinal);

    /// <summary>How many other devices' streams one session pulls through the remote.</summary>
    public const int MaximumRelayedStreams = 64;

    public PeerSyncApplication(PeerStore store, OriginReplicationCoordinator replication, PeerSyncOptions? options = null,
        AlertStore? alerts = null, PeerSyncLiveOptions? live = null, MeshMembership? mesh = null)
    {
        this.mesh = mesh;
        this.live = live;
        this.alerts = alerts;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.replication = replication ?? throw new ArgumentNullException(nameof(replication));
        this.options = options ?? PeerSyncOptions.Default;
        this.options.Validate();
    }

    public CapabilityExchange LocalCapabilities { get; } = new();

    public bool WantsAlerts => alerts is not null;

    /// <summary>
    /// Persists every alert in the batch, then returns the acknowledgement; an ack is never sent before the write.
    /// Works identically for a pulled batch (one-shot sync) and an unsolicited push on a long-lived session.
    /// </summary>
    public async ValueTask<AlertAck?> AcceptAlertBatchAsync(string remotePeerId, AlertBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (alerts is null || batch.Alerts.Count == 0) return null;
        // Only alerts the authenticated desktop itself owns are accepted: a peer cannot inject alerts on another's behalf.
        var ack = new AlertAck { RequestId = batch.RequestId };
        var now = DateTimeOffset.UtcNow;
        foreach (var record in batch.Alerts)
        {
            if (record.OriginPeerId != remotePeerId) throw new PeerSyncException("Alert origin does not match the authenticated peer.");
            await alerts.StoreInboundAsync(Alert.FromContract(record), now, cancellationToken).ConfigureAwait(false);
            ack.Entries.Add(new AlertAckEntry { AlertId = record.AlertId, Revision = record.Revision, Escalation = record.Escalated });
        }

        if (live?.AlertsStored is { } stored)
            await stored().ConfigureAwait(false);
        return ack;
    }

    public async ValueTask PrepareHelloAsync(PeerHello localHello, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localHello);
        var identity = await RequireIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (localHello.PeerId != identity.PeerId)
            throw new PeerSyncException("Session hello does not match the persisted local identity.");
        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        localHello.Capabilities.Clear();
        localHello.Capabilities.Add(capabilities);
        LocalCapabilities.Capabilities.Clear();
        LocalCapabilities.Capabilities.Add(capabilities.Select(item => item.Clone()));
        localHello.Cursors.Clear();
        localHello.Cursors.Add(LocalAvailability(identity, DateTimeOffset.UtcNow));
        // Mesh statements travel in the capability exchange, which is sent only after both sides authenticated.
        LocalCapabilities.MeshStatements.Clear();
        LocalCapabilities.SelfLocators.Clear();
        if (mesh is not null)
        {
            // Read the version before the statements, so a change made while they are gathered renews the session.
            Volatile.Write(ref exchangedMeshVersion, mesh.Version);
            await MeshExchange.AddLocalAsync(mesh, LocalCapabilities, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask OnAuthenticatedAsync(PeerHello remoteHello, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteHello);
        authenticatedRemoteHello = remoteHello.Clone();
        relayPullsWithUnknownDevices = 0;
        Volatile.Write(ref relayRediscoveryDue, false);
        if (mesh is not null)
        {
            var before = mesh.Version;
            await MeshExchange.ApplyAsync(mesh, remoteHello.PeerId, remoteCapabilities, cancellationToken).ConfigureAwait(false);
            // What the remote just sent needs no renewal; a local change since the hello was prepared still does.
            if (before == Volatile.Read(ref exchangedMeshVersion))
                Volatile.Write(ref exchangedMeshVersion, mesh.Version);
        }
    }

    public async IAsyncEnumerable<PeerSessionRequest> GetPendingRequestsAsync(string remotePeerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var remote = authenticatedRemoteHello?.Clone() ?? throw new PeerSyncException("Remote hello is unavailable before synchronization.");
        if (remote.PeerId != remotePeerId) throw new PeerSyncException("Authenticated peer identity changed.");
        var local = await RequireIdentityAsync(cancellationToken).ConfigureAwait(false);
        var operations = 0;

        var remoteAvailability = remote.Cursors
            .Where(cursor => cursor.OriginPeerId == remotePeerId)
            .OrderBy(cursor => cursor.OriginStreamId, StringComparer.Ordinal)
            .SingleOrDefault();
        while (remoteAvailability is not null)
        {
            var retained = await store.ReadReplicationCursorAsync(remotePeerId, remotePeerId,
                remoteAvailability.OriginStreamId, cancellationToken).ConfigureAwait(false);
            var accepted = retained.Cursor?.AcceptedThrough ?? 0;
            if (accepted >= remoteAvailability.AcceptedThroughSequence) break;
            EnsureOperationBound(++operations);
            yield return new PeerSessionRequest
            {
                OriginRangeRequest = new OriginRangeRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    OriginPeerId = remotePeerId,
                    OriginStreamId = remoteAvailability.OriginStreamId,
                    FirstSequence = checked(accepted + 1),
                    LastSequence = remoteAvailability.AcceptedThroughSequence,
                    MaximumRecordCount = (uint)options.MaximumBatchRecords,
                    MaximumBatchBytes = (uint)options.MaximumBatchBytes,
                },
            };
        }

        // Readings of other trusted devices that the remote holds and this device lacks (T087): pulled through the
        // remote, each record still verified against its origin's own key. Stops when a pull makes no progress (the
        // remote lacks the next record too); the next session or the origin itself fills the gap.
        foreach (var relayed in await RelayableCursorsAsync(remote, local.PeerId, cancellationToken).ConfigureAwait(false))
        {
            var held = await store.ReadHeldThroughAsync(relayed.OriginPeerId, relayed.OriginStreamId, cancellationToken).ConfigureAwait(false);
            while (held < relayed.AcceptedThroughSequence)
            {
                EnsureOperationBound(++operations);
                yield return new PeerSessionRequest
                {
                    OriginRangeRequest = RelayRequest(relayed.OriginPeerId, relayed.OriginStreamId, checked(held + 1), relayed.AcceptedThroughSequence),
                };
                var now = await store.ReadHeldThroughAsync(relayed.OriginPeerId, relayed.OriginStreamId, cancellationToken).ConfigureAwait(false);
                if (now <= held) break;
                held = now;
            }
        }

        var localHead = local.NextSequence - 1;
        while (true)
        {
            var remoteAck = await store.ReadReplicationCursorAsync(remotePeerId, local.PeerId, local.OriginStreamId, cancellationToken)
                .ConfigureAwait(false);
            var accepted = remoteAck.Cursor?.AcceptedThrough ?? 0;
            if (accepted >= localHead) break;
            EnsureOperationBound(++operations);
            var batch = await replication.ReadForPushAsync(new OriginRangeRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                OriginPeerId = local.PeerId,
                OriginStreamId = local.OriginStreamId,
                FirstSequence = checked(accepted + 1),
                LastSequence = localHead,
                MaximumRecordCount = (uint)options.MaximumBatchRecords,
                MaximumBatchBytes = (uint)options.MaximumBatchBytes,
            }, cancellationToken).ConfigureAwait(false);
            if (batch.Records.Count == 0 && batch.PrunedRange is null) throw new PeerSyncException("Local origin availability contains an unexpected gap.");
            yield return new PeerSessionRequest { OriginBatch = batch };
        }
    }

    public async ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (relayRequests.TryRemove(batch.RequestId, out var asked))
        {
            if (batch.Records.Count > 0 && (batch.Records[0].OriginPeerId != asked.OriginPeerId || batch.Records[0].OriginStreamId != asked.OriginStreamId))
                throw new PeerSyncException("Remote answered a relay request with another stream.");
            if (batch.Records.Count == 0)
            {
                // The remote holds nothing more from there: nothing to store. The acknowledgement is informational.
                var held = await store.ReadHeldThroughAsync(asked.OriginPeerId, asked.OriginStreamId, cancellationToken).ConfigureAwait(false);
                return new OriginAck
                {
                    Cursor = new OriginCursor
                    {
                        OriginPeerId = asked.OriginPeerId,
                        OriginStreamId = asked.OriginStreamId,
                        AcceptedThroughSequence = held,
                        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                };
            }
        }

        if (batch.Records.Count == 0 && batch.PrunedRange is null) throw new PeerSyncException("Remote advertised records but returned an empty batch.");
        return (await replication.ApplyAsync(remotePeerId, batch, cancellationToken).ConfigureAwait(false)).Acknowledgement;
    }

    /// <summary>
    /// The other devices' streams the remote advertised holding that this device may pull through it: never its own or
    /// the remote's, and only devices actively trusted here.
    /// </summary>
    private async Task<IReadOnlyList<OriginCursor>> RelayableCursorsAsync(PeerHello remote, string localPeerId, CancellationToken cancellationToken)
    {
        var candidates = remote.Cursors
            .Where(cursor => cursor.OriginPeerId != localPeerId && cursor.OriginPeerId != remote.PeerId &&
                             !string.IsNullOrWhiteSpace(cursor.OriginStreamId) && cursor.AcceptedThroughSequence > 0)
            .ToArray();
        if (candidates.Length == 0) return [];
        var active = (await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false))
            .Where(peer => peer.TrustState == PeerTrustState.Active)
            .Select(peer => peer.PeerId)
            .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(cursor => active.Contains(cursor.OriginPeerId))
            .DistinctBy(cursor => (cursor.OriginPeerId, cursor.OriginStreamId))
            .OrderBy(cursor => cursor.OriginPeerId, StringComparer.Ordinal)
            .ThenBy(cursor => cursor.OriginStreamId, StringComparer.Ordinal)
            .Take(MaximumRelayedStreams)
            .ToArray();
    }

    private OriginRangeRequest RelayRequest(string originPeerId, string originStreamId, ulong first, ulong last)
    {
        var request = new OriginRangeRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OriginPeerId = originPeerId,
            OriginStreamId = originStreamId,
            FirstSequence = first,
            LastSequence = last,
            MaximumRecordCount = (uint)options.MaximumBatchRecords,
            MaximumBatchBytes = (uint)options.MaximumBatchBytes,
        };
        relayRequests[request.RequestId] = (originPeerId, originStreamId);
        return request;
    }

    /// <summary>
    /// Live freshness for relayed readings: open-ended pulls, each starting after the newest record held from that
    /// stream, for every stream the remote advertised at session start or this device already holds. A trusted device
    /// whose stream is known on neither side (it had no readings yet when the session started) renews the session
    /// after a few pulls, so the remote's next hello can advertise it.
    /// </summary>
    private async ValueTask<IReadOnlyList<OriginRangeRequest>> LiveRelayRequestsAsync(CancellationToken cancellationToken)
    {
        var remote = authenticatedRemoteHello;
        if (remote is null) return [];
        var local = await RequireIdentityAsync(cancellationToken).ConfigureAwait(false);
        var known = new PeerHello { PeerId = remote.PeerId };
        known.Cursors.Add(remote.Cursors);
        foreach (var held in await store.ReadHeldOriginStreamsAsync(local.PeerId, MaximumRelayedStreams, cancellationToken).ConfigureAwait(false))
            known.Cursors.Add(new OriginCursor { OriginPeerId = held.OriginPeerId, OriginStreamId = held.OriginStreamId, AcceptedThroughSequence = held.HeldThrough });

        var streams = await RelayableCursorsAsync(known, local.PeerId, cancellationToken).ConfigureAwait(false);
        var requests = new List<OriginRangeRequest>();
        foreach (var relayed in streams)
        {
            var held = await store.ReadHeldThroughAsync(relayed.OriginPeerId, relayed.OriginStreamId, cancellationToken).ConfigureAwait(false);
            var newest = await store.ReadHighestOriginSequenceAsync(relayed.OriginPeerId, relayed.OriginStreamId, cancellationToken).ConfigureAwait(false);
            requests.Add(RelayRequest(relayed.OriginPeerId, relayed.OriginStreamId, checked(Math.Max(held, newest) + 1), 0));
        }

        var unknown = (await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false))
            .Any(peer => peer.TrustState == PeerTrustState.Active && peer.PeerId != local.PeerId && peer.PeerId != remote.PeerId &&
                         streams.All(stream => stream.OriginPeerId != peer.PeerId));
        if (!unknown) relayPullsWithUnknownDevices = 0;
        else if (++relayPullsWithUnknownDevices >= RelayPullsBeforeRediscovery) Volatile.Write(ref relayRediscoveryDue, true);
        return requests;
    }

    // Pulls with a trusted device whose stream is unknown here, before the session renews to learn it (about a minute).
    private const int RelayPullsBeforeRediscovery = 4;
    private int relayPullsWithUnknownDevices;
    private bool relayRediscoveryDue;

    public async ValueTask<OriginAck?> AcceptRangePrunedAsync(string remotePeerId, PrunedRange range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        return await replication.ApplyPrunedRangeAsync(remotePeerId, range, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        var local = await RequireIdentityAsync(cancellationToken).ConfigureAwait(false);
        var cursor = acknowledgement.Cursor ?? throw new PeerSyncException("Origin acknowledgement cursor is required.");
        if (cursor.OriginPeerId != local.PeerId || cursor.OriginStreamId != local.OriginStreamId ||
            cursor.AcceptedThroughSequence > local.NextSequence - 1)
            throw new PeerSyncException("Origin acknowledgement does not match the local origin stream.");
        await store.UpdateReplicationCursorAsync(new ReplicationCursor(remotePeerId, local.PeerId, local.OriginStreamId,
            cursor.AcceptedThroughSequence, "raw", cursor.UpdatedAt.ToDateTimeOffset()), [], cancellationToken).ConfigureAwait(false);
    }

    public IPeerLiveSession? OpenLiveSession(string remotePeerId)
    {
        if (live is null)
        {
            return null;
        }

        var meshVersion = Volatile.Read(ref exchangedMeshVersion);

        return new PeerLiveSession(new PeerLiveSessionOptions(
            store,
            remotePeerId,
            (request, token) => new ValueTask<OriginRecordBatch>(replication.ReadForPushAsync(request, token)),
            async token =>
            {
                var trusted = await store.ReadTrustedPeerAsync(remotePeerId, token).ConfigureAwait(false);
                if (trusted is null || trusted.TrustState != PeerTrustState.Active)
                    throw new PeerSyncException("Peer trust is not active.");
            },
            live.Interests,
            live.RequestRemotePush,
            live.MaximumInFlightBatches,
            options.MaximumBatchRecords,
            options.MaximumBatchBytes,
            LocallyInitiated: true,
            RenewRequested: () => Volatile.Read(ref relayRediscoveryDue) || (mesh is not null && mesh.Version != meshVersion),
            RelayRequests: LiveRelayRequestsAsync));
    }

    public async ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = await store.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return capabilities.OrderBy(item => item.Namespace, StringComparer.Ordinal).ThenBy(item => item.CapabilityId, StringComparer.Ordinal)
            .Take(options.MaximumCapabilities)
            .Select(item => new CapabilityDescriptor
            {
                ModuleId = item.CapabilityId.StartsWith(item.Namespace + ".", StringComparison.Ordinal)
                    ? item.CapabilityId : item.Namespace + "." + item.CapabilityId,
                ModuleVersion = item.Version,
                DeclarationSha256 = ByteString.CopyFrom(SHA256.HashData(item.Descriptor)),
            }).ToArray();
    }

    public async ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteHello);
        var identity = await RequireIdentityAsync(cancellationToken).ConfigureAwait(false);
        var cursors = new List<OriginCursor>
        {
            LocalAvailability(identity, DateTimeOffset.UtcNow),
        };
        foreach (var streamId in remoteHello.Cursors
                     .Where(cursor => cursor.OriginPeerId == remoteHello.PeerId)
                     .Select(cursor => cursor.OriginStreamId)
                     .Where(streamId => !string.IsNullOrWhiteSpace(streamId))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(streamId => streamId, StringComparer.Ordinal))
        {
            var retained = await store.ReadReplicationCursorAsync(
                remoteHello.PeerId,
                remoteHello.PeerId,
                streamId,
                cancellationToken).ConfigureAwait(false);
            if (retained.Cursor is not null)
            {
                cursors.Add(new OriginCursor
                {
                    OriginPeerId = retained.Cursor.OriginPeerId,
                    OriginStreamId = retained.Cursor.OriginStreamId,
                    AcceptedThroughSequence = retained.Cursor.AcceptedThrough,
                    UpdatedAt = Timestamp.FromDateTimeOffset(retained.Cursor.UpdatedAt),
                });
            }
        }

        return cursors;
    }

    private async Task<LocalPeerIdentity> RequireIdentityAsync(CancellationToken cancellationToken) =>
        await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
        throw new PeerSyncException("Local peer identity is not initialized.");

    private static OriginCursor LocalAvailability(LocalPeerIdentity identity, DateTimeOffset now) => new()
    {
        OriginPeerId = identity.PeerId,
        OriginStreamId = identity.OriginStreamId,
        AcceptedThroughSequence = identity.NextSequence - 1,
        UpdatedAt = Timestamp.FromDateTimeOffset(now),
    };

    private void EnsureOperationBound(int operations)
    {
        if (operations > options.MaximumOperationsPerSession)
            throw new PeerSyncException("Synchronization exceeded the configured per-session operation bound.");
    }
}
