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

public sealed class PeerNodeRuntime : IAsyncDisposable
{
    private readonly WebApplication peerApplication;
    private readonly LocalApiHost localApi;
    private readonly INodeIdentityProvider identity;
    private readonly NodePeerConnectionManager connections;
    private readonly NodeCollectionScheduler collection;

    private PeerNodeRuntime(
        WebApplication peerApplication,
        LocalApiHost localApi,
        INodeIdentityProvider identity,
        NodePeerConnectionManager connections,
        NodeCollectionScheduler collection)
    {
        this.peerApplication = peerApplication;
        this.localApi = localApi;
        this.identity = identity;
        this.connections = connections;
        this.collection = collection;
    }

    public Uri? AdminAddress => localApi.Address;

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public Task<PeerCollectionRunResult> CollectOnceAsync(
        CancellationToken cancellationToken = default) => collection.RunOnceAsync(cancellationToken);

    public static async Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        var connections = new NodePeerConnectionManager(identity, peerStore, replication);
        var invitationService = new NodePeerInvitationService(identity, peerStore, connections);
        var collection = new NodeCollectionScheduler(
            new PeerSystemMetricsIngestionBridge(
                peerStore,
                identity,
                new SystemSnapshotCollector(platform),
                PeerCollectionOptions.Default(
                    $"peer:{identity.PublicIdentity.PeerId}",
                    Guid.NewGuid().ToString("N"))));
        var peerArgs = new[]
        {
            "--MonitorApp:Peer:BindAddress", options.PeerBindAddress.ToString(),
            "--MonitorApp:Peer:Port", options.PeerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--MonitorApp:Peer:AllowLoopback", System.Net.IPAddress.IsLoopback(options.PeerBindAddress).ToString(),
        };
        var pairingCodes = new PairingShortCodeRegistry();
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
                services.AddProductionPeerSessionCoordinator();
                services.AddSingleton(new PeerPairingServiceOptions(Environment.MachineName));
                services.AddProductionPeerPairingCoordinator();
                services.AddSingleton(pairingCodes);
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
            new PhonePairingOptions([options.PeerBindAddress], options.PeerPort, pairingCodes));
        try
        {
            await peerApplication.StartAsync(cancellationToken).ConfigureAwait(false);
            await localApi.StartAsync(cancellationToken).ConfigureAwait(false);
            await connections.StartAsync(cancellationToken).ConfigureAwait(false);
            collection.Start();
            return new PeerNodeRuntime(peerApplication, localApi, identity, connections, collection);
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
