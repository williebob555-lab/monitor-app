using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;
using MonitorApp.Client.Ui.Controls;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

public sealed class ThingsViewTests
{
    [AvaloniaFact]
    public void OpeningAComputerPullsItsPartsOutAroundIt()
    {
        using var model = new MainViewModel(new FakeThingsSource());
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        Assert.Contains(model.Shell.Modes, m => m.Mode == ShellMode.Things);
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        var laptop = things.Cards.Single(c => c.Name == "Wilson-laptop");
        Assert.True(laptop.CanOpen);
        Assert.DoesNotContain(laptop.Values, v => v.Label == "Memory total");
        // A busy computer's readings come under headings, and a big heading starts folded instead of one long list.
        var network = Assert.Single(laptop.Groups, g => g.Title == "Network");
        Assert.False(network.StartsOpen);
        Assert.Contains(network.Values, v => v.Label == "Data sent (total)");
        Assert.True(things.Cards.Single(c => c.Name == "Focusrite 2i2").Groups.Single().StartsOpen);
        Assert.Contains(laptop.Parts, p => p.Name == "Memory" && p.Values.Any(v => v.Label == "Memory total" && v.Badge == "measured · live"));

        things.OpenCommand.Execute(laptop);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var panel = window.GetVisualDescendants().OfType<ExplodedPanel>().Single();
        Assert.Equal(1 + laptop.Parts.Count, panel.Children.Count);
        Assert.True(panel.Bounds.Width > 0);

        // A refresh that changed nothing leaves the cards and the opened view as they are (no rebuild, no second explosion).
        var opened = things.OpenCard;
        things.RefreshAsync().GetAwaiter().GetResult();
        Assert.Same(laptop, things.Cards.Single(c => c.Name == "Wilson-laptop"));
        Assert.Same(opened, things.OpenCard);

        things.CloseCommand.Execute(null);
        Assert.True(things.IsGrid);
        window.Close();
    }

    [AvaloniaFact]
    public void ThingsAndTheEcosystemOpenEachOtherOnTheSameDevice()
    {
        using var model = new MainViewModel(new FakeThingsSource());
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        model.Environment.RefreshAsync().GetAwaiter().GetResult();
        things.RefreshAsync().GetAwaiter().GetResult();
        var laptop = things.Cards.Single(c => c.Name == "Wilson-laptop");
        Assert.Equal(FakeEnvironmentSource.LocalId, laptop.LiveNodeId);
        var memory = laptop.Parts.Single(p => p.Name == "Memory");
        Assert.Equal(("memory", FakeEnvironmentSource.LocalId), (memory.LiveLayerId, memory.LiveNodeId));

        model.Shell.GoTo(ShellMode.Things);
        things.SeeLiveCommand.Execute(memory);
        Assert.Equal(ShellMode.Ecosystem, model.Shell.Mode);
        Assert.Equal(FakeEnvironmentSource.LocalId, model.Shell.Dive.NodeId);

        Assert.True(model.Shell.Dive.CanOpenInThings);
        model.Shell.Dive.GoTo(FakeEnvironmentSource.LocalId);
        var page = Assert.IsType<MonitorApp.Client.Core.ViewModels.Dive.DiveDevicePage>(model.Shell.Dive.Page);
        model.Shell.Dive.OpenInThingsCommand.Execute(page);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ShellMode.Things, model.Shell.Mode);
        Assert.Equal("Wilson-laptop", things.OpenCard?.Name);
    }

    [AvaloniaFact]
    public void ThingsFollowTheTimelineAndAreReadOnlyInThePast()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Assert.True(things.CanChange);

        var earlier = DateTimeOffset.UtcNow.AddHours(-3);
        model.Environment.GoToAsync(earlier).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(earlier, things.AsOf);
        Assert.Contains(earlier, source.AsOfRequests);
        Assert.False(things.CanChange);
        Assert.StartsWith("RECORDED", things.RecordedText, StringComparison.Ordinal);
        things.EditCommand.Execute(things.Cards.First());
        Assert.False(things.IsEditing);

        model.Environment.ReturnToNowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(things.AsOf);
        Assert.True(things.CanChange);
        window.Close();
    }

    [AvaloniaFact]
    public void EditingAThingSendsOnlyWhatChangedAndOffersOnlyPlacesOutsideIt()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        var laptop = things.Cards.Single(c => c.Name == "Wilson-laptop");
        things.EditCommand.Execute(laptop);
        Dispatcher.UIThread.RunJobs();
        Assert.True(things.IsEditing);
        // A computer can't go inside its own memory; its kind stays offered even though "Add" doesn't list it.
        Assert.DoesNotContain(things.EditPlaces, p => p.Id is { } id && id.StartsWith(laptop.Id + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(things.EditPlaces, p => p.Id == laptop.Id);
        Assert.Equal("device.computer", things.EditKind?.Id);
        things.CancelEditCommand.Execute(null);

        var amp = things.Cards.Single(c => c.Name == "Focusrite 2i2");
        things.EditCommand.Execute(amp);
        Assert.Equal("Bought used. Needs a 12 V supply.", things.EditNote);
        things.EditInside = things.EditPlaces.Single(p => p.Name == "Studio rack");
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var sent = Assert.Single(source.Edits);
        Assert.Equal(new ThingEditDto("thing:amp", InsideId: "thing:rack"), sent);
        Assert.False(things.IsEditing);

        // Stage, tags and a value from a spec sheet travel in one edit.
        things.EditCommand.Execute(amp);
        things.EditStage = things.Stages.Single(s => s.Id == "Installed");
        things.EditTags = "studio, #spare";
        things.NewValueLabel = "Max power";
        things.NewValueText = "5 W";
        things.NewValueFromSpec = true;
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var second = source.Edits[1];
        Assert.Equal("Installed", second.Lifecycle);
        Assert.Equal(["studio", "spare"], second.Tags);
        Assert.Equal(new ThingValueEditDto("Max power", "5 W", true), Assert.Single(second.Values!));
        Assert.Null(second.Name);

        // Links show on the card in words; in the form one is removed and another added, on Save.
        Assert.Equal(["Connected to Router at 192.168.1.1"], amp.LinkTexts);
        things.EditCommand.Execute(amp);
        things.RemoveLinkCommand.Execute(things.EditLinks.Single());
        things.NewLinkType = things.LinkTypes.Single(k => k.Id == "powered-by");
        things.NewLinkTarget = things.LinkTargets.Single(p => p.Name == "Studio rack");
        Assert.DoesNotContain(things.LinkTargets, p => p.Id == amp.Id);
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var third = source.Edits[2];
        Assert.Equal(["rel:amp-router"], third.RemoveLinks);
        Assert.Equal(new ThingLinkEditDto("powered-by", "thing:rack"), Assert.Single(third.AddLinks!));
        window.Close();
    }

    [AvaloniaFact]
    public void PortsAndConnectionsShowOnTheCardAndTheFormAddsAPortAndPlugsItIn()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        var amp = things.Cards.Single(c => c.Name == "Focusrite 2i2");
        Assert.Equal(["DC in · DC barrel 5.5/2.1 mm socket · input · 12 V · dc-fixed → Pedal power DC out 1"], amp.PortTexts);
        var wiring = Assert.Single(amp.WiringRows);
        Assert.Equal(("Won't work", true), (wiring.VerdictText, wiring.IsProblem));
        Assert.Equal(["Pedal power DC out 1 supplies 9 V, but Focusrite 2i2 DC in accepts 12 V."], wiring.Reasons);

        // The power supply: unplug the 9 V port from the amp, add a 12 V port and plug that one into the amp instead.
        var psu = things.Cards.Single(c => c.Name == "Pedal power");
        things.EditCommand.Execute(psu);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["DC out 2"], things.ConnectFromChoices.Select(c => c.Text));
        Assert.DoesNotContain(things.ConnectToChoices, c => c.Text == "Focusrite 2i2 · DC in");
        things.UnplugPortCommand.Execute(things.EditPorts.Single(p => p.Name == "DC out 1"));
        Assert.Contains(things.ConnectToChoices, c => c.Text == "Focusrite 2i2 · DC in");
        things.NewPortName = "DC out 3";
        things.NewPortConnector = things.Connectors.Single(c => c.Id == "dc-barrel-5521");
        things.NewPortGender = "Plug";
        things.NewPortDirection = "Output";
        things.NewPortSignals = "dc-fixed";
        things.NewPortVoltage = "12 V";
        things.NewPortCurrent = "1 A";
        things.NewPortFromSpec = true;
        things.ConnectFrom = things.ConnectFromChoices.Single(c => c.Text == "The new port");
        things.ConnectTo = things.ConnectToChoices.Single(c => c.Text == "Focusrite 2i2 · DC in");
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var sent = Assert.Single(source.Edits);
        Assert.Equal(["conn:psu-amp"], sent.Disconnect);
        var port = Assert.Single(sent.AddPorts!);
        Assert.Equal(new ThingPortEditDto("DC out 3", "dc-barrel-5521", "Plug", "Output", null, null, "12 V", null, "1 A", null, true), port with { Signals = null });
        Assert.Equal(["dc-fixed"], port.Signals);
        Assert.Equal(new ThingConnectEditDto("DC out 3", "thing:amp", "port:amp-dc"), Assert.Single(sent.Connect!));
        Assert.Null(sent.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void ARackIsDrawnOnItsCardAndAThingIsPlacedAtAUnit()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        var rack = things.Cards.Single(c => c.Name == "Studio rack").Rack!;
        Assert.Equal("6U rack", rack.SizeText);
        Assert.Equal(["U6:", "U5–4:Focusrite 2i2", "U3:DSP (height unknown)", "U2:", "U1:", "U3:EQ"], rack.Rows.Select(r => $"{r.UnitText}:{r.Label}"));
        Assert.Equal(2 * ThingRackRow.UnitHeight, rack.Rows[1].Height);
        Assert.True(rack.Rows[2].IsProblem);
        Assert.Equal(["DSP (U3, 1U) and EQ (U3, 1U) take the same space."], rack.Notes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "U5–4" && t.IsEffectivelyVisible);

        // Put the amp in the rack at U5 from the edit form.
        things.EditCommand.Execute(things.Cards.Single(c => c.Name == "Focusrite 2i2"));
        things.EditInside = things.EditPlaces.Single(p => p.Name == "Studio rack");
        things.EditSlot = "U5";
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var sent = Assert.Single(source.Edits);
        Assert.Equal(("thing:rack", "U5"), (sent.InsideId, sent.Slot));
        window.Close();
    }

    [AvaloniaFact]
    public void APedalboardIsDrawnFromAboveWithTheFrontAtTheBottomAndAPedalIsPlacedInMillimetres()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        var board = things.Cards.Single(c => c.Name == "Pedalboard").Board!;
        Assert.Equal("500 × 250 mm, seen from above (front at the bottom)", board.SizeText);
        var fuzz = board.Items.Single(i => i.Label == "Fuzz");
        Assert.Equal((20.0, 120.0, 70.0, 120.0, true), (fuzz.Left, fuzz.Top, fuzz.Width, fuzz.Height, fuzz.IsProblem)); // 250 - 10 - 120 from the back
        Assert.Contains(board.Items, i => i.Label == "Tuner" && i.IsOk);
        Assert.Equal(["Delay and Fuzz take the same space.", "Looper is on Pedalboard but has no position yet."], board.Notes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Tuner" && t.IsEffectivelyVisible);

        things.EditCommand.Execute(things.Cards.Single(c => c.Name == "Focusrite 2i2"));
        things.EditInside = things.EditPlaces.Single(p => p.Name == "Pedalboard");
        things.EditSlot = "200, 60";
        things.SaveEditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("thing:board", "200, 60"), (source.Edits.Single().InsideId, source.Edits.Single().Slot));
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingAPedalOnTheBoardOrEquipmentInTheRackMovesIt()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 1500);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        // The tuner sits at x 380 mm; drag it 100 mm to the left. Points are worked out in the board's own mm.
        var tuner = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Tuner").FindAncestorOfType<Grid>()!;
        var canvas = tuner.FindAncestorOfType<Canvas>()!;
        var from = canvas.TranslatePoint(new Avalonia.Point(415, 180), window)!.Value;
        var to = canvas.TranslatePoint(new Avalonia.Point(315, 180), window)!.Value;
        window.MouseDown(from, Avalonia.Input.MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new ThingEditDto("thing:tuner", Slot: "280,10"), source.Edits.Single());

        // The DSP is at U3; drag it up one unit.
        var dsp = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "DSP (height unknown)" && t.IsEffectivelyVisible).FindAncestorOfType<Grid>()!;
        var rowFrom = dsp.TranslatePoint(new Avalonia.Point(dsp.Bounds.Width / 2, dsp.Bounds.Height / 2), window)!.Value;
        var rowTo = rowFrom.WithY(rowFrom.Y - ThingRackRow.UnitHeight);
        window.MouseDown(rowFrom, Avalonia.Input.MouseButton.Left);
        window.MouseMove(rowTo);
        window.MouseUp(rowTo, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new ThingEditDto("thing:dsp", Slot: "U4"), source.Edits[^1]);

        // A tap is not a move.
        window.MouseDown(rowFrom, Avalonia.Input.MouseButton.Left);
        window.MouseUp(rowFrom, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, source.Edits.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void WhatsMissingListsNeedsAndAddsOneToTheListAsARequirement()
    {
        var source = new FakeThingsSource();
        using var model = new MainViewModel(source);
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        var window = Show(model, 1400, 900);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("What's missing: 2 things", things.MissingTitle);
        var stronger = things.Missing[0];
        Assert.Equal(("A stronger supply than Pedal power", true, "Pedal power"), (stronger.Need, stronger.IsProblem, stronger.About));
        Assert.False(things.Missing[1].IsProblem);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "A stronger supply than Pedal power" && t.IsEffectivelyVisible);

        things.AddNeedCommand.Execute(stronger);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("A stronger supply than Pedal power\nFocusrite 2i2 DC in draws 0.5 A, more than the 0.3 A Pedal power DC out 1 can supply.", "requirement"), Assert.Single(source.Added));

        // In the past there is nothing to add, so the list is empty.
        things.AsOf = DateTimeOffset.UtcNow.AddDays(-1);
        things.RefreshAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.False(things.HasMissing);
        window.Close();
    }

    [Trait("Category", "Manual")]
#if MANUAL_SHOTS
    [AvaloniaFact]
#else
    [AvaloniaFact(Skip = "Manual: run with -p:ManualShots=true to render screenshots.")]
#endif
    public void RenderThingsScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("MONITOR_UI_SHOTS_DIR") ?? "/tmp/claude-0/shots";
        Directory.CreateDirectory(dir);
        using var model = new MainViewModel(new FakeThingsSource());
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        Shot(model, 1400, 900, dir, "things-wide-1400x900", () => { });
        Shot(model, 390, 844, dir, "things-phone-390x844", () => { });
        Shot(model, 1400, 900, dir, "things-open-wide-1400x900", () => things.OpenCommand.Execute(things.Cards.Single(c => c.Name == "Wilson-laptop")));
        Shot(model, 390, 844, dir, "things-open-phone-390x844", () => things.OpenCommand.Execute(things.Cards.Single(c => c.Name == "Wilson-laptop")));
        Shot(model, 1400, 900, dir, "things-edit-wide-1400x900", () => things.EditCommand.Execute(things.Cards.Single(c => c.Name == "Focusrite 2i2")));
        Shot(model, 390, 844, dir, "things-edit-phone-390x844", () => things.EditCommand.Execute(things.Cards.Single(c => c.Name == "Focusrite 2i2")));
        Shot(model, 1400, 900, dir, "things-ports-edit-wide-1400x900", () => things.EditCommand.Execute(things.Cards.Single(c => c.Name == "Pedal power")));
        Shot(model, 1400, 1500, dir, "things-rack-wide-1400x1500", () => things.FilterText = "rack");
        Shot(model, 1400, 1500, dir, "things-board-wide-1400x1500", () => things.FilterText = "pedalboard");
    }

    private static Window Show(MainViewModel model, double width, double height)
    {
        model.Shell.GoTo(ShellMode.Things);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Shot(MainViewModel model, double width, double height, string dir, string name, Action arrange)
    {
        var things = model.Sections.OfType<ThingsViewModel>().Single();
        things.CloseCommand.Execute(null);
        things.CancelEditCommand.Execute(null);
        var window = Show(model, width, height);
        things.RefreshAsync().GetAwaiter().GetResult();
        arrange();
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(30);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered; is Skia enabled?");
        frame.Save(Path.Combine(dir, name + ".png"));
        window.Close();
    }

    /// <summary>A desktop node holding a small model: this laptop with its parts, a router, and two things added by hand.</summary>
    private sealed class FakeThingsSource : FakeEnvironmentSource, IThingsSource
    {
        private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

        public FakeThingsSource()
        {
            ExtraState.AddRange(
            [
                (SignalIds.MemoryUsed, 7.1e9, "Bytes"),
                (SignalIds.MemoryTotal, 16e9, "Bytes"),
                (SignalIds.StorageFree, 212e9, "Bytes"),
                (SignalIds.StorageTotal, 512e9, "Bytes"),
                (SignalIds.BatteryLevel, 0.83, "Ratio"),
                ("lan.link.wlan0.speed_mbps", 866, "Count"),
                (SignalIds.NetworkBytesReceived, 48.2e9, "Bytes"),
                (SignalIds.NetworkBytesSent, 9.7e9, "Bytes"),
                (SignalIds.NetworkConnected, 1, "Flag"),
                (SignalIds.NetworkVpn, 1, "Flag"),
                ("lan.link.eth0.speed_mbps", 1000, "Count"),
                ("lan.link.eth1.speed_mbps", 2500, "Count"),
                ("lan.link.docker0.speed_mbps", 10000, "Count"),
            ]);
        }

        public List<DateTimeOffset?> AsOfRequests { get; } = [];

        public Task<ThingsDto?> GetThingsAsync(DateTimeOffset? asOf = null, CancellationToken ct = default)
        {
            AsOfRequests.Add(asOf);
            var laptop = "peer:" + LocalId;
            string[] subjects = ["peer:" + LocalId];
            ThingDto Part(string slot, string name, string kind, string label, string prefix, params ThingFactDto[] facts) =>
                new($"{laptop}/{slot}", name, kind, label, "Installed", [], false, subjects, facts, laptop, [], At, prefix);
            var mac = new ThingFactDto("identity.lan.interface.mac", "Hardware address (MAC)", "3c:22:fb:91:0a:7e", "Measured", "monitoring", At, 0);
            ThingDto[] things =
            [
                new(laptop, "Wilson-laptop", "device.computer", "Computer", "Owned", [], true, subjects,
                    [new ThingFactDto("identity.peer-key-fingerprint", "Peer key fingerprint", "AB12", "Measured", "monitoring", At, 0)],
                    null, [$"{laptop}/processor", $"{laptop}/memory", $"{laptop}/storage", $"{laptop}/battery", $"{laptop}/wlan0", $"{laptop}/ts"], At),
                Part("processor", "Processor", "part.processor", "Processor", "system.cpu."),
                Part("memory", "Memory", "part.memory", "Memory", "system.memory."),
                Part("storage", "Storage", "part.storage", "Storage", "system.storage."),
                Part("battery", "Battery", "part.battery", "Battery", "android.battery."),
                Part("wlan0", "Network adapter wlan0", "part.network", "Network adapter", "lan.link.wlan0.", mac),
                Part("ts", "Virtual network adapter tailscale0", "part.network.virtual", "Virtual network adapter", "lan.link.tailscale0."),
                new("observed:gw", "Router at 192.168.1.1", "device.network.router", "Router", "Owned", [], true, ["lan:gw"], [], null, [], At),
                new("thing:amp", "Focusrite 2i2", "device.audio", "Audio equipment", "Owned", [], false, [],
                    [new ThingFactDto("note.text", "Note", "Bought used. Needs a 12 V supply.", "Entered", "you", At, 0),
                     new ThingFactDto("electrical.power.max", "Max", "5 W", "Specified", "datasheet", At, 0)], null, [], At,
                    Links: [new ThingLinkDto("rel:amp-router", "connects-to", "Connected to", "observed:gw", "Router at 192.168.1.1", true)],
                    Ports: [new ThingPortDto("port:amp-dc", "DC in", "dc-barrel-5521", "Socket", "Input", ["dc-fixed"],
                        [new ThingFactDto("electrical.voltage", "Voltage", "12 V", "Specified", "datasheet", At, 0)],
                        "conn:psu-amp", "thing:psu", "Pedal power", "DC out 1")]),
                new("thing:psu", "Pedal power", "device.power.supply", "Power supply", "Owned", [], false, [], [], null, [], At,
                    Ports: [new ThingPortDto("port:psu-1", "DC out 1", "dc-barrel-5521", "Plug", "Output", ["dc-fixed"],
                                [new ThingFactDto("electrical.voltage", "Voltage", "9 V", "Specified", "datasheet", At, 0)], "conn:psu-amp", "thing:amp", "Focusrite 2i2", "DC in"),
                            new ThingPortDto("port:psu-2", "DC out 2", "dc-barrel-5521", "Plug", "Output", ["dc-fixed"], [])]),
                new("thing:board", "Pedalboard", "assembly.pedalboard", "Pedalboard", "Owned", [], false, [], [], null, [], At),
                new("thing:rack", "Studio rack", "assembly.rack", "Rack", "Idea", [], false, [],
                    [new ThingFactDto("mechanical.rack-units", "Rack units", "Unknown", "Unknown", "you", At, 0)], null, [], At),
            ];
            return Task.FromResult<ThingsDto?>(new ThingsDto(things, [new ThingKindDto("thing", "Thing"), new ThingKindDto("device.audio", "Audio equipment")],
                [new ThingKindDto("powered-by", "Powered by"), new ThingKindDto("connects-to", "Connected to")],
                [new ConnectorChoiceDto("dc-barrel-5521", "DC barrel 5.5/2.1 mm", "Power"), new ConnectorChoiceDto("usb-c", "USB Type-C", "Usb")],
                [new ThingWiringDto("thing:amp", "Pedal power DC out 1 → Focusrite 2i2 DC in", "WillNotWork", "Won't work",
                    [new WiringFindingDto("voltage-mismatch", "VerifiedProblem", "Pedal power DC out 1 supplies 9 V, but Focusrite 2i2 DC in accepts 12 V.")])],
                asOf is not null ? [] :
                [new MissingItemDto("supply-too-weak", "A stronger supply than Pedal power", "Focusrite 2i2 DC in draws 0.5 A, more than the 0.3 A Pedal power DC out 1 can supply.", "VerifiedProblem", ["thing:psu"]),
                 new MissingItemDto("input-unconnected", "Something into Focusrite 2i2 Input 1", "Focusrite 2i2 Input 1 has nothing plugged in; it takes analog-audio.", "Unknown", ["thing:amp"])],
                [new RackLayoutDto("thing:rack", 6, true,
                    [new RackBlockDto(6, 1, null, null, true, false), new RackBlockDto(5, 2, "thing:amp", "Focusrite 2i2", true, false),
                     new RackBlockDto(3, 1, "thing:dsp", "DSP", false, true), new RackBlockDto(2, 1, null, null, true, false), new RackBlockDto(1, 1, null, null, true, false),
                     new RackBlockDto(3, 1, "thing:eq", "EQ", true, true)],
                    [], [new WiringFindingDto("overlap", "LikelyProblem", "DSP (U3, 1U) and EQ (U3, 1U) take the same space.")])],
                [new SurfaceLayoutDto("thing:board", 500, 250, true,
                    [new SurfaceItemDto("thing:fuzz", "Fuzz", 20, 10, 70, 120, true, true), new SurfaceItemDto("thing:delay", "Delay", 60, 40, 100, 130, true, true),
                     new SurfaceItemDto("thing:tuner", "Tuner", 380, 10, 70, 120, false, false)],
                    ["Looper"], [new WiringFindingDto("overlap", "VerifiedProblem", "Delay and Fuzz take the same space."),
                                 new WiringFindingDto("not-placed", "Unknown", "Looper is on Pedalboard but has no position yet.")])]));
        }

        public List<(string Text, string? Kind)> Added { get; } = [];

        public Task AddThingAsync(string text, string? kindId, CancellationToken ct = default)
        {
            Added.Add((text, kindId));
            return Task.CompletedTask;
        }

        public List<ThingEditDto> Edits { get; } = [];

        public Task EditThingAsync(ThingEditDto edit, CancellationToken ct = default)
        {
            Edits.Add(edit);
            return Task.CompletedTask;
        }
    }
}
