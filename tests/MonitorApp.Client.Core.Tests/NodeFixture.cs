using System.Security.Cryptography;
using Google.Protobuf;
using MonitorApp.Client.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Client.Core.Tests;

/// <summary>A real LocalApiHost over a real SQLite file, seeded through the production store, with one pending pairing.</summary>
internal sealed class NodeFixture : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 21, 16, 0, 0, TimeSpan.Zero);

    private readonly string directory;
    private readonly EcdsaPeerIdentityKey localKey;
    private readonly List<EcdsaPeerIdentityKey> keys = [];
    private readonly PeerPairingCoordinator coordinator;
    private ulong nextSequence = 1;

    private NodeFixture(
        string directory,
        EcdsaPeerIdentityKey localKey,
        PeerStore store,
        PeerPairingCoordinator coordinator,
        LocalApiHost host,
        string pendingPairingId,
        string localFingerprint)
    {
        this.directory = directory;
        this.localKey = localKey;
        Store = store;
        this.coordinator = coordinator;
        Host = host;
        PendingPairingId = pendingPairingId;
        LocalFingerprint = localFingerprint;
    }

    public PeerStore Store { get; }

    public LocalApiHost Host { get; }

    public string PendingPairingId { get; }

    public string LocalFingerprint { get; }

    public Uri Address => Host.Address!;

    public LocalApiClient NewClient(ILocalPresenceProvider? presence = null) => LocalApiClient.Create(Address, presence: presence);

    public ControlService? Control { get; private set; }

    public static async Task<NodeFixture> CreateAsync(
        LocalPresenceService? presence = null,
        ILocalUpdateController? updates = null,
        bool withControl = false,
        LiveInterestRegistry? interests = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "monitor-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "peer.db");
        var localKey = EcdsaPeerIdentityKey.Create();
        var identity = new IdentityProvider("peer-local", localKey);
        var store = new PeerStore(PeerStoreOptions.Create(databasePath));
        var appStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
        var coordinator = new PeerPairingCoordinator(
            store,
            appStore,
            identity,
            FixedClock.Instance,
            new SequentialRandom(),
            new PeerPairingCoordinatorOptions(
                new PeerProtocolRange(1, 0, 0), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2)));
        await coordinator.InitializeAsync();
        await store.SetLocalIdentityAsync(new LocalPeerIdentity(
            "peer-local",
            identity.PublicIdentity.PublicKeySpki,
            identity.PublicIdentity.PublicKeyFingerprint,
            "key-provider-secret",
            "origin-local",
            1,
            Now.AddHours(-1)));

        using var trustedKey = EcdsaPeerIdentityKey.Create();
        var trusted = PeerPublicIdentity.Create("peer-trusted", trustedKey);
        await store.UpsertTrustedPeerAsync(new TrustedPeer(
            trusted.PeerId,
            trusted.PublicKeySpki,
            trusted.PublicKeyFingerprint,
            "Trusted workstation",
            PeerTrustState.Active,
            Now.AddDays(-1),
            null));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            "adapter-one", "test.adapter", "1.0.0", "peer-local", null, "adapter-credential", true));

        var pendingKey = EcdsaPeerIdentityKey.Create();
        var pending = await coordinator.BeginPairingAsync(new PairingCandidate(
            PeerPublicIdentity.Create("peer-pending", pendingKey),
            "Pending laptop",
            PeerProofRole.Initiator,
            new PeerProtocolRange(1, 0, 0),
            []));

        ControlService? control = null;
        AlertStore? alerts = null;
        if (withControl)
        {
            alerts = new AlertStore(AlertStoreOptions.Create(databasePath));
            await alerts.InitializeAsync();
            var controlStore = new ControlStore(ControlStoreOptions.Create(databasePath));
            await controlStore.InitializeAsync();
            control = new ControlService(
                controlStore, store, appStore, coordinator, alerts, new MonitoringControlState(), identity);
        }

        var host = LocalApiHost.Create(
            LocalApiOptions.Loopback(databasePath), store, appStore, coordinator, FixedClock.Instance,
            alertStore: alerts, interests: interests, updates: updates, control: control, localPresence: presence);
        await host.StartAsync();
        var fixture = new NodeFixture(
            directory, localKey, store, coordinator, host, pending.PairingId!, Convert.ToHexString(identity.PublicIdentity.PublicKeyFingerprint));
        fixture.keys.Add(pendingKey);
        fixture.Control = control;
        return fixture;
    }

    public async Task<string> CreatePendingPairingAsync(string peerId)
    {
        var key = EcdsaPeerIdentityKey.Create();
        keys.Add(key);
        var result = await coordinator.BeginPairingAsync(new PairingCandidate(
            PeerPublicIdentity.Create(peerId, key), peerId, PeerProofRole.Initiator, new PeerProtocolRange(1, 0, 0), []));
        return result.PairingId!;
    }

    public Task AddNumberAsync(DateTimeOffset observedAt, double value, string subject = "host-a", string signal = "cpu.load") =>
        AppendAsync(observedAt, new TelemetryEnvelope
        {
            Metric = new MetricSample
            {
                MetricId = signal,
                Unit = MetricUnit.Ratio,
                Quality = ObservationQuality.Valid,
                Value = new MetricValue { DoubleValue = value },
            },
        }, "Valid", subject, signal);

    /// <summary>An unavailable reading; the payload deliberately carries a zero a careless client could render.</summary>
    public Task AddUnavailableAsync(DateTimeOffset observedAt, string subject = "host-a", string signal = "cpu.load") =>
        AppendAsync(observedAt, new TelemetryEnvelope
        {
            Metric = new MetricSample
            {
                MetricId = signal,
                Unit = MetricUnit.Ratio,
                Quality = ObservationQuality.Unavailable,
                Value = new MetricValue { DoubleValue = 0 },
            },
        }, "Unavailable", subject, signal);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        foreach (var key in keys)
        {
            key.Dispose();
        }

        localKey.Dispose();
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // SQLite pooled handles may linger briefly; temp files are disposable.
        }
    }

    private async Task AppendAsync(DateTimeOffset observedAt, TelemetryEnvelope envelope, string quality, string subject, string signal)
    {
        await Store.AppendOriginRecordAsync(new OriginRecordWrite(
            "peer-remote",
            "origin-remote",
            nextSequence++,
            subject,
            "adapter-one",
            signal,
            envelope.ToByteArray(),
            Enumerable.Repeat((byte)0x2a, 64).ToArray(),
            observedAt,
            observedAt.AddMilliseconds(10),
            "peer-remote",
            RecordProvenance.Replicated,
            quality));
    }

    private sealed class IdentityProvider : ILocalPeerIdentityProvider
    {
        public IdentityProvider(string peerId, IPeerIdentityKey key)
        {
            IdentityKey = key;
            PublicIdentity = PeerPublicIdentity.Create(peerId, key);
        }

        public PeerPublicIdentity PublicIdentity { get; }

        public IPeerIdentityKey IdentityKey { get; }
    }

    private sealed class FixedClock : IPeerClock
    {
        public static FixedClock Instance { get; } = new();

        public DateTimeOffset UtcNow => Now;
    }

    private sealed class SequentialRandom : IPeerRandomSource
    {
        private int id;

        public string CreateId() => $"id-{Interlocked.Increment(ref id)}";

        public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
    }
}
