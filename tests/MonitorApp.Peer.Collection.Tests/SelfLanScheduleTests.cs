using Xunit;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Collection.Tests;

public sealed class SelfLanScheduleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "lan-sched-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static SelfInterface Eth(string name, bool up = true, long? speed = 1000, string? duplex = "full", int? mtu = 1500, string subnet = "192.168.1.0/24") =>
        new(name, InterfaceMedium.Ethernet, up, speed, duplex, mtu, "aa:bb:cc:00:11:22", up ? [subnet] : [], null, null);

    private static SelfInterfaceSnapshot Snap(DateTimeOffset at, params SelfInterface[] nics) =>
        new(SelfCollectionState.Collected, null, nics, [], at);

    [Fact]
    public void LinkObservationsCarryStateSpeedDuplexAndMtuWithTruthfulUnavailableValues()
    {
        var wifiDown = new SelfInterface("wlan0", InterfaceMedium.WiFi, false, null, null, 1500, "aa:bb:cc:00:11:33", [], null, null);
        var overlay = new SelfInterface("tailscale0", InterfaceMedium.Overlay, true, null, null, 1280, null, [], null, null);
        var loopback = new SelfInterface("lo", InterfaceMedium.Loopback, true, null, null, 65536, null, [], null, null);
        var windowsLike = Eth("Ethernet", speed: 2500, duplex: null);

        var snapshot = LanLinkObservations.ToSnapshot(Snap(T0, Eth("eth0"), wifiDown, overlay, loopback, windowsLike));

        var metrics = snapshot.Metrics.ToDictionary(m => m.MetricId);
        Assert.True(metrics["lan.link.eth0.up"].Value!.BooleanValue);
        Assert.Equal(1000UL, metrics["lan.link.eth0.speed_mbps"].Value!.UnsignedIntegerValue);
        Assert.Equal(MetricUnit.MegabitsPerSecond, metrics["lan.link.eth0.speed_mbps"].Unit);
        Assert.True(metrics["lan.link.eth0.full_duplex"].Value!.BooleanValue);
        Assert.Equal(1500UL, metrics["lan.link.eth0.mtu"].Value!.UnsignedIntegerValue);
        Assert.Equal(MetricUnit.Bytes, metrics["lan.link.eth0.mtu"].Unit);

        Assert.False(metrics["lan.link.wlan0.up"].Value!.BooleanValue);
        Assert.Equal(ObservationQuality.Unavailable, metrics["lan.link.wlan0.speed_mbps"].Quality);
        Assert.Null(metrics["lan.link.wlan0.speed_mbps"].Value);
        Assert.Equal(ObservationQuality.Unavailable, metrics["lan.link.Ethernet.full_duplex"].Quality);
        Assert.Equal(2500UL, metrics["lan.link.Ethernet.speed_mbps"].Value!.UnsignedIntegerValue);

        Assert.Contains("lan.link.tailscale0.up", metrics.Keys);
        Assert.DoesNotContain("lan.link.tailscale0.speed_mbps", metrics.Keys);
        Assert.DoesNotContain(metrics.Keys, id => id.StartsWith("lan.link.lo.", StringComparison.Ordinal));
        Assert.All(snapshot.Metrics, m => Assert.NotEqual(MetricUnit.Unspecified, m.Unit));
        var health = Assert.Single(snapshot.Health);
        Assert.Equal((LanLinkObservations.HealthCapabilityId, CapabilityState.Available), (health.CapabilityId, health.State));
    }

    [Fact]
    public void UnsupportedAndFailedCollectionReportHealthWithoutInventingInterfaces()
    {
        var unsupported = LanLinkObservations.ToSnapshot(SelfInterfaceSnapshot.Unsupported("no sysfs at /sys/class/net", T0));
        var failed = LanLinkObservations.ToSnapshot(SelfInterfaceSnapshot.Failed("boom", T0));

        Assert.Empty(unsupported.Metrics);
        Assert.Equal(CapabilityState.Unsupported, Assert.Single(unsupported.Health).State);
        Assert.Empty(failed.Metrics);
        var status = Assert.Single(failed.Health);
        Assert.Equal(CapabilityState.Error, status.State);
        Assert.DoesNotContain("boom", status.DetailCode, StringComparison.Ordinal); // reason text is not exposed as a code
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task VanishedInterfaceEndsItsRelationshipsWhileCurrentOnesKeepTheirOriginalStart()
    {
        var store = await NewStoreAsync();
        var time = new StepTime(T0);
        var source = new SwitchableCollector(Snap(T0, Eth("eth0"), Eth("eth1", subnet: "10.0.0.0/24")));
        var collector = new SelfLanCollector("peer-a", source, store, time);

        await collector.CollectAndPersistAsync();
        var eth0Attach = RelationId("eth0", LanRelationshipKind.AttachedTo, LanEntityIds.Peer("peer-a"));
        var eth1Attach = RelationId("eth1", LanRelationshipKind.AttachedTo, LanEntityIds.Peer("peer-a"));
        var startedAt = (await Relationship(store, eth1Attach)).ValidFrom;

        time.Now = T0.AddMinutes(1);
        source.Snapshot = Snap(time.Now, Eth("eth0"));
        await collector.CollectAndPersistAsync();

        var ended = await Relationship(store, eth1Attach);
        Assert.Equal(time.Now, ended.ValidTo);
        Assert.Equal(startedAt, ended.ValidFrom);
        Assert.True(collector.LastEndedRelationshipCount >= 2); // attachment plus segment membership (and its uplink placeholder)
        var kept = await Relationship(store, eth0Attach);
        Assert.Null(kept.ValidTo);
        Assert.Equal(T0, kept.ValidFrom); // a second run with a later clock must not rebind the original start
        var segmentMember = await Relationship(store, RelationId("eth1", LanRelationshipKind.MemberOfSegment, LanEntityIds.Segment("peer-a", "10.0.0.0/24")));
        Assert.Equal(time.Now, segmentMember.ValidTo);
        Assert.DoesNotContain(await store.ReadOpenRelationshipsByObserverAsync("peer-a"), r => r.SourceEntityId.Contains(":if:eth1", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task UnreadableHostChangesNothingAndAReappearingInterfaceIsReopenedUnderItsOriginalStart()
    {
        var store = await NewStoreAsync();
        var time = new StepTime(T0);
        var source = new SwitchableCollector(Snap(T0, Eth("eth0")));
        var collector = new SelfLanCollector("peer-a", source, store, time);
        await collector.CollectAndPersistAsync();
        var attach = RelationId("eth0", LanRelationshipKind.AttachedTo, LanEntityIds.Peer("peer-a"));

        time.Now = T0.AddMinutes(1);
        source.Snapshot = SelfInterfaceSnapshot.Failed("transient", time.Now);
        await collector.CollectAndPersistAsync();
        Assert.Null((await Relationship(store, attach)).ValidTo);
        Assert.Equal(0, collector.LastEndedRelationshipCount);

        time.Now = T0.AddMinutes(2);
        source.Snapshot = Snap(time.Now);
        await collector.CollectAndPersistAsync();
        Assert.Equal(time.Now, (await Relationship(store, attach)).ValidTo);

        time.Now = T0.AddMinutes(3);
        source.Snapshot = Snap(time.Now, Eth("eth0"));
        await collector.CollectAndPersistAsync();
        var reopened = await Relationship(store, attach);
        Assert.Null(reopened.ValidTo);
        Assert.Equal(T0, reopened.ValidFrom);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task OtherObserversRelationshipsAreNeverEnded()
    {
        var store = await NewStoreAsync();
        var time = new StepTime(T0);
        var source = new SwitchableCollector(Snap(T0, Eth("eth0")));
        await new SelfLanCollector("peer-a", source, store, time).CollectAndPersistAsync();
        var otherSource = new SwitchableCollector(Snap(T0, Eth("eth0")));
        var other = new SelfLanCollector("peer-b", otherSource, store, time);
        await other.CollectAndPersistAsync();

        time.Now = T0.AddMinutes(1);
        otherSource.Snapshot = Snap(time.Now);
        await other.CollectAndPersistAsync();

        Assert.NotEmpty(await store.ReadOpenRelationshipsByObserverAsync("peer-a"));
        Assert.Empty(await store.ReadOpenRelationshipsByObserverAsync("peer-b"));
    }

    private static string RelationId(string nic, LanRelationshipKind kind, string target) =>
        $"{LanEntityIds.Interface("peer-a", nic)}|{kind}|{target}";

    private static async Task<RelationshipObservation> Relationship(PeerStore store, string id)
    {
        var source = id[..id.IndexOf('|', StringComparison.Ordinal)];
        return Assert.Single(await store.ReadRelationshipsAsync(source), r => r.RelationshipId == id);
    }

    private async Task<PeerStore> NewStoreAsync()
    {
        Directory.CreateDirectory(dir);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(dir, "peer.db")));
        await store.InitializeAsync();
        return store;
    }

    private sealed class SwitchableCollector(SelfInterfaceSnapshot initial) : ISelfInterfaceCollector
    {
        public SelfInterfaceSnapshot Snapshot { get; set; } = initial;

        public SelfInterfaceSnapshot Collect(DateTimeOffset now) => Snapshot;
    }

    private sealed class StepTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
