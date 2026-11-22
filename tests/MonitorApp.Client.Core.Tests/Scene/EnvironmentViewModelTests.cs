using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests.Scene;

[Trait("Category", "Integration")]
public sealed class EnvironmentViewModelTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task ProjectsTheRealNodeSelectsAndTimeTravels()
    {
        await using var node = await NodeFixture.CreateAsync();
        var now = NodeFixture.Now;
        await node.AddNumberAsync(now.AddMinutes(-1), 0.5, signal: SignalIds.CpuUtilization);
        await node.AddUnavailableAsync(now.AddMinutes(-1), signal: SignalIds.BatteryLevel);
        using var connection = new NodeConnection(node.Address);
        var store = new InMemoryLayoutStore();
        var model = new EnvironmentViewModel(connection, store, new FixedTime(now));

        await model.RefreshAsync();

        Assert.Null(model.ErrorMessage);
        Assert.Equal("Environment", model.Title);
        Assert.Equal(["peer-local", "peer-trusted", "host-a"], model.Scene.Nodes.Select(n => n.Id));
        Assert.Equal("This device", model.Scene.Find("peer-local")!.Label);
        Assert.Equal("Trusted workstation", model.Scene.Find("peer-trusted")!.Label);
        var device = model.Scene.Find("host-a")!;
        // A reading the device does not provide (battery level here) is shown as Unavailable but is not a fault.
        Assert.Equal(SceneHealth.Healthy, device.Health);
        Assert.Equal("Unavailable", device.Layers.Single(l => l.Kind == SceneLayerKind.Power).Readings.Single().DisplayValue);
        Assert.Equal(0.5, device.Layers.Single(l => l.Kind == SceneLayerKind.Compute).Fill);
        Assert.Null(device.Layers.Single(l => l.Kind == SceneLayerKind.Power).Fill);
        Assert.Equal("3 devices · 2 unknown", model.Summary);
        Assert.False(model.IsHistorical);
        Assert.All(model.Scene.Nodes, n => Assert.True(model.Scene.Layout.ContainsKey(n.Id)));

        // Outline mirrors the scene: the device is nested under the peer that holds it.
        Assert.Equal(["peer-local", "peer-trusted"], model.Outline.Select(o => o.NodeId));
        Assert.Equal("host-a", Assert.Single(model.Outline[0].Children).NodeId);
        Assert.Contains("Healthy", model.Outline[0].Children[0].AccessibleText, StringComparison.Ordinal);

        // Keyboard navigation walks the outline depth first and wraps.
        model.SelectNextCommand.Execute(null);
        Assert.Equal("peer-local", model.SelectedNodeId);
        model.SelectNextCommand.Execute(null);
        Assert.Equal("host-a", model.SelectedNodeId);
        Assert.Same(model.Scene.Find("host-a"), model.SelectedNode);
        model.SelectNextCommand.Execute(null);
        model.SelectNextCommand.Execute(null);
        Assert.Equal("peer-local", model.SelectedNodeId);
        model.SelectPreviousCommand.Execute(null);
        Assert.Equal("peer-trusted", model.SelectedNodeId);

        // Pinning is remembered by the store and survives a new view model and a refresh.
        model.PinNodeCommand.Execute(new SceneNodeMoved("peer-trusted", new ScenePoint(-7, 2)));
        Assert.Equal(new ScenePoint(-7, 2), model.Scene.Layout["peer-trusted"]);
        var second = new EnvironmentViewModel(connection, store, new FixedTime(now));
        await second.RefreshAsync();
        Assert.Equal(new ScenePoint(-7, 2), second.Scene.Layout["peer-trusted"]);
        Assert.Equal(model.Scene.Layout["host-a"], second.Scene.Layout["host-a"]);

        // Time travel before any data existed: the device is gone, the view says so, and Return to now restores it.
        await model.GoToAsync(now.AddHours(-2));
        Assert.True(model.IsHistorical);
        Assert.True(model.Scene.IsHistorical);
        Assert.Null(model.Scene.Find("host-a"));
        Assert.Equal("2 devices · 2 unknown", model.Summary);
        await model.ReturnToNowCommand.ExecuteAsync(null);
        Assert.False(model.IsHistorical);
        Assert.NotNull(model.Scene.Find("host-a"));
    }

    [Fact]
    public async Task LiveRunPollsUntilCancelled()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = new NodeConnection(node.Address);
        var model = new EnvironmentViewModel(connection, timeProvider: new FixedTime(NodeFixture.Now));
        using var cts = new CancellationTokenSource();

        var run = model.RunLiveAsync(TimeSpan.FromMilliseconds(50), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (model.Scene.Nodes.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.StartsWith("Polling", model.UpdateMode, StringComparison.Ordinal);
        await cts.CancelAsync();
        await run;
        Assert.Equal(2, model.Scene.Nodes.Count);
        Assert.Equal("Manual refresh", model.UpdateMode);
    }
}
