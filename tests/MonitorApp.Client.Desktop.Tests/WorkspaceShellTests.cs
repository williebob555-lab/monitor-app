using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;
using MonitorApp.Client.Ui.Controls;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

/// <summary>The workspace shell as a person meets it: rendered at real widths, driven through its controls.</summary>
public sealed class WorkspaceShellTests
{
    /// <summary>One width sweep: wide, medium, then phone; the right chrome at each, 44 px touch targets on the phone.</summary>
    [AvaloniaFact]
    public void ShellAdaptsFromWideToMediumToPhone()
    {
        using var model = new MainViewModel(new FakeEnvironmentSource());
        var (window, shell) = Show(model, 1400, 860);

        Assert.Equal(ShellLayoutClass.Wide, shell.LayoutClass);
        Assert.True(Find<Border>(shell, "RailPanel").IsVisible, "wide: rail visible");
        Assert.False(Find<Border>(shell, "BottomBar").IsVisible, "wide: no bottom bar");
        Assert.True(Find<TextBox>(shell, "SearchBox").IsVisible, "wide: toolbar search visible");
        Assert.Equal(5, Find<ListBox>(shell, "Rail").ItemCount);
        Assert.Equal("LIVE", Find<TextBlock>(shell, "LiveText").Text);

        window.Width = 860;
        Pump();
        Assert.Equal(ShellLayoutClass.Medium, shell.LayoutClass);
        Assert.True(Find<Border>(shell, "RailPanel").IsVisible, "medium: rail visible");
        Assert.False(Find<Border>(shell, "InspectorHost").IsVisible, "medium: inspector is an overlay, absent without a selection");

        window.Width = 390;
        window.Height = 844;
        Pump();
        Assert.Equal(ShellLayoutClass.Narrow, shell.LayoutClass);
        Assert.False(Find<Border>(shell, "RailPanel").IsVisible, "phone: no rail");
        Assert.True(Find<Border>(shell, "BottomBar").IsVisible, "phone: bottom bar");
        Assert.False(Find<TextBox>(shell, "SearchBox").IsVisible, "phone: toolbar reduced (no search box)");
        Assert.True(Find<TextBlock>(shell, "NarrowTitle").IsVisible, "phone: toolbar shows the title");
        Assert.Equal("LIVE", Find<TextBlock>(shell, "LiveText").Text);
        Assert.Equal(["Ecosystem", "Alerts", "History", "Search", "More"], model.Shell.Modes.Select(m => m.NarrowTitle));
        foreach (var item in Find<ListBox>(shell, "BottomNav").GetVisualDescendants().OfType<ListBoxItem>())
        {
            Assert.True(item.Bounds.Height >= 44, $"bottom nav item is {item.Bounds.Height} px high");
        }

        var pill = Find<Button>(shell, "LivePill");
        Assert.True(pill.Bounds.Height >= 44 && pill.Bounds.Width >= 44, $"pill is {pill.Bounds.Size}");
        window.Close();
    }

    /// <summary>
    /// The main journey: select in the outline, read the inspector, follow a relationship, see control tiers, travel to
    /// the past (pill, outline, controls disabled), return to live, then send a control and read its outcomes.
    /// </summary>
    [AvaloniaFact]
    public async Task SelectInspectTravelInTimeAndControl()
    {
        var source = new FakeControlEnvironmentSource
        {
            Outcome = new ControlOutcomeDto("Refused", ControlsViewModel.NotPermittedReason, "control is denied", false),
        };
        using var model = new MainViewModel(source);
        var (window, shell) = Show(model, 1400, 860);
        await model.Environment.RefreshAsync();
        Pump();

        // Select an outline item: the inspector fills in.
        var tree = shell.GetVisualDescendants().OfType<TreeView>().Single();
        Assert.Equal(model.Environment.Outline.Count, tree.ItemCount);
        var server = model.Environment.Outline.Single(item => item.Name == FakeEnvironmentSource.ServerName);
        Assert.Contains("Healthy", server.HealthText, StringComparison.Ordinal);
        tree.SelectedItem = server;
        Pump();
        Assert.Equal(FakeEnvironmentSource.ServerId, model.Environment.SelectedNodeId);
        var liveSignals = string.Join(',', InspectorViewModel.LiveSignalIds);
        Assert.Equal([$"begin:{FakeEnvironmentSource.ServerId}:{liveSignals}"], source.Viewing);
        Assert.True(model.Shell.Inspector.IsLiveSampling, "inspecting a live device shows the Live 1 s indicator");
        var inspector = Find<Border>(shell, "InspectorHost");
        Assert.True(Find<TextBlock>(shell, "LiveSampling").IsVisible);
        Assert.Equal("Live 1 s", Find<TextBlock>(shell, "LiveSampling").Text);
        Assert.True(inspector.IsVisible, "inspector docks when something is selected");
        var texts = inspector.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Equal(FakeEnvironmentSource.ServerName, inspector.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "InspectorTitle").Text);
        foreach (var expected in new[] { "Paired device", "Healthy", "CPU utilization", "18%" })
        {
            Assert.Contains(expected, texts);
        }

        Assert.Equal(model.Environment.SelectedNode!.Layers.Select(l => l.Title), model.Shell.Inspector.Layers.Select(l => l.Title));
        Assert.True(model.Shell.Inspector.Layers.Count >= 2, "compute and memory layers");
        foreach (var layer in model.Shell.Inspector.Layers)
        {
            Assert.Contains(layer.Title, texts);
        }

        Assert.Contains(texts, t => t is not null && t.StartsWith("Reported by the device itself, ", StringComparison.Ordinal));
        Assert.Contains(
            inspector.GetVisualDescendants().OfType<ProgressBar>(),
            bar => bar.IsVisible && Math.Abs(bar.Value - 12.4 / 32) < 0.001);

        // Controls: tier shown before acting, enabled while live.
        var buttons = ControlButtons(shell);
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, b => Assert.True(b.IsEnabled, "controls enabled while live"));
        var actions = model.Shell.Inspector.Controls.Actions;
        Assert.Contains(actions, a => a.Kind == ControlActionKind.PauseCollection && a.RequiresPresence && a.TierText.Contains("asks for your fingerprint", StringComparison.Ordinal));
        Assert.Contains(actions, a => a.Kind == ControlActionKind.ResumeCollection && !a.RequiresPresence);

        // History: pill, dashed outline and Return to live appear; controls refuse to act.
        var past = DateTimeOffset.UtcNow.AddHours(-3);
        Assert.False(Find<Panel>(shell, "HistoricalOutline").IsVisible);
        Assert.False(Find<Button>(shell, "ReturnToLive").IsEnabled, "nothing to return to while live");
        await model.Environment.GoToAsync(past);
        Pump();
        Assert.Equal("RECORDED " + past.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Find<TextBlock>(shell, "LiveText").Text);
        Assert.Contains("historical", Find<Button>(shell, "LivePill").Classes);
        Assert.True(Find<Panel>(shell, "HistoricalOutline").IsVisible, "historical outline on the workspace");
        Assert.True(model.Environment.Scene.IsHistorical);
        Assert.True(model.Environment.SelectedNodeId == FakeEnvironmentSource.ServerId, "selection survives time travel");
        Assert.All(ControlButtons(shell), b => Assert.False(b.IsEnabled, "controls must not act on history"));
        Assert.False(model.Shell.Inspector.Controls.RunCommand.CanExecute(actions[0]));
        Assert.Contains("disabled while viewing history", model.Shell.Inspector.Controls.HistoryNotice, StringComparison.Ordinal);
        Assert.Empty(source.Sent);
        Assert.Equal(
            [$"begin:{FakeEnvironmentSource.ServerId}:{liveSignals}", "end:" + FakeEnvironmentSource.ServerId],
            source.Viewing);
        Assert.False(model.Shell.Inspector.IsLiveSampling, "history is recorded data: no live sampling interest");
        Assert.False(Find<TextBlock>(shell, "LiveSampling").IsVisible, "indicator hidden in history");

        // Return to live clears all of it.
        var back = Find<Button>(shell, "ReturnToLive");
        Assert.True(back.IsEnabled);
        await ((IAsyncRelayCommand)back.Command!).ExecuteAsync(null);
        Pump();
        Assert.Equal("LIVE", Find<TextBlock>(shell, "LiveText").Text);
        Assert.DoesNotContain("historical", Find<Button>(shell, "LivePill").Classes);
        Assert.False(Find<Panel>(shell, "HistoricalOutline").IsVisible);
        Assert.All(ControlButtons(shell), b => Assert.True(b.IsEnabled, "controls back after return to live"));
        Assert.Equal(3, source.Viewing.Count);
        Assert.Equal($"begin:{FakeEnvironmentSource.ServerId}:{liveSignals}", source.Viewing[2]);

        // Scrubbing the timeline travels too, and the far right means live.
        var scrubber = Find<Slider>(shell, "Scrubber");
        scrubber.Value = 500;
        await model.Shell.Timeline.CommitAsync();
        Assert.InRange((DateTimeOffset.UtcNow - model.Environment.AsOf!.Value).TotalHours, 11.9, 12.1);
        scrubber.Value = TimelineViewModel.ScrubMaximum;
        await model.Shell.Timeline.CommitAsync();
        Assert.False(model.Environment.IsHistorical, "scrubber at the right end is live");
        Pump();

        // Not permitted is the expected default and names the computer to grant on.
        var pause = ControlButtons(shell).First();
        await ((IAsyncRelayCommand)pause.Command!).ExecuteAsync(pause.CommandParameter);
        Pump();
        Assert.Equal(["pause:" + FakeEnvironmentSource.ServerId], source.Sent);
        var outcome = Find<TextBlock>(shell, "ControlOutcome");
        Assert.Equal("Not allowed yet: grant this device on Home server", outcome.Text);
        Assert.Contains("error", outcome.Classes);

        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Collection resumed", false);
        var resume = ControlButtons(shell).Single(b => ((ControlActionRow)b.CommandParameter!).Kind == ControlActionKind.ResumeCollection);
        await ((IAsyncRelayCommand)resume.Command!).ExecuteAsync(resume.CommandParameter);
        Pump();
        Assert.Equal("Done: Collection resumed", outcome.Text);

        // Update approval and remote pairing: shown only when the computer says this device is permitted.
        var controls = model.Shell.Inspector.Controls;
        source.Sent.Clear();
        source.UpdateStatus = new RemoteUpdateStatusDto(false, false, "1.0.0", null, null, "Idle", null, false);
        controls.Update(model.Environment.SelectedNode);
        await controls.LoadTask;
        Pump();
        Assert.False(controls.HasPendingUpdate, "update must stay hidden while not permitted");
        Assert.False(controls.CanApproveUpdate, "approving while not permitted would prompt for a fingerprint before a refusal");
        Assert.False(Find<StackPanel>(shell, "UpdatePanel").IsVisible);

        const string Digest = "ab12";
        source.UpdateStatus = new RemoteUpdateStatusDto(true, true, "1.0.0", "1.1.0", Digest, "Staged", null, false);
        source.PendingPairings.Add(new PendingPairingDto("pair-1", "peer-new", "New phone", "FEEDBEEF", "PendingApproval", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));
        controls.Update(model.Environment.SelectedNode);
        await controls.LoadTask;
        Pump();
        Assert.True(controls.CanApproveUpdate, "permitted + staged update can be approved");
        Assert.Contains("1.1.0", controls.UpdateSummary, StringComparison.Ordinal);
        Assert.Contains("asks for your fingerprint", controls.UpdateTierText, StringComparison.Ordinal);
        Assert.True(Find<StackPanel>(shell, "UpdatePanel").IsVisible);
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Update approved", false);
        await controls.ApproveUpdateCommand.ExecuteAsync(null);
        await controls.ResolvePairingCommand.ExecuteAsync(Assert.Single(controls.PendingPairings));
        Assert.Equal(
            [$"update:{FakeEnvironmentSource.ServerId}:1.1.0:{Digest}", $"pairing:{FakeEnvironmentSource.ServerId}:approve:pair-1:FEEDBEEF"],
            source.Sent);

        // Relationships are links: following one moves the selection and the breadcrumbs.
        var link = inspector.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b.Command == model.Shell.Inspector.SelectRelatedCommand);
        link.Command!.Execute(link.CommandParameter);
        Pump();
        Assert.Equal(FakeEnvironmentSource.LocalId, model.Environment.SelectedNodeId);
        Assert.Equal(["Ecosystem", SceneProjector.LocalLabel], model.Shell.Breadcrumbs.Select(b => b.Text));
        Assert.False(model.Shell.Inspector.Controls.IsAvailable, "no controls for this device itself");
        Assert.Equal(
            ["end:" + FakeEnvironmentSource.ServerId, $"begin:local:{liveSignals}"],
            source.Viewing.TakeLast(2));

        // Detaching the shell ends interest so the device returns to its normal rate.
        window.Close();
        Assert.Equal("end:local", source.Viewing[^1]);
        Assert.False(model.Shell.Inspector.IsLiveSampling);
    }

    /// <summary>Search, keyboard shortcuts, mode navigation, the plain-source case and the schematic extension slot.</summary>
    [AvaloniaFact]
    public async Task SearchKeyboardNavigationAndTheCanvasSlot()
    {
        using var model = new MainViewModel(new FakeEnvironmentSource());
        var window = new Window { Width = 1400, Height = 860 };
        var shell = new WorkspaceShell
        {
            RunEnvironmentLive = false,
            DataContext = model,
            WorkspaceTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<EnvironmentViewModel>(
                (vm, _) => new TextBlock { Name = "FakeCanvas", Text = "canvas for " + vm?.Summary }),
        };
        window.Content = shell;
        window.Show();
        await model.Environment.RefreshAsync();
        Pump();

        // The canvas lands in WorkspaceHost and the outline stays beside it.
        var host = Find<ContentControl>(shell, "WorkspaceHost");
        Assert.Single(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "FakeCanvas");
        Assert.True(Find<Border>(shell, "OutlinePanel").IsVisible, "outline remains as the accessible alternative");
        Assert.Single(Find<Border>(shell, "OutlinePanel").GetVisualDescendants().OfType<TreeView>());
        Assert.DoesNotContain(model.Shell.Modes, m => m.Title is "Physical" or "Inventory" or "Projects" or "Sourcing");
        Assert.Same(model.Environment, model.SelectedSection);

        // Ctrl+K focuses search; typing finds the reading; jumping selects the node and emphasises the metric.
        model.Shell.GoTo(ShellMode.Alerts);
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        Pump();
        var box = Find<TextBox>(shell, "SearchBox");
        Assert.True(box.IsFocused, "Ctrl+K focuses the toolbar search");
        box.Text = "server memory";
        Pump();
        Assert.True(model.Shell.SearchDropdownOpen);
        Assert.True(Find<Border>(shell, "SearchDropdown").IsVisible);
        var metric = model.Shell.Search.Results.First(r => r.Result.Kind == SceneSearchKind.Metric);
        Assert.Equal(FakeEnvironmentSource.ServerId, metric.Result.NodeId);
        model.Shell.Search.JumpToCommand.Execute(metric);
        Pump();
        Assert.Equal(ShellMode.Ecosystem, model.Shell.Mode);
        Assert.Equal(FakeEnvironmentSource.ServerId, model.Environment.SelectedNodeId);
        Assert.False(model.Shell.SearchDropdownOpen);
        var layer = model.Shell.Inspector.Layers.Single(l => l.Id == metric.Result.LayerId);
        Assert.True(layer.IsFocused && layer.Readings.Any(r => r.IsFocused), "jump emphasises the layer and reading");
        Assert.False(model.Shell.Inspector.Controls.IsAvailable, "a source without IControlSource offers no controls");

        // Modes reach every section.
        model.Shell.GoTo(ShellMode.Settings);
        Pump();
        Assert.Equal(["Devices", "Pairing and trust", "Overview"], model.Shell.SubSections.Select(s => s.Title));
        Find<TabStrip>(shell, "SubTabs").SelectedItem = model.Shell.SubSections.OfType<PairingsViewModel>().Single();
        Pump();
        Assert.IsType<PairingsViewModel>(model.SelectedSection);
        Assert.Contains(shell.GetVisualDescendants(), v => v is PairingsView);
        model.Shell.GoTo(ShellMode.Activity);
        Assert.Equal(["Current state", "History", "As of"], model.Shell.SubSections.Select(s => s.Title));

        // Phone: selection opens the bottom sheet, Escape closes it, Ctrl+K opens the search page.
        model.Shell.GoTo(ShellMode.Ecosystem);
        window.Width = 390;
        window.Height = 844;
        Pump();
        model.Environment.ShowScene(SampleEnvironment.Create(DateTimeOffset.UtcNow));
        model.Environment.SelectedNodeId = SampleEnvironment.Phone;
        Pump();
        var sheet = Find<Border>(shell, "InspectorHost");
        Assert.True(sheet.IsVisible, "sheet slides up on selection");
        Assert.Contains("sheet", sheet.Classes);
        Assert.Contains("open", sheet.Classes);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, sheet.VerticalAlignment);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.Null(model.Environment.SelectedNodeId);
        Assert.False(sheet.IsVisible, "Escape closes the sheet");
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        Pump();
        Assert.Equal(ShellMode.Search, model.Shell.Mode);
        window.Close();
    }

    private static List<Button> ControlButtons(WorkspaceShell shell) =>
        Find<StackPanel>(shell, "ControlsPanel").GetVisualDescendants().OfType<Button>()
            .Where(b => b.CommandParameter is ControlActionRow).ToList();

    private static (Window Window, WorkspaceShell Shell) Show(MainViewModel model, double width, double height)
    {
        var shell = new WorkspaceShell { RunEnvironmentLive = false, DataContext = model };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Pump();
        return (window, shell);
    }

    private static T Find<T>(WorkspaceShell shell, string name)
        where T : Control =>
        shell.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
