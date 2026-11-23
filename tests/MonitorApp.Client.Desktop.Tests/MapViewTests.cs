using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;
using MonitorApp.Client.Ui.Views;
using MonitorApp.Client.Ui.Workspace;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

public sealed class MapViewTests
{
    [AvaloniaFact]
    public void TheMapSwitchShowsTheGraphAndASelectedDeviceDivesIn()
    {
        using var model = new MainViewModel(new FakeEnvironmentSource());
        var window = Show(model, 1400, 900);

        Assert.True(model.Shell.IsDive);
        Assert.Empty(window.GetVisualDescendants().OfType<SchematicCanvas>());
        model.Shell.ToggleMapCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.Shell.IsMap);
        Assert.False(model.Shell.IsDive);
        var map = window.GetVisualDescendants().OfType<SchematicCanvas>().Single();
        Assert.Contains(map.Scene.Nodes, n => n.Id == FakeEnvironmentSource.ServerId);
        Assert.Contains(map.Scene.Links, l => l.FromId == FakeEnvironmentSource.ServerId || l.ToId == FakeEnvironmentSource.ServerId);

        // Selecting on the map fills the shared selection (the inspector); "Dive in" goes to that device in the Dive.
        map.SelectedNodeId = FakeEnvironmentSource.ServerId;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FakeEnvironmentSource.ServerId, model.Environment.SelectedNode?.Id);
        model.Shell.DiveIntoSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.Shell.IsDive);
        Assert.Equal(FakeEnvironmentSource.ServerId, model.Shell.Dive.NodeId);

        // The outline and the map are exclusive presentations.
        model.Shell.ToggleMapCommand.Execute(null);
        model.Shell.ToggleOutlineCommand.Execute(null);
        Assert.False(model.Shell.MapOpen);
        Assert.False(model.Shell.IsMap);
        window.Close();
    }

    [Trait("Category", "Manual")]
#if MANUAL_SHOTS
    [AvaloniaFact]
#else
    [AvaloniaFact(Skip = "Manual: run with -p:ManualShots=true to render screenshots.")]
#endif
    public void RenderMapScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("MONITOR_UI_SHOTS_DIR") ?? "/tmp/claude-0/shots";
        Directory.CreateDirectory(dir);
        foreach (var (w, h, name) in new[] { (1400d, 900d, "map-wide-1400x900"), (390d, 844d, "map-phone-390x844") })
        {
            using var model = new MainViewModel(new FakeEnvironmentSource());
            var window = Show(model, w, h);
            model.Shell.ToggleMapCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            model.Environment.SelectedNodeId = FakeEnvironmentSource.ServerId;
            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(20);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            window.GetVisualDescendants().OfType<SchematicCanvas>().Single().CompleteAnimations();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, name + ".png"));
            window.Close();
        }
    }

    private static Window Show(MainViewModel model, double width, double height)
    {
        model.Environment.RefreshAsync().GetAwaiter().GetResult();
        model.Shell.GoTo(ShellMode.Ecosystem);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
