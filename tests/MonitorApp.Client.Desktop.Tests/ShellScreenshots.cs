using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Shell;
using MonitorApp.Client.Ui.Views;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests;

/// <summary>
/// Manual tool, not a test of behaviour: renders the shell over SampleEnvironment data to PNGs for design review.
/// Skipped unless built with -p:ManualShots=true (which also switches the test app to Skia rendering, see TestApp.cs). Output: /tmp/claude-0/shots (override with MONITOR_UI_SHOTS_DIR).
/// Example: dotnet test tests/MonitorApp.Client.Desktop.Tests -p:ManualShots=true --filter Category=Manual
/// </summary>
public sealed class ShellScreenshots
{
    [Trait("Category", "Manual")]
#if MANUAL_SHOTS
    [AvaloniaFact]
#else
    [AvaloniaFact(Skip = "Manual: run with -p:ManualShots=true to render screenshots.")]
#endif
    public void RenderShellScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("MONITOR_UI_SHOTS_DIR") ?? "/tmp/claude-0/shots";
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.UtcNow;

        var source = new FakeControlEnvironmentSource { PairingsHeldBy = SampleEnvironment.Server };
        source.PendingPairings.Add(new PendingPairingDto(
            "pair-watch", "peer-watch", "Pixel Watch", "9F2C4E7A1B3D5F60", "PendingApproval", now, now.AddMinutes(5)));
        using var model = new MainViewModel(source);

        // The Dive (default): home grid wide and on a phone, a device, the root-cause part, and a recorded moment.
        model.Environment.ShowScene(DiveScenes.BackupDiskFull(now));
        model.Shell.Dive.RefreshApprovalsAsync().GetAwaiter().GetResult();
        Capture(model, 1400, 860, dir, "dive-home-wide-1400x860", () => model.Shell.Dive.GoHome());
        Capture(model, 860, 860, dir, "dive-home-medium-860x860", () => model.Shell.Dive.GoHome());
        Capture(model, 390, 844, dir, "dive-home-phone-390x844", () => model.Shell.Dive.GoHome());
        Capture(model, 1400, 860, dir, "dive-device-wide-1400x860", () => model.Shell.Dive.GoTo(SampleEnvironment.Server));
        Capture(model, 390, 844, dir, "dive-device-phone-390x844", () => model.Shell.Dive.GoTo(SampleEnvironment.Server));
        Capture(model, 1400, 860, dir, "dive-service-wide-1400x860", () => model.Shell.Dive.GoTo(SampleEnvironment.Backup));
        Capture(model, 1400, 860, dir, "dive-part-wide-1400x860", () =>
        {
            model.Shell.Dive.GoHome();
            model.Shell.Dive.DiveToCause();
        });
        Capture(model, 390, 844, dir, "dive-part-phone-390x844", () => { });
        Capture(model, 1400, 860, dir, "dive-recorded-wide-1400x860", () =>
        {
            model.Shell.Dive.GoHome();
            model.Environment.AsOf = now.AddHours(-3);
        });
        Capture(model, 1400, 860, dir, "dive-recorded-device-1400x860", () => model.Shell.Dive.GoTo(SampleEnvironment.Server));
        model.Environment.AsOf = null;
        model.Shell.Dive.GoHome();

        // The outline with the inspector (the accessible alternative to the Dive).
        model.Environment.ShowScene(SampleEnvironment.Create(now));
        model.Shell.OutlineOpen = true;
        var wide = Capture(model, 1400, 860, dir, "wide-1400x860-selected", () => model.Environment.SelectedNodeId = SampleEnvironment.Server);
        _ = wide;
        Capture(model, 1400, 860, dir, "wide-1400x860-recorded", () =>
        {
            model.Environment.SelectedNodeId = SampleEnvironment.Phone;
            model.Environment.AsOf = now.AddHours(-3);
        });
        model.Environment.AsOf = null;
        model.Environment.SelectedNodeId = null;
        Capture(model, 390, 844, dir, "phone-390x844-ecosystem", () => { });
        Capture(model, 390, 844, dir, "phone-390x844-sheet", () => model.Environment.SelectedNodeId = SampleEnvironment.Backup);
        Capture(model, 390, 844, dir, "phone-390x844-search", () =>
        {
            model.Environment.SelectedNodeId = null;
            model.Shell.GoTo(ShellMode.Search);
            model.Shell.Search.Query = "cpu";
        });
    }

    private static string Capture(MainViewModel model, double width, double height, string dir, string name, Action arrange)
    {
        model.Shell.GoTo(name.Contains("search", StringComparison.Ordinal) ? ShellMode.Search : ShellMode.Ecosystem);
        var shell = new WorkspaceShell { RunEnvironmentLive = false, ReducedMotion = true, DataContext = model };
        var window = new Window { Width = width, Height = height, Content = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        arrange();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        // Let the sheet slide-up (0.22 s) finish before capturing: the headless render timer only ticks when forced.
        for (var i = 0; i < 12; i++)
        {
            Thread.Sleep(30);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered; is Skia enabled?");
        var path = Path.Combine(dir, name + ".png");
        frame.Save(path);
        window.Close();
        return path;
    }
}
