using Avalonia;
using Avalonia.Headless;
using MonitorApp.Client.Desktop;
using MonitorApp.Client.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MonitorApp.Client.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        // Real Skia rendering (not headless drawing) so the pinned SkiaSharp native build is exercised by every UI test.
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
