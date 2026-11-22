using MonitorApp.Client.Core.Scene;
using Xunit;
using static MonitorApp.Client.Core.Tests.Scene.SceneTestData;

namespace MonitorApp.Client.Core.Tests.Scene;

[Trait("Category", "Unit")]
public sealed class ProjectorTests
{
    private static SceneGraph Project(
        IReadOnlyList<TrustedPeerDto> peers, StateDto state, NetworkRates? rates = null, bool historical = false) =>
        SceneProjector.Project(Runtime(), peers, state, rates, Now, historical);

    [Fact]
    public void BuildsPeersDevicesAndTypedLinksFromRealisticData()
    {
        var peers = new[] { PeerDto("peer-b", "Phone"), PeerDto("peer-a", null), PeerDto("peer-gone", "Old laptop", "Revoked") };
        var state = State(
            Number("peer-local", "peer:peer-local", SignalIds.CpuUtilization, 0.2, "Ratio"),
            Number("peer-b", "peer:peer-b", SignalIds.BatteryLevel, 0.4, "Ratio"),
            Flag("peer-b", "peer:peer-b", SignalIds.BatteryCharging, false),
            Number("peer-local", "nas-1", SignalIds.StorageFree, 1e12, "Bytes"),
            Number("peer-local", "nas-1", SignalIds.StorageTotal, 4e12, "Bytes"),
            Number("peer-gone", "peer:peer-gone", SignalIds.CpuUtilization, 0.9, "Ratio"),
            Number("peer-gone", "printer", SignalIds.CpuUtilization, 0.9, "Ratio"));

        var graph = Project(peers, state);

        Assert.Equal(["peer-local", "peer-a", "peer-b", "nas-1"], graph.Nodes.Select(n => n.Id));
        Assert.Equal("This device", graph.Find("peer-local")!.Label);
        Assert.Equal(SceneNodeKind.LocalPeer, graph.Find("peer-local")!.Kind);
        Assert.Equal("peer-a", graph.Find("peer-a")!.Label);
        Assert.Equal("Phone", graph.Find("peer-b")!.Label);
        Assert.Null(graph.Find("peer-gone"));
        Assert.Null(graph.Find("printer"));

        var nas = graph.Find("nas-1")!;
        Assert.Equal(SceneNodeKind.Device, nas.Kind);
        Assert.Equal("peer-local", nas.ParentId);
        Assert.Equal(SceneLayerKind.Storage, Assert.Single(nas.Layers).Kind);
        Assert.Equal(0.75, nas.Layers[0].Fill);

        Assert.Equal(SceneHealth.Unknown, graph.Find("peer-a")!.Health);
        Assert.Equal("No readings received", graph.Find("peer-a")!.HealthReason);
        Assert.Equal(SceneHealth.Healthy, graph.Find("peer-b")!.Health);

        Assert.Equal(
            ["replication:peer-local:peer-a", "replication:peer-local:peer-b", "hosting:peer-local:nas-1"],
            graph.Links.Select(l => l.Id));
        Assert.Equal(SceneLinkKind.Replication, graph.Links[0].Kind);
        Assert.Equal(SceneHealth.Unknown, graph.Links[0].Health);
        Assert.Equal(SceneLinkKind.Hosting, graph.Links[2].Kind);
        Assert.Empty(graph.Layout);
    }

    [Fact]
    public void StaleAndOfflineAreDistinctAndRevokedIsNotShown()
    {
        var state = State(
            Number("p-stale", "peer:p-stale", SignalIds.CpuUtilization, 0.2, "Ratio", 14 * 60, freshness: "Stale"),
            Number("p-off", "peer:p-off", SignalIds.CpuUtilization, 0.2, "Ratio", 3600, freshness: "Offline"));
        var graph = Project([PeerDto("p-stale"), PeerDto("p-off"), PeerDto("p-revoked", state: "Revoked")], state);

        Assert.Equal(SceneHealth.Stale, graph.Find("p-stale")!.Health);
        Assert.Equal("Last reading 14 min ago", graph.Find("p-stale")!.HealthReason);
        Assert.Equal(SceneHealth.Offline, graph.Find("p-off")!.Health);
        Assert.Equal(SceneHealth.Offline, graph.Links.Single(l => l.ToId == "p-off").Health);
        Assert.Null(graph.Find("p-revoked"));
        Assert.DoesNotContain(graph.Links, l => l.ToId == "p-revoked");
    }

    [Fact]
    public void ActivityComesFromRatesOnALogScaleAndIsZeroWithoutThem()
    {
        var state = State(Number("peer-a", "peer:peer-a", SignalIds.CpuUtilization, 0.2, "Ratio"));
        var peers = new[] { PeerDto("peer-a") };

        Assert.Equal(0, Project(peers, state).Find("peer-a")!.Activity);

        var rates = new NetworkRates(new Dictionary<string, double> { ["peer:peer-a"] = 100e6 });
        var graph = Project(peers, state, rates);
        Assert.Equal(1.0, graph.Find("peer-a")!.Activity, 6);
        Assert.Equal(1.0, graph.Links[0].Activity, 6);
        Assert.Equal(0.1, NetworkRates.ToActivity(1000), 6);
        Assert.Equal(0.64, NetworkRates.ToActivity(1e6), 6);
        Assert.Equal(0, NetworkRates.ToActivity(10));
    }

    [Fact]
    public void RatesComeFromCounterDeltasAndIgnoreResets()
    {
        static HistoryPointDto Counter(string signal, double value, int secondsAgo) =>
            Number("p", "peer:p", signal, value, "Bytes", secondsAgo) with { ObservedAt = Now.AddSeconds(-secondsAgo) };

        var rates = NetworkRates.FromHistory([
            Counter(SignalIds.NetworkBytesSent, 1_000_000, 100),
            Counter(SignalIds.NetworkBytesSent, 3_000_000, 0),
            Counter(SignalIds.NetworkBytesReceived, 9_000_000, 100),
            Counter(SignalIds.NetworkBytesReceived, 500, 0), // counter reset: contributes nothing
        ]);
        Assert.Equal(20_000, rates.BytesPerSecondBySubject["peer:p"], 6);
        Assert.Empty(NetworkRates.FromHistory([Counter(SignalIds.NetworkBytesSent, 5, 0)]).BytesPerSecondBySubject);
    }

    [Fact]
    public void HistoricalProjectionUsesTheQueriedInstant()
    {
        var asOf = Now.AddMinutes(-10);
        var state = State() with { AsOf = asOf };
        var graph = SceneProjector.Project(Runtime(), [], state, null, Now, true);
        Assert.True(graph.IsHistorical);
        Assert.Equal(asOf, graph.AsOf);
        Assert.Equal(Now, SceneProjector.Project(Runtime(), [], state, null, Now, false).AsOf);
    }

    [Fact]
    public void ProjectionIsDeterministicRegardlessOfInputOrder()
    {
        var points = new[]
        {
            Number("peer-a", "peer:peer-a", SignalIds.CpuUtilization, 0.2, "Ratio"),
            Number("peer-local", "dev-2", SignalIds.CpuUtilization, 0.2, "Ratio"),
            Number("peer-local", "dev-1", SignalIds.CpuUtilization, 0.2, "Ratio"),
        };
        var one = Project([PeerDto("peer-a"), PeerDto("peer-b")], State(points));
        var two = Project([PeerDto("peer-b"), PeerDto("peer-a")], State(points.Reverse().ToArray()));
        Assert.Equal(one.Nodes.Select(n => n.Id), two.Nodes.Select(n => n.Id));
        Assert.Equal(one.Links.Select(l => l.Id), two.Links.Select(l => l.Id));
    }
}
