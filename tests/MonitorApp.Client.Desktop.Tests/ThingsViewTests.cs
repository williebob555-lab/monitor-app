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
        Assert.Contains(laptop.Parts, p => p.Name == "Memory" && p.Values.Any(v => v.Label == "Memory total" && v.Badge == "measured · live"));

        things.OpenCommand.Execute(laptop);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var panel = window.GetVisualDescendants().OfType<ExplodedPanel>().Single();
        Assert.Equal(1 + laptop.Parts.Count, panel.Children.Count);
        Assert.True(panel.Bounds.Width > 0);

        things.CloseCommand.Execute(null);
        Assert.True(things.IsGrid);
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
            ]);
        }

        public Task<ThingsDto?> GetThingsAsync(CancellationToken ct = default)
        {
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
                     new ThingFactDto("electrical.power.max", "Max", "5 W", "Specified", "datasheet", At, 0)], null, [], At),
                new("thing:rack", "Studio rack", "assembly.rack", "Rack", "Idea", [], false, [],
                    [new ThingFactDto("mechanical.rack-units", "Rack units", "Unknown", "Unknown", "you", At, 0)], null, [], At),
            ];
            return Task.FromResult<ThingsDto?>(new ThingsDto(things, [new ThingKindDto("thing", "Thing"), new ThingKindDto("device.audio", "Audio equipment")]));
        }

        public Task AddThingAsync(string text, string? kindId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
