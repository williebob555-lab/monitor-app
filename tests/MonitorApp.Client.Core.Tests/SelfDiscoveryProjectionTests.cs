using Google.Protobuf;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Contracts.V1;
using MonitorApp.Model;
using MonitorApp.Peer.Persistence;
using Xunit;
using static MonitorApp.Client.Core.Tests.Scene.SceneTestData;

namespace MonitorApp.Client.Core.Tests;

/// <summary>Devices the computers found (net:{mac}, ts:{id}) on the Map and as things in the shared model.</summary>
public sealed class SelfDiscoveryProjectionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void MapNamesFoundDevicesHangsThemOffTheRouterAndDoesNotDrawMembersTwice()
    {
        var state = State(
            Number("peer-local", "peer:peer-local", SignalIds.CpuUtilization, 0.1, "Ratio"),
            Flag("peer-local", "peer:peer-local", "service.tailscale.running", true),
            Text("peer-local", "net:a06391000001", "net.device.name", "Home Router"),
            Flag("peer-local", "net:a06391000001", "net.device.gateway", true),
            Text("peer-local", "net:a06391000001", "net.device.network", "192.168.1.0/24"),
            Text("peer-local", "net:b827eb123456", "net.device.name", "raspberrypi"),
            Flag("peer-local", "net:b827eb123456", "net.device.gateway", false),
            Text("peer-local", "net:b827eb123456", "net.device.network", "192.168.1.0/24"),
            Text("peer-local", "ts:n1", "net.device.name", "Laptop"),          // the paired laptop: not drawn twice
            Text("peer-local", "ts:n2", "net.device.name", "grandma-pc"));

        var graph = SceneProjector.Project(Runtime(), [PeerDto("peer-laptop", "Laptop")], state, null, Now, false, localLabel: "Desktop");

        var router = graph.Nodes.Single(n => n.Id == "net:a06391000001");
        Assert.Equal(("Home Router", "peer-local"), (router.Label, router.ParentId));
        var pi = graph.Nodes.Single(n => n.Id == "net:b827eb123456");
        Assert.Equal(("raspberrypi", "net:a06391000001"), (pi.Label, pi.ParentId));
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "ts:n1");
        Assert.Equal("grandma-pc", graph.Nodes.Single(n => n.Id == "ts:n2").Label);
        Assert.Contains(graph.Nodes, n => n.Id == "svc:peer-local:tailscale" && n.Label == "Tailscale" && n.ParentId == "peer-local");
        Assert.Equal("Name on the network", SignalCatalog.Describe("net.device.name").Label);
        Assert.Equal("Drive 0 size", SignalCatalog.Describe("hardware.drive.0.size").Label);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task FoundDevicesBecomeOneThingEachLinkedToTheRouter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "found-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var peers = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
            await peers.InitializeAsync();
            await using var model = new ModelStore(Path.Combine(directory, "model.db"));
            await model.InitializeAsync();
            ulong sequence = 1;
            async Task Text(string origin, string subject, string signal, string value) =>
                await Append(origin, subject, signal, new MetricValue { StringValue = value });
            async Task Flag(string origin, string subject, string signal, bool value) =>
                await Append(origin, subject, signal, new MetricValue { BooleanValue = value });
            async Task Append(string origin, string subject, string signal, MetricValue value)
            {
                var envelope = new TelemetryEnvelope { Metric = new MetricSample { MetricId = signal, Value = value, Quality = ObservationQuality.Valid } };
                await peers.AppendOriginRecordAsync(new OriginRecordWrite(origin, "stream-" + origin, sequence++, subject, "monitor.discovery", signal,
                    envelope.ToByteArray(), new byte[64], Now, Now, origin, RecordProvenance.Replicated, "Valid"));
            }

            // Two computers found the same router; one also found a printer.
            foreach (var origin in new[] { "peer-desktop", "peer-laptop" })
            {
                await Text(origin, "net:a06391000001", "net.device.name", "Home Router");
                await Text(origin, "net:a06391000001", "net.device.mac", "a0:63:91:00:00:01");
                await Text(origin, "net:a06391000001", "net.device.kind", "device.network.router");
                await Text(origin, "net:a06391000001", "net.device.network", "192.168.1.0/24");
                await Flag(origin, "net:a06391000001", "net.device.gateway", true);
            }

            await Text("peer-desktop", "net:00c0ee000002", "net.device.name", "Office Printer");
            await Text("peer-desktop", "net:00c0ee000002", "net.device.mac", "00:c0:ee:00:00:02");
            await Text("peer-desktop", "net:00c0ee000002", "net.device.kind", "device.printer");
            await Text("peer-desktop", "net:00c0ee000002", "net.device.network", "192.168.1.0/24");
            await Flag("peer-desktop", "net:00c0ee000002", "net.device.gateway", false);

            MonitoringBridge.ValueReader = MonitorApp.Contracts.ObservationValues.Read;
            await MonitoringBridge.SyncFromPeerStoreAsync(peers, model);
            await MonitoringBridge.SyncFromPeerStoreAsync(peers, model);

            var loaded = await model.LoadAsync();
            var router = Assert.Single(loaded.Objects, o => o.Name == "Home Router");
            Assert.Equal("device.network.router", router.KindId);
            var printer = Assert.Single(loaded.Objects, o => o.Name == "Office Printer");
            Assert.Equal("device.printer", printer.KindId);
            Assert.Single(loaded.Relationships, r => r.SourceId == printer.Id && r.TargetId == router.Id && r.TypeId == "connects-to");
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }
}
