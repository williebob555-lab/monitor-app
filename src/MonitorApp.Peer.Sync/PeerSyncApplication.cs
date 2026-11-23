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
        if (batch.Records.Count == 0 && batch.PrunedRange is null) throw new PeerSyncException("Remote advertised records but returned an empty batch.");
        return (await replication.ApplyAsync(remotePeerId, batch, cancellationToken).ConfigureAwait(false)).Acknowledgement;
    }

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
            RenewRequested: mesh is null ? null : () => mesh.Version != meshVersion));
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
