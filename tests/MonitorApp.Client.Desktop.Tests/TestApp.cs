using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using MonitorApp.Client.Desktop;
using MonitorApp.Client.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MonitorApp.Client.Desktop.Tests;

public static class TestAppBuilder
{
#if MANUAL_SHOTS
    /// <summary>Manual screenshot builds render with Skia (real pixels); CI uses the fast headless drawing.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
#else
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
#endif
}
