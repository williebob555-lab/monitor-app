using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Ui.Workspace;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests.Workspace;

public sealed class SchematicCanvasTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Recorder : System.Windows.Input.ICommand
    {
        public List<SceneNodeMoved> Moves { get; } = [];

#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Moves.Add((SceneNodeMoved)parameter!);
    }

    [AvaloniaFact]
    public void SampleSceneSelectsDragsExplodesAndSurvivesTelemetry()
    {
        var recorder = new Recorder();
        var clock = new SteppingClock(Now);
        var canvas = new SchematicCanvas { Scene = SampleEnvironment.Create(Now), NodeMovedCommand = recorder, Clock = clock };
        var window = new Window { Width = 1200, Height = 760, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        canvas.CompleteAnimations();
        var state = canvas.State;
        Assert.True(state.Camera.Zoom > 0, "fitted camera");
        Assert.True(state.ScreenRect(SampleEnvironment.Server).Width > 20, $"server card has size, zoom {state.Camera.Zoom}");

        // Click selects the node under the pointer.
        var serverCentre = state.ScreenRect(SampleEnvironment.Server).Center;
        window.MouseDown(serverCentre, MouseButton.Left);
        window.MouseUp(serverCentre, MouseButton.Left);
        Assert.True(canvas.SelectedNodeId == SampleEnvironment.Server, $"click selected '{canvas.SelectedNodeId}' at {serverCentre}");

        // Click on a wire selects the link and clears the node.
        var route = state.Routes.First(r => r.Link.Id.StartsWith("replication:" + SampleEnvironment.Laptop + ":" + SampleEnvironment.Server, StringComparison.Ordinal));
        var onWire = SchematicGeometry.Along(route.Points, 0.5).Position;
        Assert.Null(state.HitNode(onWire));
        window.MouseDown(onWire, MouseButton.Left);
        window.MouseUp(onWire, MouseButton.Left);
        Assert.True(canvas.SelectedLinkId == route.Link.Id && canvas.SelectedNodeId is null, $"link selected: {canvas.SelectedLinkId}/{canvas.SelectedNodeId}");

        // Drag the host: it and its three services move together, snapped, and the command fires on release.
        var cameraBefore = (state.Camera.Center, state.Camera.Zoom);
        var start = state.ScreenRect(SampleEnvironment.Server).Center;
        window.MouseDown(start, MouseButton.Left);
        foreach (var step in new[] { 10, 20, 30, 40 })
        {
            window.MouseMove(new Point(start.X + step, start.Y + (step * 0.75)), RawInputModifiers.LeftMouseButton);
        }

        window.MouseUp(new Point(start.X + 40, start.Y + 30), MouseButton.Left);
        Assert.Equal(4, recorder.Moves.Count);
        Assert.Equal(cameraBefore, (state.Camera.Center, state.Camera.Zoom));
        var others = SampleEnvironment.Create(Now).Layout.Where(kv => !recorder.Moves.Any(m => m.NodeId == kv.Key)).Select(kv => kv.Value).ToList();
        foreach (var move in recorder.Moves)
        {
            var onGridOrAligned = IsSnapped(move.Position.X, others.Select(o => o.X)) && IsSnapped(move.Position.Y, others.Select(o => o.Y));
            Assert.True(onGridOrAligned || move.NodeId != SampleEnvironment.Server, $"{move.NodeId} released at unsnapped {move.Position}");
        }

        var moved = recorder.Moves.Single(m => m.NodeId == SampleEnvironment.Server).Position;
        Assert.True(moved.X > 4.5, $"server moved right of its start: {moved}");

        // Double-click explodes the server; the planner only yields real layers; Escape reassembles.
        var c = state.ScreenRect(SampleEnvironment.Server).Center;
        clock.Step = TimeSpan.FromMilliseconds(50);
        window.MouseDown(c, MouseButton.Left);
        window.MouseUp(c, MouseButton.Left);
        window.MouseDown(c, MouseButton.Left);
        window.MouseUp(c, MouseButton.Left);
        clock.Step = TimeSpan.FromSeconds(1);
        canvas.CompleteAnimations();
        Assert.Equal(SampleEnvironment.Server, canvas.ExplodedNodeId);
        Assert.Equal(1.0, state.ExplodeProgress);

        // Telemetry update with the same layout: camera, drags and exploded state stay.
        var centre = state.Camera.Center;
        var zoom = state.Camera.Zoom;
        var updated = SampleEnvironment.Create(Now.AddSeconds(5));
        canvas.Scene = updated with { Layout = new Dictionary<string, ScenePoint>(SampleEnvironment.Create(Now).Layout) };
        canvas.CompleteAnimations();
        Assert.Equal((centre, zoom), (state.Camera.Center, state.Camera.Zoom));
        Assert.Equal(SampleEnvironment.Server, canvas.ExplodedNodeId);
        Assert.Equal(moved.X, state.Position(SampleEnvironment.Server).X, 6);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        canvas.CompleteAnimations();
        Assert.Null(canvas.ExplodedNodeId);
        Assert.Equal(0.0, state.ExplodeProgress);

        // Keyboard: arrows walk the stable order, Enter explodes, history draws without breaking.
        canvas.SetCurrentValue(SchematicCanvas.SelectedNodeIdProperty, null);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        var first = canvas.SelectedNodeId;
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(first is not null && canvas.SelectedNodeId is not null && first != canvas.SelectedNodeId, $"arrow moved {first} -> {canvas.SelectedNodeId}");
        canvas.IsHistorical = true;
        canvas.ReducedMotion = true;
        Assert.False(canvas.Advance(0.016), "with reduced motion and nothing animating the loop can stop");
        window.Close();
    }

    private static bool IsSnapped(double v, IEnumerable<double> others) =>
        Math.Abs((v / SchematicSnapper.GridStep) - Math.Round(v / SchematicSnapper.GridStep)) < 1e-9 || others.Any(o => Math.Abs(o - v) < 1e-9);

    /// <summary>Wall-clock stand-in: each read advances by <see cref="Step"/>, so taps are far apart unless a test narrows it.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public TimeSpan Step { get; set; } = TimeSpan.FromSeconds(1);

        public override DateTimeOffset GetUtcNow() => now += Step;
    }
}
