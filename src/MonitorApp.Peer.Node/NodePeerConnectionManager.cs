using System.Collections.Concurrent;
using System.Net;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Sync;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Peer.Node;

public sealed class NodePeerConnectionManager : IAsyncDisposable
{
    private readonly ILocalPeerIdentityProvider identity;
    private readonly PeerStore store;
    private readonly OriginReplicationCoordinator replication;
    private readonly LiveInterestRegistry? interests;
    private readonly TimeSpan? heartbeatInterval;
    private readonly MeshMembership? mesh;
    private readonly PeerLinkStatusBoard? links;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, string> permanentFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> connections = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool started;

    public NodePeerConnectionManager(
        ILocalPeerIdentityProvider identity,
        PeerStore store,
        OriginReplicationCoordinator replication,
        LiveInterestRegistry? interests = null,
        TimeSpan? heartbeatInterval = null,
        MeshMembership? mesh = null,
        PeerLinkStatusBoard? links = null)
    {
        this.mesh = mesh;
        this.links = links;
        this.interests = interests;
        this.heartbeatInterval = heartbeatInterval;
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.replication = replication ?? throw new ArgumentNullException(nameof(replication));
    }

    public IReadOnlyDictionary<string, string> PermanentFailures => permanentFailures;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (started)
            {
                throw new InvalidOperationException("Peer connection scheduling is already started.");
            }

            started = true;
        }

        var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in peers.Where(item => item.TrustState == PeerTrustState.Active))
        {
            var locators = await store.ReadPeerLocatorsAsync(peer.PeerId, cancellationToken)
                .ConfigureAwait(false);
            if (locators.Count > 0)
            {
                StartConnection(peer, locators[0]);
            }
            else
            {
                // Phones and tablets have no address to call; they connect to this computer.
                links?.Waiting(peer.PeerId);
            }
        }
    }

    /// <summary>
    /// Dials every actively trusted peer that has a locator and no running session: peers the mesh admitted since the
    /// last pass, and peers whose session ended (for example refused because the mesh had not reached them yet).
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!started || lifetime.IsCancellationRequested) return;
        }

        var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var peer in peers.Where(item => item.TrustState == PeerTrustState.Active))
        {
            lock (gate)
            {
                if (connections.TryGetValue(peer.PeerId, out var running))
                {
                    if (!running.IsCompleted) continue;
                    connections.Remove(peer.PeerId);
                    permanentFailures.TryRemove(peer.PeerId, out _);
                }
            }

            var locators = await store.ReadPeerLocatorsAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
            if (locators.Count > 0)
            {
                StartConnection(peer, locators[0]);
            }
            else
            {
                // Phones and tablets have no address to call; they connect to this computer.
                links?.Waiting(peer.PeerId);
            }
        }
    }

    public async Task TrackAsync(
        string peerId,
        IPAddress address,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var peer = await store.ReadTrustedPeerAsync(peerId, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Only a paired peer can be scheduled for reconnection.");
        if (peer.TrustState != PeerTrustState.Active)
        {
            throw new InvalidOperationException("A revoked peer cannot be scheduled for reconnection.");
        }

        var locator = new PeerLocator(peerId, address.ToString(), port, DateTimeOffset.UtcNow, null);
        await store.UpsertPeerLocatorAsync(locator, cancellationToken).ConfigureAwait(false);
        StartConnection(peer, locator);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (gate)
        {
            pending = connections.Values.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A session that faulted must never abort the rest of the node's shutdown (stores would stay open).
            permanentFailures[$"shutdown:{exception.GetType().Name}"] = exception.Message;
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private void StartConnection(TrustedPeer peer, PeerLocator locator)
    {
        lock (gate)
        {
            if (!started || lifetime.IsCancellationRequested || connections.ContainsKey(peer.PeerId))
            {
                return;
            }

            connections.Add(peer.PeerId, RunConnectionAsync(peer, locator));
        }
    }

    private async Task RunConnectionAsync(TrustedPeer peer, PeerLocator first)
    {
        // Dial the best address; one that keeps failing before it ever authenticates gives way to the peer's next address
        // (an advertised address this computer cannot reach must not cut it off from one that works).
        var attempt = 0;
        var locator = first;
        while (!lifetime.IsCancellationRequested)
        {
            var locators = await ReadLocatorsAsync(peer.PeerId).ConfigureAwait(false);
            if (attempt > 0 && locators.Count > 0) locator = locators[attempt % locators.Count];
            var hasAlternatives = locators.Count > 1;
            if (await RunConnectionOnceAsync(peer, locator, hasAlternatives).ConfigureAwait(false) is null) return;
            // The address stopped working (or never did): move on to the next one.
            attempt++;
        }
    }

    private async Task<IReadOnlyList<PeerLocator>> ReadLocatorsAsync(string peerId)
    {
        try
        {
            return await store.ReadPeerLocatorsAsync(peerId, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Returns whether the session authenticated, or null when the connection must not be retried.</summary>
    private async Task<bool?> RunConnectionOnceAsync(TrustedPeer peer, PeerLocator locator, bool hasAlternatives)
    {
        var authenticated = false;
        var failuresSinceAuthentication = 0;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            if (!IPAddress.TryParse(locator.Host, out var address))
            {
                throw new InvalidOperationException("A persisted peer locator is not an IP address.");
            }

            var security = IPAddress.IsLoopback(address)
                ? PeerTransportSecurity.LoopbackDevelopment
                : PeerTransportSecurity.TailscaleEncrypted;
            var endpoint = new PeerTransportEndpoint(
                new UriBuilder(Uri.UriSchemeHttp, address.ToString(), locator.Port).Uri,
                peer.PeerId,
                security);
            var remoteIdentity = new PeerPublicIdentity(
                peer.PeerId,
                peer.PublicKeySpki,
                peer.PublicKeyFingerprint);
            var authentication = new P256PeerSessionAuthenticationProvider(
                identity.PublicIdentity,
                remoteIdentity,
                identity.IdentityKey);
            // Held-open session: after catch-up both origins stream live. The dialer asks the remote to push its own
            // origin too, because the remote may have no locator to dial back (the same shape as the phone).
            var synchronization = new PeerSyncApplication(
                store,
                replication,
                live: new PeerSyncLiveOptions(interests, RequestRemotePush: true),
                mesh: mesh);
            var shown = $"{address}:{locator.Port}";
            links?.Connecting(peer.PeerId, shown);
            var transport = PeerTransportOptions.Create(endpoint);
            if (heartbeatInterval is { } heartbeat)
            {
                transport = transport with { HeartbeatInterval = heartbeat };
            }

            await using var connector = new PeerGrpcConnector(
                transport,
                authentication,
                synchronization);
            connector.StateChanged += state =>
            {
                if (state != PeerTransportState.Authenticated) return;
                links?.Connected(peer.PeerId, shown);
                failuresSinceAuthentication = 0;
                if (authenticated) return;
                authenticated = true;
                _ = MarkConnectedAsync(peer.PeerId, locator);
            };
            connector.SessionFailed += failure =>
            {
                links?.Failed(peer.PeerId, PeerLinkErrors.Plain(failure), retrying: failure is not (PeerAuthenticationException or PeerCompatibilityException));
                if (hasAlternatives && ++failuresSinceAuthentication >= 3) session.Cancel();
            };
            await connector.RunAsync(session.Token).ConfigureAwait(false);
            return lifetime.IsCancellationRequested ? null : authenticated;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return authenticated;
        }
        catch (Exception exception) when (exception is
            PeerAuthenticationException or
            PeerCompatibilityException or
            InvalidOperationException)
        {
            permanentFailures[peer.PeerId] = exception.Message;
            links?.Failed(peer.PeerId, PeerLinkErrors.Plain(exception), retrying: false);
            return null;
        }
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
}
