using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Agent.Core;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;

namespace MonitorApp.Peer.Node;

/// <summary>Tunables for live behaviour; defaults suit production, tests shorten them.</summary>
public sealed record NodeLiveOptions(
    TimeSpan? CollectionInterval = null,
    LiveInterestPolicy? InterestPolicy = null,
    TimeSpan? HeartbeatInterval = null);

public sealed class PeerNodeRuntime : IAsyncDisposable
{
    private readonly WebApplication peerApplication;
    private readonly LocalApiHost localApi;
    private readonly INodeIdentityProvider identity;
    private readonly NodePeerConnectionManager connections;
    private readonly NodeCollectionScheduler collection;
    private readonly PeerStore store;
    private readonly LiveInterestRegistry interests;

    private PeerNodeRuntime(
        WebApplication peerApplication,
        LocalApiHost localApi,
        INodeIdentityProvider identity,
        NodePeerConnectionManager connections,
        NodeCollectionScheduler collection,
        PeerStore store,
        LiveInterestRegistry interests,
        int peerPort)
    {
        PeerPort = peerPort;
        this.store = store;
        this.interests = interests;
        this.peerApplication = peerApplication;
        this.localApi = localApi;
        this.identity = identity;
        this.connections = connections;
        this.collection = collection;
    }

    public Uri? AdminAddress => localApi.Address;

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public PeerStore Store => store;

    /// <summary>The port the peer (gRPC) listener was asked to bind.</summary>
    public int PeerPort { get; }

    /// <summary>The sampling interval currently in force for this node's own collection.</summary>
    public TimeSpan CurrentCollectionInterval => collection.CurrentInterval;

    public int ActiveLiveInterestCount => interests.ActiveInterestCount;

    /// <summary>
    /// Newly stored observations (locally collected or replicated) as they commit; the in-process feed a UI subscribes to.
    /// </summary>
    public IAsyncEnumerable<StoredOriginRecord> LiveObservations(CancellationToken cancellationToken = default) =>
        store.Live.StreamAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// A local UI is viewing live data: with no argument this node's own data (switching its collection to the fast
    /// interval), otherwise a paired remote peer (declared over the held-open session). Dispose to stop viewing.
    /// </summary>
    public IDisposable BeginViewing(string? remotePeerId = null) =>
        remotePeerId is null
            ? interests.WatchLocal(identity.PublicIdentity.PeerId)
            : interests.WatchRemote(remotePeerId);

    public Task<PeerCollectionRunResult> CollectOnceAsync(
        CancellationToken cancellationToken = default) => collection.RunOnceAsync(cancellationToken);

    public static Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        CancellationToken cancellationToken = default) => StartAsync(options, null, cancellationToken);

    public static async Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        NodeLiveOptions? live,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var interests = new LiveInterestRegistry(live?.InterestPolicy);
        // Fails clearly on unsupported OSes and Linux distributions before any state is created.
        var platform = PlatformDetector.DetectCurrent();
        Directory.CreateDirectory(options.DataDirectory);
        var databasePath = Path.Combine(options.DataDirectory, "peer.db");
        var peerStore = new PeerStore(PeerStoreOptions.Create(databasePath));
        var pairingStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        await peerStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await pairingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var identity = await NodeIdentityProvider.LoadOrCreateAsync(
            options.DataDirectory,
            peerStore,
            cancellationToken).ConfigureAwait(false);
        var clock = new SystemPeerClock();
        var pairing = new PeerPairingCoordinator(
            peerStore,
            pairingStore,
            identity,
            clock,
            new CryptographicPeerRandomSource(),
            new PeerPairingCoordinatorOptions(
                new PeerProtocolRange(1, 1, 1),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2)));
        await pairing.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var replication = new OriginReplicationCoordinator(peerStore);
        var capabilityRegistry = new PeerCapabilityRegistry(peerStore);
        await capabilityRegistry.RegisterAsync(
            SystemCapabilityDeclaration.Create(),
            cancellationToken).ConfigureAwait(false);
        var connections = new NodePeerConnectionManager(identity, peerStore, replication, interests, live?.HeartbeatInterval);
        var invitationService = new NodePeerInvitationService(identity, peerStore, connections);
        var collection = new NodeCollectionScheduler(
            new PeerSystemMetricsIngestionBridge(
                peerStore,
                identity,
                new SystemSnapshotCollector(platform),
                PeerCollectionOptions.Default(
                    $"peer:{identity.PublicIdentity.PeerId}",
                    Guid.NewGuid().ToString("N"))),
            live?.CollectionInterval,
            interests,
            identity.PublicIdentity.PeerId);
        var peerArgs = new[]
        {
            "--MonitorApp:Peer:BindAddress", options.PeerBindAddress.ToString(),
            "--MonitorApp:Peer:Port", options.PeerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--MonitorApp:Peer:AllowLoopback", System.Net.IPAddress.IsLoopback(options.PeerBindAddress).ToString(),
        };
        var peerApplication = await PeerHostApplication.CreateAsync(
            peerArgs,
            services =>
            {
                services.AddSingleton(peerStore);
                services.AddSingleton(pairingStore);
                services.AddSingleton<ILocalPeerIdentityProvider>(identity);
                services.AddSingleton(identity);
                services.AddSingleton<IPeerClock>(clock);
                services.AddSingleton(pairing);
                services.AddSingleton(replication);
                services.AddSingleton(interests);
                services.AddProductionPeerSessionCoordinator();
                services.AddSingleton(new PeerPairingServiceOptions(Environment.MachineName));
                services.AddProductionPeerPairingCoordinator();
            }, cancellationToken).ConfigureAwait(false);
        var localApi = LocalApiHost.Create(
            new LocalApiOptions(
                databasePath,
                System.Net.IPAddress.Loopback,
                options.AdminPort),
            peerStore,
            pairingStore,
            pairing,
            clock,
            invitationService,
            interests);
        try
        {
            await peerApplication.StartAsync(cancellationToken).ConfigureAwait(false);
            await localApi.StartAsync(cancellationToken).ConfigureAwait(false);
            await connections.StartAsync(cancellationToken).ConfigureAwait(false);
            collection.Start();
            return new PeerNodeRuntime(peerApplication, localApi, identity, connections, collection, peerStore, interests, options.PeerPort);
        }
        catch
        {
            await collection.DisposeAsync().ConfigureAwait(false);
            await connections.DisposeAsync().ConfigureAwait(false);
            await localApi.DisposeAsync().ConfigureAwait(false);
            await peerApplication.DisposeAsync().ConfigureAwait(false);
            identity.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await collection.DisposeAsync().ConfigureAwait(false);
        await connections.DisposeAsync().ConfigureAwait(false);
        await localApi.DisposeAsync().ConfigureAwait(false);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await peerApplication.StopAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }

        await peerApplication.DisposeAsync().ConfigureAwait(false);
        identity.Dispose();
    }
}
