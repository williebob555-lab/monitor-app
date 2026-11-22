using System.Net;
using System.Net.Sockets;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Android.Core;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>Software P-256 identity; stands in for Android Keystore on Linux CI.</summary>
internal sealed class SoftwareIdentity : ILocalPeerIdentityProvider, IDisposable
{
    private readonly EcdsaPeerIdentityKey key = EcdsaPeerIdentityKey.Create();

    public SoftwareIdentity()
    {
        PublicIdentity = MobilePeerIdentity.CreatePublicIdentity(key.ExportSubjectPublicKeyInfo());
    }

    public PeerPublicIdentity PublicIdentity { get; }

    public IPeerIdentityKey IdentityKey => key;

    public void Dispose() => key.Dispose();
}

/// <summary>Platform boundary of the phone: returns whatever the test says Android reported.</summary>
internal sealed class ScriptedDeviceReader : IMobileDeviceReader
{
    public int Battery { get; set; } = 80;

    public bool Charging { get; set; }

    public DateTimeOffset? FixedTime { get; set; }

    public ValueTask<MobileDeviceSample> ReadAsync(CancellationToken cancellationToken)
    {
        var now = FixedTime ?? DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new MobileDeviceSample(
            now,
            BatteryReadingNormalizer.NormalizeCapacity(Battery, now),
            new DeviceReading<bool>(true, Charging, now, "test"),
            NetworkTransportSummary.Create(true, false, true, false, false, now)));
    }
}

/// <summary>A desktop peer: production Peer.Service host over a real SQLite file, as Peer.Node composes it.</summary>
internal sealed class DesktopPeer : IAsyncDisposable
{
    private readonly SoftwareIdentity identity = new();
    private readonly string root;
    private readonly PeerSystemMetricsIngestionBridge collector;
    private Microsoft.AspNetCore.Builder.WebApplication? app;

    private DesktopPeer(string root, int port, PeerStore store, PairingApplicationStore pairingStore, PeerPairingCoordinator pairing,
        PeerSystemMetricsIngestionBridge collector, SoftwareIdentity identity)
    {
        this.root = root;
        this.identity = identity;
        Port = port;
        Store = store;
        PairingStore = pairingStore;
        Pairing = pairing;
        this.collector = collector;
    }

    public int Port { get; }

    public PairingShortCodeRegistry Codes { get; private set; } = new();

    public PeerStore Store { get; }

    public PairingApplicationStore PairingStore { get; }

    public PeerPairingCoordinator Pairing { get; }

    public PeerPublicIdentity Identity => identity.PublicIdentity;

    public PeerPairingInvitation Invitation() => new(IPAddress.Loopback, Port, Identity.PeerId, Identity.PublicKeyFingerprint, null);

    public static async Task<DesktopPeer> StartAsync(string root, TimeProvider? time = null)
    {
        Directory.CreateDirectory(root);
        var identity = new SoftwareIdentity();
        var databasePath = Path.Combine(root, "peer.db");
        var store = new PeerStore(PeerStoreOptions.Create(databasePath));
        var pairingStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        await store.InitializeAsync();
        await pairingStore.InitializeAsync();
        await MobilePeerIdentity.EnsureRegisteredAsync(store, identity, default);
        var pairing = new PeerPairingCoordinator(
            store, pairingStore, identity, new SystemPeerClock(), new CryptographicPeerRandomSource(),
            new PeerPairingCoordinatorOptions(new PeerProtocolRange(1, 1, 1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2)));
        await pairing.InitializeAsync();
        await new PeerCapabilityRegistry(store).RegisterAsync(SystemCapabilityDeclaration.Create());
        var collector = new PeerSystemMetricsIngestionBridge(
            store, identity, new SystemSnapshotCollector(Platform.Ubuntu),
            PeerCollectionOptions.Default($"peer:{identity.PublicIdentity.PeerId}", Guid.NewGuid().ToString("N")));
        var desktop = new DesktopPeer(root, FreePort(), store, pairingStore, pairing, collector, identity);
        desktop.Codes = new PairingShortCodeRegistry(time);
        await desktop.StartHostAsync();
        return desktop;
    }

    public async Task StartHostAsync()
    {
        var replication = new OriginReplicationCoordinator(Store);
        app = await PeerHostApplication.CreateAsync(
        [
            "--MonitorApp:Peer:BindAddress=127.0.0.1",
            $"--MonitorApp:Peer:Port={Port}",
            "--MonitorApp:Peer:AllowLoopback=true",
        ], services =>
        {
            services.AddSingleton(Store);
            services.AddSingleton(PairingStore);
            services.AddSingleton<ILocalPeerIdentityProvider>(identity);
            services.AddSingleton<IPeerClock>(new SystemPeerClock());
            services.AddSingleton(Pairing);
            services.AddSingleton(Codes);
            services.AddSingleton(replication);
            services.AddProductionPeerSessionCoordinator();
            services.AddSingleton(new PeerPairingServiceOptions("test-desktop"));
            services.AddProductionPeerPairingCoordinator();
        });
        await app.StartAsync();
    }

    public async Task StopHostAsync()
    {
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
        app = null;
    }

    public async Task CollectAsync()
    {
        // Warm-up first call of the real collector reports CPU as unavailable but still records health/metrics.
        var result = await collector.RunOnceAsync();
        Assert.True(result.AppendedRecords > 0);
    }

    /// <summary>The desktop user's explicit approval, via the production pairing coordinator.</summary>
    public async Task ApproveWhenPendingAsync(string pairingId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = await PairingStore.ReadPairingAsync(pairingId);
            if (snapshot is { State: PairingApprovalState.PendingApproval })
            {
                await Pairing.ApprovePairingAsync(pairingId);
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Desktop never received the pairing request.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync();
        identity.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal static class TestDirectories
{
    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-mobile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
