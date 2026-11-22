using Avalonia;
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
using MonitorApp.Client.Core.ViewModels.Alerts;
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
        source.ExtraAlerts.Add(new AlertDto("a2", 1, "disk-free", SampleEnvironment.Server, SampleEnvironment.Server, "Warning", "Raised",
            "Storage almost full", "240 GB free of 4 TB", now.AddMinutes(-20), null));
        DiveScenes.AddTrendHistory(source, now);
        using var model = new MainViewModel(source);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = 1400, Height = 860, Content = shell };
        window.Show();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        await model.Shell.Dive.RefreshApprovalsAsync();
        await model.Shell.Dive.TrendsTask;
        Pump();
        var dive = model.Shell.Dive;

        // Sparklines come from the node's recorded history plus the live reading, and stay hidden where there is none.
        var serverTile = dive.Home.Tiles.OfType<DiveDeviceTile>().Single(t => t.NodeId == SampleEnvironment.Server);
        Assert.True(
            serverTile.TrendList.Select(t => t.Kind).SequenceEqual([DiveTrendKind.Cpu, DiveTrendKind.Network])
                && serverTile.TrendList[0].Points.Count == 21 && serverTile.TrendList[1].Points.Count == 19,
            "server sparklines: " + string.Join("; ", serverTile.TrendList.Select(t => $"{t.Kind} {t.Points.Count} pts '{t.Text}'")));
        Assert.True(serverTile.TrendList[0].NowText == "18 %", $"CPU sparkline ends at the live reading, says {serverTile.TrendList[0].NowText}");
        var noHistory = dive.Home.Tiles.OfType<DiveDeviceTile>().Where(t => t.NodeId is SampleEnvironment.Phone or SampleEnvironment.Tablet or SampleEnvironment.Fedora);
        Assert.All(noHistory, t => Assert.False(t.HasTrends, $"{t.Title} has no history, yet shows {t.TrendList.Count} sparklines"));
        var drawn = shell.GetVisualDescendants().OfType<Sparkline>().Where(l => l.IsEffectivelyVisible).ToList();
        Assert.True(drawn.Count == 3 && drawn.All(l => l.Bounds.Width > 40 && l.Points is { Count: >= 2 }),
            $"{drawn.Count} sparklines drawn on Home (server CPU and network, laptop CPU): {string.Join(", ", drawn.Select(l => l.Bounds.Size))}");

        // Wide: four columns, every tile rendered as a touch-sized button, no side inspector, the outline one switch away.
        var grid = HomeGrid(shell);
        Assert.True(grid.Columns == 4, $"wide grid has {grid.Columns} columns");
        var tiles = TileButtons(shell);
        Assert.True(tiles.Count == dive.Home.Tiles.Count && tiles.Count == 7, $"{tiles.Count} tile buttons for {dive.Home.Tiles.Count} tiles");
        Assert.All(tiles, t => Assert.True(t.Bounds.Height >= 44 && t.Bounds.Width >= 44, $"tile {t.DataContext} is {t.Bounds.Size}"));
        Assert.False(Find<Border>(shell, "InspectorHost").IsVisible, "the Dive has no side inspector");
        Assert.True(Find<Button>(shell, "OutlineToggle").IsVisible, "the outline stays reachable");
        Assert.Equal(["Home"], model.Shell.Breadcrumbs.Select(b => b.Text));

        // Phone: two compact tiles a row at 390 px (the attention tile spans), one column when narrower; 44 px targets; slim timeline.
        window.Width = 390;
        window.Height = 844;
        Pump();
        model.Shell.TimelineOpen = true; // a phone opens it from the LIVE pill; shown here to measure it
        Pump();
        grid = HomeGrid(shell);
        Assert.True(grid.Columns == 2, $"phone grid at 390 px has {grid.Columns} columns in {grid.Bounds.Width} px");
        Assert.All(TileButtons(shell), t => Assert.True(
            t.Bounds.Height >= 44 && (t.DataContext is DiveAttentionTile ? t.Bounds.Width >= grid.Bounds.Width - 1 : t.Bounds.Width >= 168 && t.Bounds.Width < grid.Bounds.Width / 2),
            $"phone tile {t.DataContext?.GetType().Name} is {t.Bounds.Size} in {grid.Bounds.Size}"));
        var compact = TileButtons(shell).Where(t => t.DataContext is DiveDeviceTile).Select(t => t.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("compacttile"))).ToList();
        Assert.All(compact, c => Assert.True(c.IsVisible, "phone tiles use the compact variant"));
        var timeline = Find<TimelineView>(shell, "Timeline");
        Assert.True(timeline.Classes.Contains("slim") && !Find<TextBlock>(shell, "RangeHeader").IsVisible && timeline.Bounds.Height <= 60,
            $"the Dive's timeline is one slim row: classes {string.Join(",", timeline.Classes)}, height {timeline.Bounds.Height}");
        Assert.All(timeline.GetVisualDescendants().OfType<Control>().Where(c => c is (Button or Slider) and not RepeatButton && c.IsEffectivelyVisible),
            c => Assert.True(c.Bounds.Height >= 44 && c.Bounds.Width >= 44, $"timeline target {c.Name ?? c.DataContext?.ToString()} is {c.Bounds.Size}"));
        window.Width = 340;
        Pump();
        grid = HomeGrid(shell);
        Assert.True(grid.Columns == 1, $"below about 170 px a tile, the phone grid has {grid.Columns} columns in {grid.Bounds.Width} px");
        model.Shell.TimelineOpen = false;
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

        // The device page's CPU gauge carries its sparkline; sampling pace and acknowledging an alert go through the same controls.
        await dive.Controls.LoadTask;
        Pump();
        var cpuGauge = ((DiveDevicePage)dive.Page).Gauges.Single(g => g.Label == "CPU");
        Assert.True(cpuGauge.HasTrend && cpuGauge.AccessibleText.Contains("CPU over the last", StringComparison.Ordinal), cpuGauge.AccessibleText);
        var sampling = Find<StackPanel>(shell, "DiveSampling");
        Assert.True(sampling.IsEffectivelyVisible, "sampling interval offered on a paired computer");
        var every30 = sampling.GetVisualDescendants().OfType<Button>().Single(b => b.CommandParameter is SamplingChoice { Title: "Every 30 s" });
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Sampling interval set.", false);
        Tap(window, every30);
        Assert.True(source.Sent[^1] == $"interval:{SampleEnvironment.Server}:30s" && dive.Controls.OutcomeText == "Done: Sampling interval set.",
            $"sent {source.Sent[^1]}, outcome {dive.Controls.OutcomeText}");
        var alerts = Find<ItemsControl>(shell, "DiveAlerts");
        Assert.True(alerts.IsEffectivelyVisible && dive.Controls.OpenAlerts.Select(a => a.AlertId).SequenceEqual(["a2"]),
            $"only the server's unresolved alert is offered: {string.Join(",", dive.Controls.OpenAlerts.Select(a => a.AlertId))}");
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Alert acknowledged.", false);
        var acknowledge = alerts.GetVisualDescendants().OfType<Button>().Single(b => b.CommandParameter is AlertAckRow);
        acknowledge.BringIntoView();
        Pump();
        Tap(window, acknowledge);
        Assert.True(source.Sent[^1] == $"ack:{SampleEnvironment.Server}:a2" && dive.Controls.OpenAlerts.Single().IsAcknowledged,
            $"sent {source.Sent[^1]}, row {dive.Controls.OpenAlerts.Single()}");
        Assert.Equal("Done: Alert acknowledged.", Find<TextBlock>(shell, "DiveOutcome").Text);

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
        Assert.Equal(3, source.Sent.Count);
        Assert.All(sampling.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEnabled, "sampling must not change from a recorded view"));
        Assert.All(Find<TextBlock>(shell, "DiveTitle").GetVisualAncestors().OfType<ScrollViewer>().First().GetVisualDescendants().OfType<Sparkline>(),
            l => Assert.False(l.IsEffectivelyVisible, "no live sparklines over a recorded moment"));

        // With motion on, a tap starts a zoom, and the page comes to rest at full size and opacity. The zoom runs on the wall clock, so the
        // test ends it with CompleteAnimations instead of waiting for frames a slow runner may not deliver in time.
        await model.Environment.ReturnToNowAsync();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        dive.GoHome();
        shell.ReducedMotion = false;
        Pump();
        var view = shell.GetVisualDescendants().OfType<DiveView>().Single();
        var zooms = view.ZoomsStarted;
        Tap(window, TileButtons(shell).Single(t => t.DataContext is DiveDeviceTile { NodeId: SampleEnvironment.Laptop }));
        Assert.True(view.ZoomsStarted == zooms + 1, $"the tap should start one zoom; started {view.ZoomsStarted - zooms}");
        view.CompleteAnimations();
        Pump();
        var page = view.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PageHost");
        Assert.True(
            dive.NodeId == SampleEnvironment.Laptop && !view.IsZooming && page.RenderTransform is null && page.Opacity == 1,
            $"zoom ended at {dive.NodeId}, zooming {view.IsZooming}, transform {page.RenderTransform}, opacity {page.Opacity}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AlertsGroupByStateAlertPageActsAndDivesIntoTheDevice()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new FakeControlEnvironmentSource { PairingsHeldBy = SampleEnvironment.Server };
        source.PendingPairings.Add(new PendingPairingDto("pair-1", "peer-watch", "Pixel Watch", "FEEDBEEF", "PendingApproval", now, now.AddMinutes(5)));
        var since = now.AddMinutes(-35).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        source.ExtraAlerts.Add(new AlertDto("a3", 1, "threshold.disk_free", SampleEnvironment.Server, SampleEnvironment.Server, "Critical", "Raised",
            "Low free disk space", $"Low free disk space on {SampleEnvironment.Server}: 6.0% is below 10.0% since {since}.", now.AddMinutes(-35), null));
        source.ExtraAlerts.Add(new AlertDto("a4", 1, "threshold.cpu_high", SampleEnvironment.Laptop, SampleEnvironment.Laptop, "Warning", "Raised",
            "Sustained high CPU", $"Sustained high CPU on {SampleEnvironment.Laptop}: 93.4% is above 90.0% since {since}.", now.AddMinutes(-14), null));
        source.ExtraAlerts.Add(new AlertDto("a2", 1, "disk-free", SampleEnvironment.Server, SampleEnvironment.Server, "Warning", "Raised",
            "Storage almost full", "240 GB free of 4 TB", now.AddMinutes(-20), null));
        source.ExtraAlerts.Add(new AlertDto("old", 1, "peer.silent", SampleEnvironment.Tablet, SampleEnvironment.Server, "Warning", "Resolved",
            "Peer not seen: Tablet", "Tablet has not connected.", now.AddDays(-3), now.AddDays(-2)));
        DiveScenes.AddTrendHistory(source, now);
        using var model = new MainViewModel(source);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = 1400, Height = 860, Content = shell };
        window.Show();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        await model.Shell.Dive.RefreshApprovalsAsync();
        await model.Shell.Dive.TrendsTask;
        model.Shell.GoTo(ShellMode.Alerts);
        var alerts = model.Shell.Alerts;
        await alerts.RefreshAsync();
        Pump();

        // Home: the approval on top, then the alerts by state; the 3-day-old resolved alert is not "recent".
        string Groups() => string.Join(" | ", alerts.Home.Groups.Select(g => $"{g.Kind}: {string.Join(",", g.Items.Select(i => i.AlertId))}"));
        Assert.True(Groups() == "NeedsYou: a3 | Active: a4,a2 | Resolved: a1", $"groups: {Groups()}");
        Assert.True(alerts.Home.Headline == "1 needs you" && alerts.Home.HasApprovals && Find<ItemsControl>(shell, "AlertApprovals").IsEffectivelyVisible,
            $"headline '{alerts.Home.Headline}', approvals {alerts.Home.Approvals.Count}");
        var disk = alerts.Home.Groups[0].Items[0];
        var cpu = alerts.Home.Groups[1].Items[0];
        Assert.True(disk.Severity.Text == "Critical" && disk.DeviceText == "Home server · Storage" && disk.ValueText == "Storage 94 %" && disk.IsAlarm,
            $"disk card: {disk.Severity.Text} / {disk.DeviceText} / {disk.ValueText} / alarm {disk.IsAlarm}");
        Assert.True(cpu.HasTrend && cpu.AgeText.StartsWith("For 14 min", StringComparison.Ordinal), $"cpu card trend {cpu.HasTrend}, age '{cpu.AgeText}'");
        var cards = AlertCards(shell);
        Assert.True(cards.Count == 4, $"{cards.Count} alert cards rendered");
        Assert.All(cards, c => Assert.True(c.Bounds.Height >= 44 && c.Bounds.Width <= 961, $"card {c.DataContext} is {c.Bounds.Size}"));

        // Tap the critical card: the alert page says what fired from the alert's own words, and the breadcrumb follows.
        Tap(window, cards.Single(c => c.DataContext is AlertCard { AlertId: "a3" }));
        var page = Assert.IsType<AlertPage>(alerts.Page);
        Assert.Equal(["Alerts", "Low free disk space"], model.Shell.Breadcrumbs.Select(b => b.Text));
        Assert.True(Find<TextBlock>(shell, "AlertObserved").Text == "6.0 %" && Find<TextBlock>(shell, "AlertThreshold").Text == "below 10.0 %",
            $"observed '{page.ObservedText}', threshold '{page.ThresholdText}'");
        Assert.DoesNotContain(since, Find<TextBlock>(shell, "AlertDetail").Text, StringComparison.Ordinal);
        Assert.True(page.Gauge is { Value: "94" } && page.DiveIntoText == "Dive into Home server", $"gauge {page.Gauge?.Value}, link '{page.DiveIntoText}'");

        // Actions go to the computer that raised it, with tiers said first; acknowledging moves it to "Acknowledged".
        var acknowledge = Find<Button>(shell, "AlertAcknowledge");
        Assert.True(acknowledge.IsEnabled && page.AcknowledgeTier == "Normal tier: no fingerprint needed", $"ack enabled {acknowledge.IsEnabled}, tier '{page.AcknowledgeTier}'");
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Alert acknowledged.", false);
        acknowledge.BringIntoView();
        Pump();
        Tap(window, acknowledge);
        Assert.True(source.Sent[^1] == $"ack:{SampleEnvironment.Server}:a3" && page.IsAcknowledged && !acknowledge.IsEnabled,
            $"sent {string.Join(",", source.Sent)}, acknowledged {page.IsAcknowledged}, still enabled {acknowledge.IsEnabled}");
        Assert.Equal("Done: Alert acknowledged.", Find<TextBlock>(shell, "AlertOutcome").Text);
        source.Outcome = new ControlOutcomeDto("Applied", "applied", "Alerts muted.", false);
        var muteDay = Find<Button>(shell, "AlertMuteDay");
        muteDay.BringIntoView();
        Pump();
        Tap(window, muteDay);
        Assert.True(source.Sent[^1] == $"mute:{SampleEnvironment.Server}:threshold.disk_free:24h", $"mute sent {source.Sent[^1]}");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.True(alerts.IsHome && Groups() == "Active: a4,a2 | Acknowledged: a3 | Resolved: a1", $"after acknowledge and Escape: home {alerts.IsHome}, {Groups()}");

        // Phone: one column, 44 px targets, a back chip to Alerts.
        window.Width = 390;
        window.Height = 844;
        Pump();
        Assert.All(AlertCards(shell), c => Assert.True(c.Bounds.Height >= 44 && c.Bounds.Width >= 300, $"phone card {c.DataContext} is {c.Bounds.Size}"));
        alerts.OpenAlert("a4");
        Pump();
        var back = Find<Button>(shell, "AlertBack");
        Assert.True(back.IsEffectivelyVisible, "phone alert page has a back chip");
        Assert.All(AlertPageButtons.Select(n => Find<Button>(shell, n)),
            b => Assert.True(b.Bounds.Height >= 44, $"{b.Name} is {b.Bounds.Size} on a phone"));
        Tap(window, back);
        Assert.True(alerts.IsHome, "back chip returns to the Alerts home");
        window.Width = 1400;
        window.Height = 860;
        Pump();

        // "Dive into" switches to the Ecosystem's Dive, at the part the rule watches.
        // (Switching mode refreshes the environment from the fake, whose own scene lacks the sample devices, so the landing place is
        // recorded when the Dive moves rather than read afterwards.)
        alerts.OpenAlert("a3");
        Pump();
        string? landed = null;
        void OnDive(object? sender, DiveNavigatedEventArgs e) => landed ??= $"{model.Shell.Dive.NodeId}/{model.Shell.Dive.LayerId}";
        model.Shell.Dive.Navigated += OnDive;
        Tap(window, Find<Button>(shell, "DiveInto"));
        model.Shell.Dive.Navigated -= OnDive;
        Assert.True(model.Shell.Mode == ShellMode.Ecosystem && model.Shell.IsDive && landed == $"{SampleEnvironment.Server}/storage",
            $"dove to {model.Shell.Mode} (Dive {model.Shell.IsDive}) landing at {landed}");
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));

        // Recorded: actions are off on the alert page.
        model.Environment.AsOf = now.AddHours(-2);
        model.Shell.GoTo(ShellMode.Alerts);
        alerts.GoHome();
        alerts.OpenAlert("a4");
        Pump();
        Assert.True(Find<Border>(shell, "AlertsRecordedBand").IsVisible && !Find<Button>(shell, "AlertAcknowledge").IsEnabled
            && !Find<Button>(shell, "AlertMuteHour").IsEnabled, "recorded: band shown, actions disabled");
        var sent = source.Sent.Count;
        await alerts.AcknowledgeCommand.ExecuteAsync(null);
        Assert.Equal(sent, source.Sent.Count);
        await model.Environment.ReturnToNowAsync();
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));

        // With motion on, opening an alert zooms like the Dive and comes to rest.
        alerts.GoHome();
        shell.ReducedMotion = false;
        Pump();
        var view = shell.GetVisualDescendants().OfType<AlertsView>().Single();
        var zooms = view.ZoomsStarted;
        Tap(window, AlertCards(shell).Single(c => c.DataContext is AlertCard { AlertId: "a4" }));
        Assert.True(view.ZoomsStarted == zooms + 1, $"the tap should start one zoom; started {view.ZoomsStarted - zooms}");
        view.CompleteAnimations();
        Pump();
        var host = view.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PageHost");
        Assert.True(!view.IsZooming && host.RenderTransform is null && host.Opacity == 1 && alerts.Page is AlertPage { AlertId: "a4" },
            $"zooming {view.IsZooming}, transform {host.RenderTransform}, opacity {host.Opacity}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task HistoryTellsWhatHappenedAnEventShowsThatMomentAndTheToolsStayReachable()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new FakeControlEnvironmentSource();
        // The built-in alert a1 (critical CPU on the server, raised 5 h ago, resolved 4 h ago), one newer warning, one 9-day-old alert outside
        // the story's week; the server was paired 2 days ago (FakeEnvironmentSource.GetPeersAsync).
        source.ExtraAlerts.Add(new AlertDto("a2", 1, "disk-free", "peer:" + FakeEnvironmentSource.ServerId, FakeEnvironmentSource.ServerId, "Warning", "Raised",
            "Storage almost full", "240 GB free of 4 TB", now.AddMinutes(-20), null));
        source.ExtraAlerts.Add(new AlertDto("a9", 1, "disk-free", "peer:" + FakeEnvironmentSource.ServerId, FakeEnvironmentSource.ServerId, "Warning", "Resolved",
            "Too old to tell", "-", now.AddDays(-9), now.AddDays(-9).AddHours(1)));
        source.ExtraAlerts.Add(new AlertDto("a3", 1, "backup-slow", "peer:" + FakeEnvironmentSource.ServerId, FakeEnvironmentSource.ServerId, "Warning", "Raised",
            "Backup running slow", "Backup took 3 h", now.AddHours(-5).AddMinutes(20), null));
        // CPU history in the minutes before a1 was raised, for the event page's sparkline.
        for (var i = 9; i >= 1; i--)
        {
            source.History.Add(FakeEnvironmentSource.Recorded("peer:" + FakeEnvironmentSource.ServerId, SignalIds.CpuUtilization, 0.6 + (0.04 * (9 - i)), "Ratio",
                now.AddHours(-5).AddMinutes(-i), (ulong)i));
        }

        using var model = new MainViewModel(source);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = 1400, Height = 860, Content = shell };
        window.Show();
        await model.Environment.RefreshAsync();
        model.Shell.GoTo(ShellMode.Activity);
        var activity = model.Shell.Activity;
        await activity.RefreshAsync();
        Pump();

        // Home: the story newest first, grouped by local day, from real events only (the 9-day-old alert is outside the week).
        string Story() => string.Join(" | ", activity.Home.Days.SelectMany(d => d.Items).Select(e => $"{e.KindText}: {e.Title} ({e.DeviceText})"));
        Assert.True(Story() == "Alert raised: Storage almost full (Home server) | Alert resolved: CPU saturated (Home server) | "
            + "Alert raised: Backup running slow (Home server) | Alert raised: CPU saturated (Home server) | Device paired: Paired Home server (Home server)",
            $"story: {Story()}");
        Assert.Equal(["Warning", "Resolved", "Warning", "Critical", "Trusted"], activity.Home.Days.SelectMany(d => d.Items).Select(e => e.Severity.Text));
        Assert.All(activity.Home.Days, d => Assert.True(d.Items.Select(e => e.Time.ToLocalTime().Date).Distinct().Count() == 1, $"day '{d.Title}' mixes dates"));
        var pairedDay = activity.Home.Days.Single(d => d.Items.Any(e => e.KindText == "Device paired"));
        Assert.False(pairedDay.Title.StartsWith("Today", StringComparison.Ordinal) || pairedDay.Title.StartsWith("Yesterday", StringComparison.Ordinal),
            $"2 days ago is titled '{pairedDay.Title}'");
        Assert.True(activity.Home.Headline == "5 things happened"
            && activity.Home.Breakdown == "In the last 7 days: 1 critical alert · 2 other alerts · 1 resolved · 1 pairing or trust change.",
            $"headline '{activity.Home.Headline}', breakdown '{activity.Home.Breakdown}'");
        var rows = EntryRows(shell, "ActivityDays");
        Assert.True(rows.Count == 5, $"{rows.Count} entry rows rendered");
        Assert.All(rows, r => Assert.True(r.Bounds.Height >= 44 && r.Bounds.Width <= 961, $"row {r.DataContext} is {r.Bounds.Size}"));
        var critical = rows.Single(r => r.DataContext is MonitorApp.Client.Core.ViewModels.Activity.ActivityEntry { IsAlarm: true });
        var raisedAt = ((MonitorApp.Client.Core.ViewModels.Activity.ActivityEntry)critical.DataContext!).Time;

        // Tap the critical entry: the app moves to that moment and the page shows the server's readings as recorded then.
        Tap(window, critical);
        var page = Assert.IsType<MonitorApp.Client.Core.ViewModels.Activity.ActivityEventPage>(activity.Page);
        await activity.OpenTask;
        Pump();
        Assert.True(model.Environment.AsOf == raisedAt && model.Shell.IsHistorical && Find<Border>(shell, "ActivityRecordedBand").IsVisible,
            $"as of {model.Environment.AsOf} (expected {raisedAt}), band {Find<Border>(shell, "ActivityRecordedBand").IsVisible}");
        Assert.Equal(["Activity and history", "CPU saturated"], model.Shell.Breadcrumbs.Select(b => b.Text));
        Assert.True(Find<TextBlock>(shell, "EventDetail").Text == "CPU at 99%" && page.DeviceTitle == FakeEnvironmentSource.ServerName && !page.IsLoading,
            $"detail '{Find<TextBlock>(shell, "EventDetail").Text}', device '{page.DeviceTitle}', loading {page.IsLoading}, note '{page.ReadingsNote}'");
        string Gauges() => string.Join(", ", page.Gauges.Select(g => $"{g.Label} {g.Value}{g.Unit} trend {g.Trend?.Text ?? "-"}"));
        var cpu = page.Gauges.SingleOrDefault(g => g.Label == "CPU");
        Assert.True(cpu is { Value: "18", HasTrend: true } && cpu.Trend!.Text.Contains("before " + page.MomentText, StringComparison.Ordinal)
            && cpu.Trend.Text.Contains("at " + page.MomentText + " 92 %", StringComparison.Ordinal), $"gauges: {Gauges()}");
        Assert.True(page.Gauges.Any(g => g.Label == "Memory" && g.HasFill) && Find<ItemsControl>(shell, "EventGauges").IsEffectivelyVisible, $"gauges: {Gauges()}");
        Assert.True(page.HasNearby && page.Nearby.Any(n => n.Title == "Backup running slow")
            && page.Nearby.All(n => n.Event != page.Entry.Event && (n.Time - page.Entry.Time).Duration() <= TimeSpan.FromHours(1)),
            $"nearby: {string.Join(", ", page.Nearby.Select(n => $"{n.TimeText} {n.Title}"))}");

        // Escape climbs back to the story and puts the app back at live (it was live before the entry was opened).
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await activity.OpenTask;
        Pump();
        Assert.True(activity.IsHome && model.Environment.AsOf is null && !Find<Border>(shell, "ActivityRecordedBand").IsVisible,
            $"after Escape: home {activity.IsHome}, as of {model.Environment.AsOf}");

        // "See the whole home as of" switches to the Ecosystem's Dive, recorded at that moment.
        activity.Open(activity.Home.Days.SelectMany(d => d.Items).Single(e => e.IsAlarm));
        await activity.OpenTask;
        Pump();
        page = Assert.IsType<MonitorApp.Client.Core.ViewModels.Activity.ActivityEventPage>(activity.Page);
        Tap(window, Find<Button>(shell, "SeeWholeHome"));
        Assert.True(model.Shell.Mode == ShellMode.Ecosystem && model.Shell.IsDive && model.Shell.Dive.IsHome && model.Environment.AsOf == raisedAt
            && model.Shell.LiveText == "RECORDED " + page.MomentText, $"mode {model.Shell.Mode}, Dive {model.Shell.IsDive}, as of {model.Environment.AsOf}, pill {model.Shell.LiveText}");

        // Back in History the page is still open; "Return to live" leaves it open, live, and Back no longer moves time.
        model.Shell.GoTo(ShellMode.Activity);
        Pump();
        Assert.Same(page, activity.Page);
        Tap(window, Find<Button>(shell, "EventReturnLive"));
        Assert.True(model.Environment.AsOf is null && activity.Page == page, $"return to live: as of {model.Environment.AsOf}");

        // Phone: one column of 44 px rows, a back chip, full-width page buttons.
        window.Width = 390;
        window.Height = 844;
        Pump();
        Assert.True(Find<Button>(shell, "ActivityBack").IsEffectivelyVisible, "phone event page has a back chip");
        var seeHome = Find<Button>(shell, "SeeWholeHome");
        Assert.True(seeHome.Bounds.Height >= 44 && seeHome.Bounds.Width >= 300, $"phone 'see the whole home' is {seeHome.Bounds.Size}");
        Tap(window, Find<Button>(shell, "ActivityBack"));
        Assert.True(activity.IsHome && model.Environment.AsOf is null, $"chip: home {activity.IsHome}, as of {model.Environment.AsOf}");
        Assert.All(EntryRows(shell, "ActivityDays"), r => Assert.True(r.Bounds.Height >= 44 && r.Bounds.Width >= 300, $"phone row {r.DataContext} is {r.Bounds.Size}"));

        // Browse history keeps the older tools: a tile opens one inside History, and selecting one from elsewhere lands there too.
        var asOfTile = Find<ItemsControl>(shell, "ActivityBrowse").GetVisualDescendants().OfType<Button>()
            .Single(b => b.DataContext is MonitorApp.Client.Core.ViewModels.Activity.ActivityBrowseTile { Section: AsOfViewModel });
        asOfTile.BringIntoView();
        Pump();
        Assert.True(asOfTile.Bounds.Height >= 44, $"browse tile is {asOfTile.Bounds.Size}");
        Tap(window, asOfTile);
        Assert.True(model.SelectedSection is AsOfViewModel && shell.GetVisualDescendants().OfType<AsOfView>().Any() && model.Shell.ToolbarTitle == "Readings as of a moment",
            $"selected {model.SelectedSection.GetType().Name}, title '{model.Shell.ToolbarTitle}'");
        window.Width = 1400;
        window.Height = 860;
        model.Shell.GoTo(ShellMode.Ecosystem);
        Pump();
        model.SelectedSection = model.Sections.OfType<HistoryViewModel>().Single();
        Pump();
        Assert.True(model.Shell.Mode == ShellMode.Activity && activity.BrowsingSection is HistoryViewModel && shell.GetVisualDescendants().OfType<HistoryView>().Any(),
            $"mode {model.Shell.Mode}, browsing {activity.BrowsingSection?.Title}");

        // With motion on, opening an entry zooms like the Dive and comes to rest.
        activity.GoHome();
        shell.ReducedMotion = false;
        Pump();
        var view = shell.GetVisualDescendants().OfType<ActivityView>().Single();
        var zooms = view.ZoomsStarted;
        Tap(window, EntryRows(shell, "ActivityDays")[0]);
        await activity.OpenTask;
        Assert.True(view.ZoomsStarted == zooms + 1, $"the tap should start one zoom; started {view.ZoomsStarted - zooms}");
        view.CompleteAnimations();
        Pump();
        Assert.True(!view.IsZooming && activity.Page is MonitorApp.Client.Core.ViewModels.Activity.ActivityEventPage, $"zooming {view.IsZooming}, page {activity.Page}");
        window.Close();
    }

    private static List<Button> EntryRows(WorkspaceShell shell, string listName) =>
        Find<ItemsControl>(shell, listName).GetVisualDescendants().OfType<Button>()
            .Where(b => b.DataContext is MonitorApp.Client.Core.ViewModels.Activity.ActivityEntry).ToList();

    private static readonly string[] AlertPageButtons = ["AlertAcknowledge", "AlertMuteHour", "AlertMuteDay", "DiveInto"];

    private static List<Button> AlertCards(WorkspaceShell shell) =>
        Find<ItemsControl>(shell, "AlertGroups").GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is AlertCard).ToList();

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
