using Xunit;
using System.Net.NetworkInformation;
using MonitorApp.Contracts;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Collection.Tests;

public sealed class SelfLanTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "lan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private const string Route = "Iface\tDestination\tGateway\tFlags\tRefCnt\tUse\tMetric\tMask\tMTU\tWindow\tIRTT\n" +
        "eth0\t00000000\t0101A8C0\t0003\t0\t0\t100\t00000000\t0\t0\t0\n" +
        "eth0\t0001A8C0\t00000000\t0001\t0\t0\t100\t00FFFFFF\t0\t0\t0\n";

    [Fact]
    public void ParsesDefaultRouteGatewayFromProcNetRoute()
    {
        var gw = Assert.Single(SelfInterfaceParsers.ParseDefaultRoutes(Route));
        Assert.Equal(new SelfGateway("192.168.1.1", "eth0"), gw);
    }

    [Fact]
    public void ParsesVlanConfigAndMac()
    {
        var v = SelfInterfaceParsers.ParseVlanConfig("VLAN Dev name\t | VLAN ID\nName-Type: VLAN_NAME_TYPE_RAW_PLUS_VID_NO_PAD\neth0.20        | 20  | eth0\n");
        Assert.Equal((20, "eth0"), v["eth0.20"]);
        Assert.Equal("aa:bb:cc:00:11:22", SelfInterfaceParsers.NormalizeMac("AA:BB:CC:00:11:22\n"));
        Assert.Null(SelfInterfaceParsers.NormalizeMac("00:00:00:00:00:00"));
    }

    [Theory]
    [InlineData("lo", null, InterfaceMedium.Loopback)]
    [InlineData("tailscale0", null, InterfaceMedium.Overlay)]
    [InlineData("wg0", null, InterfaceMedium.Overlay)]
    [InlineData("docker0", "bridge", InterfaceMedium.Bridge)]
    [InlineData("br-1a2b", null, InterfaceMedium.Bridge)]
    [InlineData("veth12", null, InterfaceMedium.Virtual)]
    [InlineData("eth0", null, InterfaceMedium.Other)]
    public void ClassifiesNonPhysicalInterfacesExplicitly(string name, string? devType, InterfaceMedium expected) =>
        Assert.Equal(expected, SelfInterfaceParsers.ClassifyByName(name, devType));

    [Fact]
    public void WindowsClassifierSeparatesOverlayVirtualAndPhysical()
    {
        Assert.Equal(InterfaceMedium.Overlay, WindowsSelfInterfaceCollector.ClassifyWindows(NetworkInterfaceType.Unknown, "Tailscale", "Tailscale Tunnel"));
        Assert.Equal(InterfaceMedium.Virtual, WindowsSelfInterfaceCollector.ClassifyWindows(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.Equal(InterfaceMedium.WiFi, WindowsSelfInterfaceCollector.ClassifyWindows(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel AX200"));
        Assert.Equal(InterfaceMedium.Ethernet, WindowsSelfInterfaceCollector.ClassifyWindows(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek"));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi-QoS Packet Scheduler-0000", "Intel AX200", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Local Area Connection* 6-WFP Native MAC Layer LightWeight Filter-0000", "Wi-Fi Direct", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Local Area Connection* 6", "Microsoft Wi-Fi Direct Virtual Adapter", true)]
    [InlineData(NetworkInterfaceType.Tunnel, "6to4 Adapter", "Microsoft 6to4 Adapter", true)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", "Teredo", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel AX200", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Realtek", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "Tailscale", "Tailscale Tunnel", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", false)]
    public void WindowsDriverPlumbingIsNotCollected(NetworkInterfaceType type, string name, string description, bool plumbing) =>
        Assert.Equal(plumbing, WindowsSelfInterfaceCollector.IsWindowsPlumbing(type, name, description));

    [Fact]
    public void LinuxCollectorReadsFixtureSysfs()
    {
        var sys = Path.Combine(dir, "net");
        Directory.CreateDirectory(sys);
        Make(sys, "eth0", "up", "1000", "full", "1500", "AA:BB:CC:00:11:22", "DEVTYPE=\n", device: true);
        Make(sys, "eth0.20", "up", "1000", "full", "1500", "aa:bb:cc:00:11:22", "DEVTYPE=vlan\n", device: false);
        Make(sys, "wlan0", "down", null, null, "1500", "aa:bb:cc:00:11:33", "DEVTYPE=wlan\n", device: true);
        Make(sys, "tailscale0", "unknown", null, null, "1280", "00:00:00:00:00:00", "", device: false);
        Make(sys, "lo", "unknown", null, null, "65536", "00:00:00:00:00:00", "", device: false);
        File.WriteAllText(Path.Combine(dir, "route"), Route);
        File.WriteAllText(Path.Combine(dir, "vlan"), "eth0.20 | 20 | eth0\n");
        var snap = new LinuxSelfInterfaceCollector(sys, Path.Combine(dir, "route"), Path.Combine(dir, "vlan"),
            n => n switch { "eth0" => ["192.168.1.0/24"], "eth0.20" => ["10.20.0.0/24"], _ => [] }).Collect(Now);

        Assert.Equal(SelfCollectionState.Collected, snap.State);
        var by = snap.Interfaces.ToDictionary(i => i.Name);
        Assert.True(by["eth0"].IsPhysical);
        Assert.Equal(1000, by["eth0"].SpeedMbps);
        Assert.Equal("full", by["eth0"].Duplex);
        Assert.Equal(1500, by["eth0"].Mtu);
        Assert.Equal("aa:bb:cc:00:11:22", by["eth0"].Mac);
        Assert.Equal((InterfaceMedium.Vlan, 20, "eth0"), (by["eth0.20"].Medium, by["eth0.20"].VlanId, by["eth0.20"].VlanParent));
        Assert.Equal(InterfaceMedium.WiFi, by["wlan0"].Medium);
        Assert.False(by["wlan0"].IsUp);
        Assert.False(by["tailscale0"].IsPhysical);
        Assert.True(by["tailscale0"].IsOverlay);
        Assert.Null(by["tailscale0"].Mac);
        Assert.Equal(InterfaceMedium.Loopback, by["lo"].Medium);
    }

    [Fact]
    public void LinuxCollectorReportsUnsupportedWhenSysfsMissing()
    {
        var snap = new LinuxSelfInterfaceCollector(Path.Combine(dir, "missing")).Collect(Now);
        Assert.Equal(SelfCollectionState.Unsupported, snap.State);
        Assert.Empty(snap.Interfaces);
    }

    [Fact]
    public void LinuxCollectorRunsAgainstRealSys()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/net")) return;
        var snap = SelfInterfaceCollectorFactory.Create().Collect(Now);
        Assert.True(snap.State == SelfCollectionState.Collected, $"State {snap.State}: {snap.Reason}");
        var lo = Assert.Single(snap.Interfaces, i => i.Name == "lo");
        Assert.Equal(InterfaceMedium.Loopback, lo.Medium);
        Assert.False(lo.IsPhysical);
        Assert.All(snap.Interfaces, i => Assert.True(i.Mtu is null or > 0));
        var fragment = LanTopologyBuilder.Build("peer-a", snap);
        Assert.All(fragment.Relationships, r => Assert.True(LanTopologyValidator.Validate(r.Contract).IsValid));
    }

    [Fact]
    [Trait("Category", "Linux")]
    public void UnreadableVlanAndRouteFilesLeaveCollectionSuccessfulWithUnknownMembership()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/net")) return;
        // /proc/self/mem exists but reading it from offset 0 fails (EIO), like a root-only
        // /proc/net/vlan/config does for the unprivileged service user.
        const string unreadable = "/proc/self/mem";
        var snap = new LinuxSelfInterfaceCollector(procRoute: unreadable, procVlanConfig: unreadable).Collect(Now);
        Assert.True(snap.State == SelfCollectionState.Collected, $"State {snap.State}: {snap.Reason}");
        Assert.Empty(snap.Gateways);
        Assert.All(snap.Interfaces, i => Assert.Null(i.VlanId));
    }

    private static SelfInterfaceSnapshot Snapshot(string cidr, string gw) => new(SelfCollectionState.Collected, null,
        [new SelfInterface("eth0", InterfaceMedium.Ethernet, true, 1000, "full", 1500, "aa:bb:cc:00:11:22", [cidr], null, null),
         new SelfInterface("eth0.20", InterfaceMedium.Vlan, true, 1000, "full", 1500, null, ["10.20.0.0/24"], 20, "eth0"),
         new SelfInterface("docker0", InterfaceMedium.Bridge, true, null, null, 1500, "02:42:00:00:00:01", ["172.17.0.0/16"], null, null),
         new SelfInterface("tailscale0", InterfaceMedium.Overlay, true, null, null, 1280, null, ["100.64.1.2/32"], null, null)],
        [new SelfGateway(gw, "eth0")], Now);

    [Fact]
    public void BuilderKeepsOverlayAndVirtualOutOfSegmentsAndOnlyInfersUpstream()
    {
        var f = LanTopologyBuilder.Build("peer-a", Snapshot("192.168.1.0/24", "192.168.1.1"));
        var rels = f.Relationships.Select(r => r.Contract).ToList();
        Assert.Contains(rels, r => r.Kind == LanRelationshipKind.AttachedTo && r.Source.Kind == LanEntityKind.OverlayInterface);
        Assert.DoesNotContain(rels, r => r.Source.EntityId.EndsWith("tailscale0", StringComparison.Ordinal) && r.Kind != LanRelationshipKind.AttachedTo);
        Assert.DoesNotContain(rels, r => r.Source.EntityId.EndsWith("docker0", StringComparison.Ordinal) && r.Kind != LanRelationshipKind.AttachedTo);
        Assert.Contains(rels, r => r.Kind == LanRelationshipKind.MemberOfVlan);
        Assert.Contains(rels, r => r.Kind == LanRelationshipKind.RoutesVia && r.Evidence == LanEvidenceKind.Reported);
        Assert.All(rels.Where(r => r.Target.Kind == LanEntityKind.UnknownUpstreamSegment), r => Assert.Equal(LanEvidenceKind.Inferred, r.Evidence));
        Assert.DoesNotContain(f.Entities, e => e.Kind is LanEntityKind.Switch or LanEntityKind.Port);
        // Only the physical interface's own MAC is a strong claim; the gateway has no MAC claim.
        var strong = f.Claims.Where(c => c.Strength == IdentityStrength.Strong).ToList();
        Assert.Single(strong);
        Assert.Equal(LanClaimTypes.InterfaceMac, strong[0].ClaimType);
        Assert.DoesNotContain(f.Claims, c => c.EntityId.Contains(":gw:", StringComparison.Ordinal) && c.ClaimType.Contains("mac", StringComparison.Ordinal));
        Assert.All(f.Claims, c => Assert.Equal("peer-a", c.ObserverPeerId));
    }

    [Fact]
    public void UnsupportedSnapshotProducesNoEntities()
    {
        var f = LanTopologyBuilder.Build("peer-a", SelfInterfaceSnapshot.Unsupported("n/a", Now));
        Assert.Empty(f.Entities);
        Assert.Empty(f.Relationships);
    }

    [Fact]
    public void SameSubnetAndGatewayIsProbableNotMergedAndIpOnlyNeverHardMerges()
    {
        var a = LanTopologyBuilder.Build("peer-a", Snapshot("192.168.1.0/24", "192.168.1.1"));
        var b = LanTopologyBuilder.Build("peer-b", Snapshot("192.168.1.0/24", "192.168.1.1"));
        var c = LanTopologyBuilder.Build("peer-c", Snapshot("192.168.1.0/24", "192.168.1.254"));
        var d = LanTopologyBuilder.Build("peer-d", Snapshot("192.168.5.0/24", "192.168.5.1"));
        var obs = new[] { a, b, c, d }
            .SelectMany(f => f.Entities.Where(e => e.Kind == LanEntityKind.Segment && e.Entity.EntityId.Contains("192.168", StringComparison.Ordinal))
                .Select(e => new LanSegmentReconciler.SegmentObservation(f.OwnerPeerId, e.Entity.EntityId, f.Claims.Where(x => x.EntityId == e.Entity.EntityId).ToList())))
            .ToList();
        var edges = LanSegmentReconciler.Reconcile(obs, Now);

        Assert.All(edges, e =>
        {
            Assert.Equal(LanRelationshipKind.ProbableSameSegment, e.Contract.Kind);
            Assert.True(e.Contract.Confidence < 1);
            Assert.Equal(LanEvidenceKind.Inferred, e.Contract.Evidence);
        });
        double Between(string x, string y) => edges.Single(e => e.Contract.RelationshipId.Contains(x, StringComparison.Ordinal) && e.Contract.RelationshipId.Contains(y, StringComparison.Ordinal)).Contract.Confidence;
        Assert.Equal(LanSegmentReconciler.SubnetAndGatewayConfidence, Between("peer-a", "peer-b"));
        Assert.Equal(LanSegmentReconciler.SubnetOnlyConfidence, Between("peer-a", "peer-c"));
        Assert.DoesNotContain(edges, e => e.Contract.RelationshipId.Contains("peer-d", StringComparison.Ordinal));
        // Entity ids stay per observer and weak IP-derived claims never count as shared strong identity.
        Assert.False(ObservedIdentityMatcher.ShareStrongIdentity(a.Claims, b.Claims.Where(x => x.Strength == IdentityStrength.Weak)));
        Assert.NotEqual(obs[0].SegmentEntityId, obs[1].SegmentEntityId);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CollectorPersistsOwnTopologyThroughPeerStoreAndIsIdempotent()
    {
        Directory.CreateDirectory(dir);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(dir, "peer.db")));
        await store.InitializeAsync();
        var collector = new SelfLanCollector("peer-a", new FixedCollector(Snapshot("192.168.1.0/24", "192.168.1.1")), store);
        var first = await collector.CollectAndPersistAsync();
        await collector.CollectAndPersistAsync();

        var claims = await store.ReadIdentityClaimsAsync(LanEntityIds.Interface("peer-a", "eth0"));
        Assert.Contains(claims, c => c.ClaimType == LanClaimTypes.InterfaceMac && c.Strength == IdentityStrength.Strong && c.ObserverPeerId == "peer-a");
        var seg = LanEntityIds.Segment("peer-a", "192.168.1.0/24");
        var rels = await store.ReadRelationshipsAsync(seg);
        Assert.Contains(rels, r => r.Kind == RelationshipKind.RoutesVia && r.EvidenceKind == RelationshipEvidenceKind.Reported);
        Assert.All(rels, r => Assert.Equal("peer-a", r.ObserverPeerId));
        Assert.Equal(first.Relationships.Count(r => r.Observation.SourceEntityId == seg || r.Observation.TargetEntityId == seg), rels.Count);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task IpClaimsCannotBecomeStrongIdentity()
    {
        Directory.CreateDirectory(dir);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(dir, "x.db")));
        await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddIdentityClaimAsync(
            new IdentityClaim("e", LanClaimTypes.GatewayIp, "192.168.1.1", IdentityStrength.Strong, "p", RelationshipEvidenceKind.Reported, 0.9, Now)));
    }

    private sealed class FixedCollector(SelfInterfaceSnapshot s) : ISelfInterfaceCollector
    {
        public SelfInterfaceSnapshot Collect(DateTimeOffset now) => s;
    }

    private static void Make(string root, string name, string oper, string? speed, string? duplex, string mtu, string mac, string uevent, bool device)
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "operstate"), oper + "\n");
        File.WriteAllText(Path.Combine(d, "mtu"), mtu + "\n");
        File.WriteAllText(Path.Combine(d, "address"), mac + "\n");
        File.WriteAllText(Path.Combine(d, "uevent"), uevent);
        if (speed is not null) File.WriteAllText(Path.Combine(d, "speed"), speed + "\n");
        if (duplex is not null) File.WriteAllText(Path.Combine(d, "duplex"), duplex + "\n");
        if (device) Directory.CreateDirectory(Path.Combine(d, "device"));
    }
}
