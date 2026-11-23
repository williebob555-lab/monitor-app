using MonitorApp.Client.Core.Scene;
using Xunit;

namespace MonitorApp.Client.Core.Tests.Scene;

[Trait("Category", "Unit")]
public sealed class LayoutSearchSampleTests
{
    private static SceneNode Node(string id, SceneNodeKind kind = SceneNodeKind.Peer, string? parent = null, string? label = null) =>
        new(id, label ?? id, kind, SceneHealth.Healthy, "ok", parent, [], 0, null);

    private static double Distance(ScenePoint a, ScenePoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static void AssertSpaced(IReadOnlyDictionary<string, ScenePoint> layout)
    {
        var points = layout.Values.ToList();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                Assert.True(Distance(points[i], points[j]) >= StableLayout.MinSpacing - 1e-6, $"{i} and {j} too close");
            }
        }
    }

    private static List<SceneNode> Family(int peers, params string[] extra)
    {
        var nodes = new List<SceneNode> { Node("local", SceneNodeKind.LocalPeer) };
        nodes.AddRange(Enumerable.Range(0, peers).Select(i => Node($"peer-{i}")));
        nodes.AddRange(extra.Select(e => Node(e, SceneNodeKind.Device, "peer-0")));
        return nodes;
    }

    [Fact]
    public void LocalPeerSitsAtCentreAndChildrenNearTheirParent()
    {
        var layout = new StableLayout().Place(Family(4, "dev-a", "dev-b"));
        Assert.Equal(new ScenePoint(0, 0), layout["local"]);
        Assert.All(["dev-a", "dev-b"], d => Assert.InRange(Distance(layout[d], layout["peer-0"]), 1.6, 4));
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(Distance(layout[$"peer-{i}"], layout["local"]) >= 4 - 1e-6));
        AssertSpaced(layout);
    }

    [Fact]
    public void ExistingNodesNeverMoveWhenANodeAppears()
    {
        var layout = new StableLayout();
        var before = layout.Place(Family(5, "dev-a"));

        var after = layout.Place([.. Family(5, "dev-a"), Node("peer-new"), Node("dev-new", SceneNodeKind.Device, "peer-0")]);

        foreach (var (id, point) in before)
        {
            Assert.Equal(point, after[id]);
        }

        Assert.Equal(before.Count + 2, after.Count);
        AssertSpaced(after);
    }

    [Fact]
    public void PlacementDoesNotDependOnArrivalOrder()
    {
        var a = new StableLayout().Place(Family(6, "d1", "d2"));
        var b = new StableLayout().Place(Family(6, "d1", "d2").AsEnumerable().Reverse().ToList());
        Assert.Equal(a.OrderBy(p => p.Key).ToList(), b.OrderBy(p => p.Key).ToList());
    }

    [Fact]
    public void PinnedPositionsAreHonouredAndOthersAvoidThem()
    {
        var store = new InMemoryLayoutStore();
        var pin = new StableLayout(store);
        pin.Pin("peer-1", new ScenePoint(4, 0));
        pin.Pin("local", new ScenePoint(10, 10));

        var layout = new StableLayout(store).Place(Family(8));

        Assert.Equal(new ScenePoint(4, 0), layout["peer-1"]);
        Assert.Equal(new ScenePoint(10, 10), layout["local"]);
        AssertSpaced(layout);
    }

    [Fact]
    public void ARemovedNodeKeepsItsSlotReservedAndReturnsToIt()
    {
        var layout = new StableLayout();
        var first = layout.Place(Family(3));
        layout.Place([Node("local", SceneNodeKind.LocalPeer)]);
        var other = layout.Place([Node("local", SceneNodeKind.LocalPeer), Node("someone-else")]);
        Assert.DoesNotContain(first.Values.Skip(1), p => p == other["someone-else"]);
        Assert.Equal(first["peer-1"], layout.Place(Family(3))["peer-1"]);
    }

    [Fact]
    public void JsonFileStoreRoundTripsAtomicallyAndToleratesGarbage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "layout-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "layout.json");
        try
        {
            var store = new JsonFileLayoutStore(path);
            Assert.Empty(store.Load());

            store.Save(new Dictionary<string, ScenePoint> { ["a"] = new(1.5, -2.25), ["b"] = new(0, 3) });
            store.Save(new Dictionary<string, ScenePoint> { ["a"] = new(1.5, -2.25), ["b"] = new(0, 3), ["c"] = new(9, 9) });

            var loaded = new JsonFileLayoutStore(path).Load();
            Assert.Equal(3, loaded.Count);
            Assert.Equal(new ScenePoint(1.5, -2.25), loaded["a"]);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temp files left behind

            File.WriteAllText(path, "{ not json");
            Assert.Empty(store.Load());

            // End to end: a layout persisted by one StableLayout is restored by the next.
            var persisted = new StableLayout(new JsonFileLayoutStore(path)).Place(Family(3));
            var restored = new StableLayout(new JsonFileLayoutStore(path)).Place(Family(3));
            Assert.Equal(persisted.OrderBy(p => p.Key).ToList(), restored.OrderBy(p => p.Key).ToList());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void SearchJumpsToExactMetricsAndRanksSpecificHitsFirst()
    {
        var graph = SampleEnvironment.Create(SceneTestData.Now);

        var battery = SceneSearch.Find(graph, "battery");
        Assert.Equal(SceneSearchKind.Metric, battery[0].Kind);
        Assert.StartsWith("android.battery.", battery[0].SignalId, StringComparison.Ordinal);
        var level = SceneSearch.Find(graph, "battery level");
        Assert.Equal(SignalIds.BatteryLevel, level[0].SignalId);
        Assert.Contains("Battery level", level[0].DisplayText, StringComparison.Ordinal);
        Assert.Contains(battery, r => r.Kind == SceneSearchKind.Layer);

        var cpu = SceneSearch.Find(graph, "laptop cpu");
        Assert.Equal(SampleEnvironment.Laptop, cpu[0].NodeId);
        Assert.Equal(SignalIds.CpuUtilization, cpu[0].SignalId);
        Assert.All(cpu, r => Assert.Equal(SampleEnvironment.Laptop, r.NodeId));

        var node = SceneSearch.Find(graph, "home server");
        Assert.Equal(SceneSearchKind.Node, node[0].Kind);
        Assert.Equal(SampleEnvironment.Server, node[0].NodeId);
        Assert.DoesNotContain(node, r => r.Kind == SceneSearchKind.Metric);

        Assert.Empty(SceneSearch.Find(graph, "nonexistent-thing"));
        Assert.Empty(SceneSearch.Find(graph, "   "));
    }

    [Fact]
    public void SampleEnvironmentTellsTheSpecStory()
    {
        var graph = SampleEnvironment.Create(SceneTestData.Now);

        Assert.Equal(SceneHealth.Stale, graph.Find(SampleEnvironment.Tablet)!.Health);
        Assert.Equal(SceneHealth.Offline, graph.Find(SampleEnvironment.Fedora)!.Health);
        Assert.Equal(SceneHealth.Degraded, graph.Find(SampleEnvironment.Backup)!.Health);
        Assert.Equal(SceneHealth.Degraded, graph.Find(SampleEnvironment.MediaServer)!.Health);
        Assert.Equal(SceneHealth.Healthy, graph.Find(SampleEnvironment.Syncthing)!.Health);
        Assert.Equal(SceneHealth.Degraded, graph.Links.Single(l => l.ToId == SampleEnvironment.Phone).Health);
        Assert.Equal(SceneHealth.Healthy, graph.Links.Single(l => l.ToId == SampleEnvironment.Server && l.Kind == SceneLinkKind.Replication).Health);
        Assert.Contains(graph.Links, l => l.Kind == SceneLinkKind.Dependency && l.FromId == SampleEnvironment.MediaServer && l.ToId == SampleEnvironment.Backup);
        Assert.All(graph.Nodes.Where(n => n.Kind == SceneNodeKind.Service), s => Assert.Equal(SampleEnvironment.Server, s.ParentId));
        Assert.All(graph.Nodes, n => Assert.True(graph.Layout.ContainsKey(n.Id)));
        AssertSpaced(graph.Layout);
        Assert.Equal(0.39, graph.Find(SampleEnvironment.Server)!.Layers.Single(l => l.Kind == SceneLayerKind.Memory).Fill!.Value, 2);
    }
}
