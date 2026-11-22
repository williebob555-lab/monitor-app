using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Transport.Tests;

public sealed class PeerPairingProductionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-pair-client-" + Guid.NewGuid().ToString("N"));
    private readonly EcdsaPeerIdentityKey clientKey = EcdsaPeerIdentityKey.Create();
    private readonly EcdsaPeerIdentityKey serverKey = EcdsaPeerIdentityKey.Create();

    [Fact]
    public async Task TwoFreshPeersPersistBilateralTrustAfterProofAndConfirmation()
    {
        Directory.CreateDirectory(directory);
        var clientStore = await StoreAsync("client.db");
        var serverStore = await StoreAsync("server.db");
        var serverApplicationStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(Path.Combine(directory, "server-pairing.db")));
        var serverIdentity = new IdentityProvider("server-peer", serverKey);
        var serverPairing = new PeerPairingCoordinator(serverStore, serverApplicationStore, serverIdentity,
            new SystemPeerClock(), new CryptographicPeerRandomSource(),
            new PeerPairingCoordinatorOptions(new PeerProtocolRange(1, 1, 1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)));
        await serverPairing.InitializeAsync();
        var productionCoordinator = new ProductionPeerPairingCoordinator(serverPairing, serverIdentity, new PeerPairingServiceOptions("Server"));
        await using var host = await TestHost.StartAsync(productionCoordinator);

        var clientIdentity = PeerPublicIdentity.Create("client-peer", clientKey);
        var localCoordinator = new P256PeerInitialPairingCoordinator(clientIdentity, clientKey, "Client",
            new ApproveCandidate(), new SqliteTrustSink(clientStore));
        var approveRemote = new ApprovePairingDelay(serverPairing, "pair-1");
        await using var client = new PeerPairingClient(Options(host.Address), localCoordinator,
            new PeerInitialPairingOptions(4, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)), approveRemote);

        var result = await client.PairAsync(new PeerInitialPairingRequest("pair-1", true));

        Assert.Equal("server-peer", result.Remote.Identity.PeerId);
        Assert.Equal("client-peer", (await serverStore.ReadTrustedPeerAsync("client-peer"))!.PeerId);
        Assert.Equal("server-peer", (await clientStore.ReadTrustedPeerAsync("server-peer"))!.PeerId);
        Assert.Equal(1, approveRemote.Calls);
    }

    public void Dispose()
    {
        clientKey.Dispose();
        serverKey.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task<PeerStore> StoreAsync(string name)
    {
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, name)));
        await store.InitializeAsync();
        return store;
    }

    private static PeerTransportOptions Options(Uri address) => new(
        new PeerTransportEndpoint(address, "server-peer", PeerTransportSecurity.LoopbackDevelopment),
        // Generous deadlines: a cold CI runner can take >3 s for the first HTTP/2 call + JIT.
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), 1024 * 1024);

    private sealed class IdentityProvider(string peerId, IPeerIdentityKey key) : ILocalPeerIdentityProvider
    {
        public PeerPublicIdentity PublicIdentity { get; } = PeerPublicIdentity.Create(peerId, key);
        public IPeerIdentityKey IdentityKey { get; } = key;
    }

    private sealed class ApproveCandidate : IRemotePairingApproval
    {
        public ValueTask<bool> ApproveAsync(PairingRemoteCandidate candidate, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class SqliteTrustSink(PeerStore store) : IPeerTrustSink
    {
        public async ValueTask PersistTrustedPeerAsync(PairingRemoteCandidate candidate, DateTimeOffset pairedAt, CancellationToken cancellationToken) =>
            await store.UpsertTrustedPeerAsync(new TrustedPeer(candidate.Identity.PeerId, candidate.Identity.PublicKeySpki,
                candidate.Identity.PublicKeyFingerprint, candidate.DisplayName, PeerTrustState.Active, pairedAt, null), cancellationToken);
    }

    private sealed class ApprovePairingDelay(PeerPairingCoordinator pairing, string pairingId) : IPeerReconnectDelay
    {
        public int Calls { get; private set; }
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Calls++;
            await pairing.ApprovePairingAsync(pairingId, cancellationToken);
        }
    }

    private sealed class EmptySessionCoordinator : IPeerSessionCoordinator
    {
        public ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerAuthenticationResult> AuthenticateAsync(PeerHello remoteHello, MonitorApp.Contracts.V1.PeerProof proof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(string remotePeerId, OriginRangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerHeartbeat> HeartbeatAsync(string remotePeerId, PeerHeartbeat heartbeat, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public static async Task<TestHost> StartAsync(IPeerPairingCoordinator coordinator)
        {
            var port = FreePort();
            var app = await PeerHostApplication.CreateAsync(["--MonitorApp:Peer:BindAddress=127.0.0.1", $"--MonitorApp:Peer:Port={port}", "--MonitorApp:Peer:AllowLoopback=true"], services =>
            { services.AddSingleton<IPeerPairingCoordinator>(coordinator); services.AddSingleton<IPeerSessionCoordinator, EmptySessionCoordinator>(); });
            await app.StartAsync();
            return new TestHost(app, new Uri($"http://127.0.0.1:{port}"));
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
        private static int FreePort()
        { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    }
}
