using Avalonia;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Ui.Workspace;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests.Workspace;

/// <summary>Pure math and planning behind the schematic: no UI thread, no pixels.</summary>
public sealed class SchematicLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SampleSceneRoutesPulsesSnapsAndPlansFromRealData()
    {
        var scene = SampleEnvironment.Create(Now);
        var sizer = new FlatCardRenderer();
        var rects = scene.Nodes.ToDictionary(
            n => n.Id,
            n =>
            {
                var s = sizer.NodeSize(n);
                var p = scene.Layout[n.Id];
                return new Rect(p.X - (s.Width / 2), p.Y - (s.Height / 2), s.Width, s.Height);
            });

        // Routing: every link ends exactly on a port that sits on the edge of its node, and the route starts and ends there.
        var ports = PortPlanner.Plan(rects, scene.Links);
        Assert.Equal(scene.Links.Count, ports.Count);
        foreach (var link in scene.Links)
        {
            var lp = ports[link.Id];
            AssertOnEdge(rects[link.FromId], lp.From.Position, $"{link.Id} from");
            AssertOnEdge(rects[link.ToId], lp.To.Position, $"{link.Id} to");
            var route = SchematicGeometry.Route(lp.From, lp.To, 0.2);
            Assert.True(SchematicGeometry.Distance(route[0], lp.From.Position) < 1e-6, $"{link.Id}: route starts at the from port");
            Assert.True(SchematicGeometry.Distance(route[^1], lp.To.Position) < 1e-6, $"{link.Id}: route ends at the to port");
            for (var i = 1; i < route.Count; i++)
            {
                Assert.True(Math.Abs(route[i].X - route[i - 1].X) < 1e-6 || Math.Abs(route[i].Y - route[i - 1].Y) < 1e-6,
                    $"{link.Id}: segment {i} is orthogonal");
            }
        }

        // Hit testing: card centre picks the node, a point on a wire picks the link, empty space picks nothing.
        var order = scene.Nodes.Select(n => new NodeBounds(n.Id, rects[n.Id])).ToList();
        foreach (var n in scene.Nodes)
        {
            Assert.Equal(n.Id, SchematicHitTest.Node(order, rects[n.Id].Center, 0));
        }

        var wire = SchematicGeometry.Route(ports[scene.Links[0].Id].From, ports[scene.Links[0].Id].To, 0.2);
        var onWire = SchematicGeometry.Along(wire, 0.5).Position;
        Assert.Equal(scene.Links[0].Id, SchematicHitTest.Link([new LinkPath(scene.Links[0].Id, wire)], onWire, 0.05));
        Assert.Null(SchematicHitTest.Node(order, new Point(100, 100), 0.1));

        // Pulses: only for real activity, never for 0, never toward offline or stale ends.
        foreach (var link in scene.Links)
        {
            var from = scene.Find(link.FromId)!.Health;
            var to = scene.Find(link.ToId)!.Health;
            var expected = link.Activity > 0 && from is not (SceneHealth.Offline or SceneHealth.Stale) && to is not (SceneHealth.Offline or SceneHealth.Stale);
            Assert.True(PulsePlanner.ShouldPulse(link, from, to) == expected, $"pulse decision for {link.Id} (activity {link.Activity}, {from}->{to})");
        }

        Assert.Equal(0, PulsePlanner.Count(0));
        Assert.True(PulsePlanner.Count(0.7) >= 1);
        Assert.All(Enumerable.Range(0, 5), i => Assert.InRange(PulsePlanner.Fraction(i, 5, 12.3, 0.5, false), 0, 1));

        // Snapping: aligns with another node's x, else falls on the eighth-unit grid.
        var aligned = SchematicSnapper.Snap(new Point(4.53, 1.0), [new Point(4.5, -3)], 0.1);
        Assert.Equal(4.5, aligned.Position.X);
        Assert.Contains(new Guide(true, 4.5), aligned.Guides);
        var grid = SchematicSnapper.Snap(new Point(1.06, 2.2), [new Point(9, 9)], 0.1);
        Assert.Equal(new Point(1.0, 2.25), grid.Position);

        // Camera: zoom keeps the point under the cursor fixed; focus easing converges.
        var cam = new SchematicCamera { Viewport = new Size(1000, 700) };
        cam.Fit(new Rect(-4, -5, 11, 9), 40, false);
        var screen = new Point(300, 200);
        var before = cam.ToWorld(screen);
        cam.ZoomAt(screen, 1.7);
        var after = cam.ToWorld(screen);
        Assert.True(SchematicGeometry.Distance(before, after) < 1e-9, "zoom about the cursor keeps the world point fixed");
        cam.AnimateTo(new Point(5, 5), 1.5);
        for (var i = 0; i < 400 && cam.Step(1.0 / 60, false); i++)
        {
        }

        Assert.False(cam.IsAnimating, "easing converges");
        Assert.Equal(1.5, cam.Zoom, 3);

        // Exploded plan: only components for layers present. Server has compute/memory/storage; phone has power/connectivity.
        var server = ExplodedPlanner.Plan(scene.Find(SampleEnvironment.Server)!).Select(c => c.Part).ToList();
        Assert.Contains(ExplodedPart.Cpu, server);
        Assert.Contains(ExplodedPart.Memory, server);
        Assert.Contains(ExplodedPart.Drive, server);
        Assert.DoesNotContain(ExplodedPart.Battery, server);
        Assert.DoesNotContain(ExplodedPart.Nic, server);

        var powerNetwork = scene.Find(SampleEnvironment.Phone)! with
        {
            Layers =
            [
                scene.Find(SampleEnvironment.Phone)!.Layers.First(l => l.Kind == SceneLayerKind.Power),
                scene.Find(SampleEnvironment.Laptop)!.Layers[0] with { Kind = SceneLayerKind.Network },
            ],
        };
        var parts = ExplodedPlanner.Plan(powerNetwork).Select(c => c.Part).ToList();
        Assert.Contains(ExplodedPart.Battery, parts);
        Assert.Contains(ExplodedPart.Nic, parts);
        Assert.DoesNotContain(ExplodedPart.Cpu, parts);
        Assert.DoesNotContain(ExplodedPart.Board, parts);
        Assert.False(ExplodedPlanner.CanExplode(powerNetwork with { Layers = [] }), "no layers, nothing to explode");
        Assert.All(ExplodedPlanner.Plan(scene.Find(SampleEnvironment.Server)!).Where(c => c.Part is not (ExplodedPart.Chassis or ExplodedPart.Board)),
            c => Assert.NotNull(c.Layer));

        // Silhouettes and failure: phone for android signals, the failed backup is distinguished from the merely degraded media server.
        Assert.Equal(Silhouette.Phone, SchematicModel.SilhouetteOf(scene.Find(SampleEnvironment.Phone)!));
        Assert.Equal(Silhouette.Service, SchematicModel.SilhouetteOf(scene.Find(SampleEnvironment.Backup)!));
        Assert.True(SchematicModel.IsFailed(scene.Find(SampleEnvironment.Backup)!));
        Assert.False(SchematicModel.IsFailed(scene.Find(SampleEnvironment.MediaServer)!));
    }

    private static void AssertOnEdge(Rect r, Point p, string what)
    {
        var onVertical = (Math.Abs(p.X - r.X) < 1e-6 || Math.Abs(p.X - r.Right) < 1e-6) && p.Y >= r.Y - 1e-6 && p.Y <= r.Bottom + 1e-6;
        var onHorizontal = (Math.Abs(p.Y - r.Y) < 1e-6 || Math.Abs(p.Y - r.Bottom) < 1e-6) && p.X >= r.X - 1e-6 && p.X <= r.Right + 1e-6;
        Assert.True(onVertical || onHorizontal, $"{what}: port {p} is not on the edge of {r}");
    }
}
