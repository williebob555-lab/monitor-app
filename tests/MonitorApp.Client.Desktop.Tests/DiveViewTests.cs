using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;
using MonitorApp.Client.Ui.Controls;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

/// <summary>The Dive rendered in the shell and driven like a person would: tap tiles, press Escape, send an action.</summary>
public sealed class DiveViewTests
{
    [AvaloniaFact]
    public async Task HomeGridAdaptsTapDivesEscapeClimbsOutAndActionsReachTheDevice()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new FakeControlEnvironmentSource { PairingsHeldBy = SampleEnvironment.Server };
        source.PendingPairings.Add(new PendingPairingDto("pair-1", "peer-watch", "Pixel Watch", "FEEDBEEF", "PendingApproval", now, now.AddMinutes(5)));
        using var model = new MainViewModel(source);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = 1400, Height = 860, Content = shell };
        window.Show();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        await model.Shell.Dive.RefreshApprovalsAsync();
        Pump();
        var dive = model.Shell.Dive;

        // Wide: four columns, every tile rendered as a touch-sized button, no side inspector, the outline one switch away.
        var grid = HomeGrid(shell);
        Assert.True(grid.Columns == 4, $"wide grid has {grid.Columns} columns");
        var tiles = TileButtons(shell);
        Assert.True(tiles.Count == dive.Home.Tiles.Count && tiles.Count == 7, $"{tiles.Count} tile buttons for {dive.Home.Tiles.Count} tiles");
        Assert.All(tiles, t => Assert.True(t.Bounds.Height >= 44 && t.Bounds.Width >= 44, $"tile {t.DataContext} is {t.Bounds.Size}"));
        Assert.False(Find<Border>(shell, "InspectorHost").IsVisible, "the Dive has no side inspector");
        Assert.True(Find<Button>(shell, "OutlineToggle").IsVisible, "the outline stays reachable");
        Assert.Equal(["Home"], model.Shell.Breadcrumbs.Select(b => b.Text));

        // Phone: one column filling the width, 44 px targets, then back to wide.
        window.Width = 390;
        window.Height = 844;
        Pump();
        grid = HomeGrid(shell);
        Assert.True(grid.Columns == 1, $"phone grid has {grid.Columns} columns");
        Assert.All(TileButtons(shell), t => Assert.True(t.Bounds.Width >= grid.Bounds.Width - 1 && t.Bounds.Height >= 44, $"phone tile {t.Bounds.Size} in {grid.Bounds.Size}"));
        window.Width = 1400;
        window.Height = 860;
        Pump();

        // Tap the server tile: the Dive enters it, selection follows (live 1 s interest), the breadcrumb and page show it.
        Tap(window, TileButtons(shell).Single(t => t.DataContext is DiveDeviceTile { NodeId: SampleEnvironment.Server }));
        Assert.True(dive.NodeId == SampleEnvironment.Server && model.Environment.SelectedNodeId == SampleEnvironment.Server, $"tap went to {dive.NodeId}");
        Assert.Equal(["Home", "Home server"], model.Shell.Breadcrumbs.Select(b => b.Text));
        Assert.Equal("Home server", Find<TextBlock>(shell, "DiveTitle").Text);
        Assert.Contains(source.Viewing, v => v.StartsWith($"begin:{SampleEnvironment.Server}:", StringComparison.Ordinal));

        // An action on the device routes to the device through the control source; the outcome shows on the page.
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Collection paused", false);
        var pause = Find<Border>(shell, "DiveControls").GetVisualDescendants().OfType<Button>()
            .Single(b => b.CommandParameter is ControlActionRow { Kind: ControlActionKind.PauseCollection });
        Assert.True(pause.IsEnabled, "live: the action is enabled");
        await ((IAsyncRelayCommand)pause.Command!).ExecuteAsync(pause.CommandParameter);
        Pump();
        Assert.Equal([$"pause:{SampleEnvironment.Server}"], source.Sent);
        Assert.Equal("Done: Collection paused", Find<TextBlock>(shell, "DiveOutcome").Text);

        // Investigate dives to the full disk; Escape climbs part -> device -> Home and then stays.
        Tap(window, Find<Button>(shell, "Investigate"));
        Assert.True(dive.LayerId == "storage", $"Investigate went to {dive.NodeId}/{dive.LayerId}");
        Assert.Equal(["Home", "Home server", "Storage"], model.Shell.Breadcrumbs.Select(b => b.Text));
        Assert.Contains("94% full", Find<TextBlock>(shell, "PartStatement").Text, StringComparison.Ordinal);
        foreach (var expected in new string?[] { SampleEnvironment.Server, null, null })
        {
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Assert.True(dive.NodeId == expected && dive.LayerId is null, $"Escape should reach {expected ?? "Home"}, is at {dive.NodeId}/{dive.LayerId}");
        }

        // The attention tile's "Dive to the cause" lands on the same part; the pending pairing tile opens its computer.
        Tap(window, Find<Button>(shell, "AttentionTile"));
        Assert.True(dive.NodeId == SampleEnvironment.Server && dive.LayerId == "storage", $"cause at {dive.NodeId}/{dive.LayerId}");
        dive.GoHome();
        Pump();
        Tap(window, TileButtons(shell).Single(t => t.DataContext is DiveApprovalTile));
        Assert.Equal(SampleEnvironment.Server, dive.NodeId);
        await dive.Controls.LoadTask;
        Pump();
        Assert.True(Find<ItemsControl>(shell, "DivePairings").IsEffectivelyVisible, "the pairing request is decided on the computer that holds it");

        // Phone: a back chip names where it climbs to.
        window.Width = 390;
        Pump();
        var back = Find<Button>(shell, "DiveBack");
        Assert.True(back.IsEffectivelyVisible && dive.BackText == "Home", $"back chip visible={back.IsEffectivelyVisible} text={dive.BackText}");
        window.Width = 1400;
        Pump();

        // Recorded: hatched band with the moment in words; actions disabled.
        model.Environment.AsOf = now.AddHours(-2);
        Pump();
        Assert.True(Find<Border>(shell, "RecordedBand").IsVisible, "recorded band shown");
        Assert.StartsWith("RECORDED · as of ", Find<TextBlock>(shell, "RecordedChip").Text, StringComparison.Ordinal);
        Assert.All(
            Find<Border>(shell, "DiveControls").GetVisualDescendants().OfType<Button>().Where(b => b.CommandParameter is ControlActionRow),
            b => Assert.False(b.IsEnabled, "actions must not act on history"));
        Assert.Single(source.Sent);

        // With motion on, a tap zooms in and the page always comes to rest at full size and opacity (headless runs the frames at once).
        await model.Environment.ReturnToNowAsync();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        dive.GoHome();
        shell.ReducedMotion = false;
        Pump();
        Tap(window, TileButtons(shell).Single(t => t.DataContext is DiveDeviceTile { NodeId: SampleEnvironment.Laptop }));
        var page = shell.GetVisualDescendants().OfType<DiveView>().Single().GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PageHost");
        Assert.True(
            dive.NodeId == SampleEnvironment.Laptop && page.RenderTransform is null && page.Opacity == 1,
            $"zoom ended at {dive.NodeId}, transform {page.RenderTransform}, opacity {page.Opacity}");
        window.Close();
    }

    private static DiveTileGrid HomeGrid(WorkspaceShell shell) =>
        Find<ItemsControl>(shell, "HomeTiles").GetVisualDescendants().OfType<DiveTileGrid>().First();

    private static List<Button> TileButtons(WorkspaceShell shell) =>
        Find<ItemsControl>(shell, "HomeTiles").GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();

    /// <summary>A real pointer press and release in the middle of the control.</summary>
    private static void Tap(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{control} is not on screen");
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    private static T Find<T>(WorkspaceShell shell, string name)
        where T : Control =>
        shell.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
