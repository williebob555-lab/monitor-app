using System.Net;
using MonitorApp.Contracts;
using MonitorApp.Peer.Collection.Switches;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Collection.Tests;

public sealed class CiscoSg250Tests
{
    private const string SwitchId = "switch:rack-sg250";
    private const string Owner = "peer-rack";

    [Fact]
    public async Task MapsReportedPortsAndPvidThroughBridgePortIfIndexOnly()
    {
        // Bridge port 9 describes IF-MIB ifIndex 3, not interface 9.
        var snapshot = await CollectAsync(
            new(Sg250Oids.IfName + ".3", "STRING: \"gi1/0/3\""),
            new(Sg250Oids.IfName + ".9", "STRING: \"gi1/0/9\""),
            new(Sg250Oids.IfType + ".3", "INTEGER: ethernetCsmacd(6)"),
            new(Sg250Oids.IfType + ".9", "INTEGER: ethernetCsmacd(6)"),
            new(Sg250Oids.IfName + ".100", "STRING: \"vlan100\""),
            new(Sg250Oids.IfType + ".100", "INTEGER: l2vlan(135)"),
            new(Sg250Oids.IfAdminStatus + ".3", "INTEGER: up(1)"),
            new(Sg250Oids.IfOperStatus + ".3", "INTEGER: down(2)"),
            new(Sg250Oids.IfHighSpeed + ".3", "Gauge32: 1000"),
            new(Sg250Oids.IfHighSpeed + ".9", "Gauge32: 0"),
            new(Sg250Oids.BridgePortIfIndex + ".9", "INTEGER: 3"),
            new(Sg250Oids.BridgePortPvid + ".9", "INTEGER: 23"),
            new(Sg250Oids.BridgeAddress, "Hex-STRING: 00 11 22 33 44 55"));

        Assert.Equal(Sg250CollectionStatus.Available, snapshot.Status);
        Assert.Equal(SwitchId, snapshot.SwitchEntityId);
        Assert.Equal(Owner, snapshot.CollectorPeerId);
        Assert.Equal("00:11:22:33:44:55", snapshot.BridgeMac);

        var port3 = Assert.Single(snapshot.Ports, p => p.IfIndex == 3);
        Assert.Equal("gi1/0/3", port3.Name);
        Assert.True(port3.AdminUp is true);
        Assert.True(port3.OperUp is false);
        Assert.Equal(1000L, port3.SpeedMbps);
        Assert.Equal(23, port3.Pvid);

        var port9 = Assert.Single(snapshot.Ports, p => p.IfIndex == 9);
        Assert.Null(port9.SpeedMbps);  // zero means "unknown" in ifHighSpeed, not zero bandwidth
        Assert.Null(port9.Pvid);        // no bridge-port mapping => no guessed VLAN membership

        var strong = Assert.Single(snapshot.Topology.Claims, c => c.Strength == IdentityStrength.Strong);
        Assert.Equal(SwitchId, strong.EntityId);
        Assert.Equal("lan.switch.bridge_mac", strong.ClaimType);
        Assert.Equal("00:11:22:33:44:55", strong.NormalizedValue);
        Assert.Equal(Owner, strong.ObserverPeerId);

        Assert.Equal(3, snapshot.Topology.Entities.Count);
        Assert.DoesNotContain(snapshot.Ports, p => p.IfIndex == 100); // logical VLAN interface is not a port
        Assert.All(snapshot.Topology.Relationships, rel =>
        {
            Assert.Equal(LanEvidenceKind.Reported, rel.Contract.Evidence);
            Assert.True(LanTopologyValidator.Validate(rel.Contract).IsValid);
            Assert.Equal(Owner, rel.Observation.ObserverPeerId);
            Assert.Equal(RelationshipEvidenceKind.Reported, rel.Observation.EvidenceKind);
        });
        Assert.Equal(2, snapshot.Topology.Relationships.Count);
        Assert.DoesNotContain(snapshot.Topology.Relationships,
            rel => rel.Contract.Kind is LanRelationshipKind.UplinkTo or LanRelationshipKind.ConnectedToPort);
    }

    [Fact]
    public async Task WithoutChassisIdentityNeverClaimsIpAsStrongOrInfersUplink()
    {
        var snapshot = await CollectAsync(
            new(Sg250Oids.IfDescr + ".1", "STRING: Ethernet 1"),
            new(Sg250Oids.IfType + ".1", "INTEGER: ethernetCsmacd(6)"),
            new(Sg250Oids.IfOperStatus + ".1", "INTEGER: dormant(5)"),
            new(Sg250Oids.BridgePortPvid + ".1", "INTEGER: 40"),
            new(Sg250Oids.BridgeAddress, "Hex-STRING: 00 00 00 00 00 00"));
        Assert.Equal(Sg250CollectionStatus.Available, snapshot.Status);
        Assert.Null(snapshot.BridgeMac);
        Assert.DoesNotContain(snapshot.Topology.Claims, x => x.Strength == IdentityStrength.Strong);
        var port = Assert.Single(snapshot.Ports);
        Assert.Null(port.OperUp); // dormant is not necessarily link-down
        Assert.Null(port.Pvid);  // missing bridge-port -> ifIndex correspondence
        Assert.Single(snapshot.Topology.Relationships);
    }

    [Fact]
    public async Task LocallyAdministeredBridgeMacIsNotAStableSwitchIdentity()
    {
        var snapshot = await CollectAsync(
            new(Sg250Oids.IfDescr + ".1", "STRING: ethernet1"),
            new(Sg250Oids.IfType + ".1", "INTEGER: ethernetCsmacd(6)"),
            new(Sg250Oids.BridgeAddress, "Hex-STRING: 02 11 22 33 44 55"));
        Assert.Equal(Sg250CollectionStatus.Available, snapshot.Status);
        Assert.Null(snapshot.BridgeMac);
        Assert.DoesNotContain(snapshot.Topology.Claims, c => c.Strength == IdentityStrength.Strong);
    }

    [Fact]
    public async Task FailedProbeReturnsEmptyExplicitStateAndNeverLeakedExceptions()
    {
        var denied = await new CiscoSg250Adapter(SwitchId, Owner,
            new StubReader(new Sg250ReadException(Sg250CollectionStatus.PermissionDenied))).CollectAsync();
        Assert.Equal(Sg250CollectionStatus.PermissionDenied, denied.Status);
        Assert.Empty(denied.Ports);
        Assert.Empty(denied.Topology.Relationships);
        Assert.Equal("SNMP read not authorized.", denied.SafeReason);

        var unexpected = await new CiscoSg250Adapter(SwitchId, Owner,
            new StubReader(new IOException("defAuthPassphrase=do-not-disclose"))).CollectAsync();
        Assert.Equal(Sg250CollectionStatus.Error, unexpected.Status);
        Assert.DoesNotContain("do-not-disclose", unexpected.SafeReason);
        Assert.Empty(unexpected.Topology.Claims);

        var invalidSuccess = await new CiscoSg250Adapter(SwitchId, Owner,
            new StubReader(new Sg250ReadException(Sg250CollectionStatus.Available))).CollectAsync();
        Assert.Equal(Sg250CollectionStatus.Error, invalidSuccess.Status);
        Assert.Empty(invalidSuccess.Ports);
        Assert.Equal("Switch probe failed.", invalidSuccess.SafeReason);

        var missing = await CollectAsync(new Sg250SnmpValue(Sg250Oids.IfHighSpeed + ".1", "Gauge32: 1000"));
        Assert.Equal(Sg250CollectionStatus.Unavailable, missing.Status);
        Assert.Empty(missing.Ports);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.31.255.5", true)]
    [InlineData("192.168.1.2", true)]
    [InlineData("169.254.1.2", true)]
    [InlineData("172.32.0.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    public void NetSnmpReaderRejectsNonPrivateTargets(string address, bool expected) =>
        Assert.Equal(expected, NetSnmpV3Sg250Reader.IsPrivateManagementAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task NetSnmpReaderRefusesACredentialsFolderOthersCanReadAndOddUserNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sg250-conf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var conf = Path.Combine(dir, "snmp.conf");
            await File.WriteAllTextAsync(conf, "defAuthPassphrase test");
            var reader = new NetSnmpV3Sg250Reader(IPAddress.Parse("192.168.1.2"), "monitor", dir, "/nonexistent/snmpbulkwalk", "/nonexistent/snmpget");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
                Assert.False(NetSnmpV3Sg250Reader.IsOwnerOnly(dir));
                var refused = await Assert.ThrowsAsync<Sg250ReadException>(() => reader.ReadAsync());
                Assert.Equal(Sg250CollectionStatus.PermissionDenied, refused.Status);

                File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Assert.True(NetSnmpV3Sg250Reader.IsOwnerOnly(dir));
                // Owner-only: it gets as far as starting Net-SNMP, which is not installed here.
                var missing = await Assert.ThrowsAsync<Sg250ReadException>(() => reader.ReadAsync());
                Assert.Equal(Sg250CollectionStatus.Unavailable, missing.Status);
            }

            Assert.Throws<ArgumentException>(() => new NetSnmpV3Sg250Reader(IPAddress.Parse("192.168.1.2"), "-v1 x", dir, "/a", "/b"));
            Assert.Throws<ArgumentException>(() => new NetSnmpV3Sg250Reader(IPAddress.Parse("192.168.1.2"), new string('a', 33), dir, "/a", "/b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CliParserAcceptsOnlyNumericOidsAndPreservesTypeWithoutLeakingInput()
    {
        Assert.True(NetSnmpV3Sg250Reader.TryParseLine(
            ".1.3.6.1.2.1.2.2.1.8.3 = INTEGER: up(1)", out var good));
        Assert.Equal(Sg250Oids.IfOperStatus + ".3", good!.Oid);
        Assert.Equal("INTEGER: up(1)", good.Value);
        Assert.False(NetSnmpV3Sg250Reader.TryParseLine(
            "IF-MIB::ifOperStatus.3 = INTEGER: up(1)", out _));
        Assert.False(NetSnmpV3Sg250Reader.TryParseLine("Timeout: No Response from 192.168.1.2", out _));
    }

    private static async Task<Sg250Snapshot> CollectAsync(params Sg250SnmpValue[] samples) =>
        await new CiscoSg250Adapter(SwitchId, Owner, new StubReader(samples)).CollectAsync();

    private sealed class StubReader : ISg250SnmpReader
    {
        private readonly IReadOnlyList<Sg250SnmpValue>? data;
        private readonly Exception? failure;

        public StubReader(params Sg250SnmpValue[] samples) => data = samples;
        public StubReader(Exception failure) => this.failure = failure;

        public Task<IReadOnlyList<Sg250SnmpValue>> ReadAsync(CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(data!) : Task.FromException<IReadOnlyList<Sg250SnmpValue>>(failure);
    }
}
