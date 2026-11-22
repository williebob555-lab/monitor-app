using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Ui.Workspace;
using Xunit;

namespace MonitorApp.Client.Desktop.Tests.Workspace;

/// <summary>Writes PNGs of the canvas for review. Skipped unless built with -p:ManualShots=true (Skia headless, see TestApp.cs).</summary>
public sealed class SchematicScreenshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Trait("Category", "Manual")]
#if MANUAL_SHOTS
    [AvaloniaFact]
#else
    [AvaloniaFact(Skip = "Manual: run with -p:ManualShots=true to render screenshots.")]
#endif
    public void CanvasScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("MONITOR_UI_SHOTS_DIR") ?? "/tmp/claude-0/shots";
        Directory.CreateDirectory(dir);
        Shot(dir, "canvas-normal.png", 1200, 760, null, null);
        Shot(dir, "canvas-exploded.png", 1200, 760, SampleEnvironment.Server, null);
        Shot(dir, "canvas-selected.png", 1200, 760, null, SampleEnvironment.Phone);
        Shot(dir, "canvas-phone.png", 390, 700, null, null);
    }

    private static void Shot(string dir, string name, double w, double h, string? explode, string? select)
    {
        var canvas = new SchematicCanvas { Scene = SampleEnvironment.Create(Now), ReducedMotion = true };
        var window = new Window { Width = w, Height = h, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        canvas.SelectedNodeId = select;
        canvas.ExplodedNodeId = explode;
        canvas.CompleteAnimations();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, name));
        window.Close();
    }
}
