using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Peer.Application;
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
    private readonly WindowsDpapiIdentityProvider identity;

    private PeerNodeRuntime(
        WebApplication peerApplication,
        LocalApiHost localApi,
        WindowsDpapiIdentityProvider identity)
    {
        this.peerApplication = peerApplication;
        this.localApi = localApi;
        this.identity = identity;
    }

    public Uri? AdminAddress => localApi.Address;

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public static async Task<PeerNodeRuntime> StartAsync(
        NodeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.DataDirectory);
        var databasePath = Path.Combine(options.DataDirectory, "peer.db");
        var peerStore = new PeerStore(PeerStoreOptions.Create(databasePath));
        var pairingStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        await peerStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await pairingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var identity = await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
            Path.Combine(options.DataDirectory, "identity.dpapi"),
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
        var invitationService = new NodePeerInvitationService(identity, peerStore);
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
            invitationService);
        try
        {
            await peerApplication.StartAsync(cancellationToken).ConfigureAwait(false);
            await localApi.StartAsync(cancellationToken).ConfigureAwait(false);
            return new PeerNodeRuntime(peerApplication, localApi, identity);
        }
        catch
        {
            await localApi.DisposeAsync().ConfigureAwait(false);
            await peerApplication.DisposeAsync().ConfigureAwait(false);
            identity.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await localApi.DisposeAsync().ConfigureAwait(false);
        await peerApplication.StopAsync().ConfigureAwait(false);
        await peerApplication.DisposeAsync().ConfigureAwait(false);
        identity.Dispose();
    }
}
