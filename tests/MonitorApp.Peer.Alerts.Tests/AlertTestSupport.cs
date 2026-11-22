using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Alerts.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Set(DateTimeOffset value) => now = value;
}

internal sealed class ScriptedCollector : ISystemSnapshotCollector
{
    public SystemSnapshot? Next { get; set; }

    public ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Next ?? throw new InvalidOperationException("No snapshot scripted."));
}

/// <summary>A desktop peer over a real SQLite file, collecting through the production ingestion bridge.</summary>
internal sealed class TestPeer : ILocalPeerIdentityProvider, IAsyncDisposable
{
    private readonly EcdsaPeerIdentityKey key = EcdsaPeerIdentityKey.Create();
    private readonly ScriptedCollector script = new();
    private readonly ManualTime clock;

    private TestPeer(string root, DateTimeOffset start)
    {
        Root = root;
        clock = new ManualTime(start);
        var spki = key.ExportSubjectPublicKeyInfo();
        var fingerprint = SHA256.HashData(spki);
        PublicIdentity = new PeerPublicIdentity("peer-" + Convert.ToHexString(fingerprint, 0, 10).ToLowerInvariant(), spki, fingerprint);
        DatabasePath = Path.Combine(root, "peer.db");
        Store = new PeerStore(PeerStoreOptions.Create(DatabasePath));
        Alerts = new AlertStore(AlertStoreOptions.Create(DatabasePath));
        Bridge = new PeerSystemMetricsIngestionBridge(
            Store, this, script,
            PeerCollectionOptions.Default($"peer:{PublicIdentity.PeerId}", Guid.NewGuid().ToString("N")),
            () => clock.GetUtcNow());
    }

    public string Root { get; }

    public string DatabasePath { get; }

    public PeerPublicIdentity PublicIdentity { get; }

    public IPeerIdentityKey IdentityKey => key;

    public PeerStore Store { get; }

    public AlertStore Alerts { get; }

    public TimeProvider Clock => clock;

    public PeerSystemMetricsIngestionBridge Bridge { get; }

    public string Subject => $"peer:{PublicIdentity.PeerId}";

    public static async Task<TestPeer> CreateAsync(DateTimeOffset start)
    {
        var peer = new TestPeer(Path.Combine(Path.GetTempPath(), "monitor-alerts-" + Guid.NewGuid().ToString("N")), start);
        await peer.Store.InitializeAsync();
        await peer.Alerts.InitializeAsync();
        await peer.Store.SetLocalIdentityAsync(new LocalPeerIdentity(
            peer.PublicIdentity.PeerId, peer.PublicIdentity.PublicKeySpki, peer.PublicIdentity.PublicKeyFingerprint,
            "test", "origin-" + peer.PublicIdentity.PeerId, 1, start));
        return peer;
    }

    public void SetNow(DateTimeOffset now) => clock.Set(now);

    public AlertEvaluator Evaluator(AlertRulesConfig config) => new(Store, Alerts, config, Clock);

    /// <summary>Collects one scripted snapshot observed at <paramref name="at"/> and advances this peer's clock to it.</summary>
    public async Task CollectAsync(DateTimeOffset at, double? cpu = null, ulong? freeBytes = null,
        ObservationQuality quality = ObservationQuality.Valid, ulong? memoryUsed = null)
    {
        clock.Set(at);
        var metrics = new List<CollectedMetric>();
        if (cpu is { } c)
            metrics.Add(new CollectedMetric("system.cpu.utilization", MetricUnit.Ratio, quality, new MetricValue { DoubleValue = c }));
        if (freeBytes is { } f)
        {
            metrics.Add(new CollectedMetric("system.storage.total", MetricUnit.Bytes, quality, new MetricValue { UnsignedIntegerValue = 100 }));
            metrics.Add(new CollectedMetric("system.storage.free", MetricUnit.Bytes, quality, new MetricValue { UnsignedIntegerValue = f }));
        }

        if (memoryUsed is { } used)
        {
            metrics.Add(new CollectedMetric("system.memory.total", MetricUnit.Bytes, quality, new MetricValue { UnsignedIntegerValue = 100 }));
            metrics.Add(new CollectedMetric("system.memory.used", MetricUnit.Bytes, quality, new MetricValue { UnsignedIntegerValue = used }));
        }

        if (quality is ObservationQuality.Unavailable or ObservationQuality.Error)
        {
            metrics = [.. metrics.Select(item => item with { Value = null })];
        }

        script.Next = new SystemSnapshot(Timestamp.FromDateTimeOffset(at), metrics, []);
        var result = await Bridge.RunOnceAsync();
        if (result.AppendedRecords == 0) throw new InvalidOperationException("Scripted snapshot appended nothing.");
    }

    public async Task TrustAsync(PeerPublicIdentity remote, DateTimeOffset pairedAt, string? name = null) =>
        await Store.UpsertTrustedPeerAsync(new TrustedPeer(remote.PeerId, remote.PublicKeySpki, remote.PublicKeyFingerprint,
            name, PeerTrustState.Active, pairedAt, null));

    public async ValueTask DisposeAsync()
    {
        key.Dispose();
        await Task.CompletedTask;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
