using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Mobile;
using MonitorApp.Client.Ui;
using MonitorApp.Client.Ui.Views;
using MonitorApp.Peer.Application;

namespace MonitorApp.Android;

/// <summary>
/// Avalonia application of the Android head: the shared <see cref="MainView"/> over the in-process phone peer.
/// The runtime itself is owned by <see cref="MobileRuntimeHost"/> (shared with the foreground service); this class
/// only reads it, so closing the UI never stops monitoring.
/// </summary>
public sealed class AndroidApp : Avalonia.Application, IDisposable
{
    private MainViewModel? model;
    private CrashJournal? journal;

    public override void Initialize()
    {
        Styles.Add(new MonitorUiStyles());
        RequestedThemeVariant = ThemeVariant.Default;
        WatchForProblems();
    }

    /// <summary>
    /// An error on the screen thread is recorded, shown in the app and survived instead of closing the app; anything
    /// else that would still close it is recorded first, so the next launch says what happened.
    /// </summary>
    private void WatchForProblems()
    {
        var files = global::Android.App.Application.Context.FilesDir?.AbsolutePath ?? System.IO.Path.GetTempPath();
        journal = new CrashJournal(System.IO.Path.Combine(files, "problems"));
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            var text = journal.Record(args.Exception, "screen", Version, DateTimeOffset.UtcNow);
            args.Handled = true;
            if (model is not null)
            {
                model.CrashNotice = text;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                journal.Record(exception, "background", Version, DateTimeOffset.UtcNow);
            }
        };
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
            journal.Record(args.Exception, "app", Version, DateTimeOffset.UtcNow);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            journal.Record(args.Exception, "background task", Version, DateTimeOffset.UtcNow);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Measures how late the screen thread is: a 100 ms tick that arrives late means taps and scrolling waited that long too. One
    /// <c>[ui]</c> line a minute goes into the log this device sends to its computers.
    /// </summary>
    private static void WatchScreenLag()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var last = watch.Elapsed;
        var windowStart = last;
        int ticks = 0, late50 = 0, late250 = 0, late1000 = 0;
        double lateTotal = 0, worst = 0;
        var timer = new DispatcherTimer(interval, DispatcherPriority.Input, (_, _) =>
        {
            var now = watch.Elapsed;
            var late = Math.Max(0, (now - last - interval).TotalMilliseconds);
            last = now;
            ticks++;
            lateTotal += late;
            worst = Math.Max(worst, late);
            late50 += late >= 50 ? 1 : 0;
            late250 += late >= 250 ? 1 : 0;
            late1000 += late >= 1000 ? 1 : 0;
            if (now - windowStart < TimeSpan.FromMinutes(1))
            {
                return;
            }

            DiagnosticLog.Write("ui", string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"screen thread lateness over {(now - windowStart).TotalSeconds:0} s: {ticks} checks, late by 50 ms+ {late50}, 250 ms+ {late250}, 1 s+ {late1000}; " +
                $"worst {worst:0} ms, total {lateTotal:0} ms"));
            windowStart = now;
            ticks = late50 = late250 = late1000 = 0;
            lateTotal = worst = 0;
        });
        timer.Start();
    }

    /// <summary>
    /// Android records why this app's earlier processes ended (crash, native crash, low memory, killed by the system, stopped by the
    /// person). Logged once per start so a device whose monitoring "just stopped" says why.
    /// </summary>
    private static void LogWhyThePreviousRunEnded(global::Android.Content.Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return;
        }

        try
        {
            var manager = (global::Android.App.ActivityManager?)context.GetSystemService(global::Android.Content.Context.ActivityService);
            var exits = manager?.GetHistoricalProcessExitReasons(context.PackageName, 0, 5);
            foreach (var exit in exits ?? [])
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(exit.Timestamp).UtcDateTime;
                DiagnosticLog.Write("exit", string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"an earlier run ended {at:yyyy-MM-ddTHH:mm:ssZ}: {exit.Reason} ({exit.Description ?? "no description"}); importance {exit.Importance}, memory {exit.Pss / 1024} MB"));
                LogCrashReport(context, exit, at);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticLog.Write("exit", "could not read why earlier runs ended", exception);
        }
    }

    private const int NativeCrash = 5; // ApplicationExitInfo.REASON_CRASH_NATIVE
    private const int NotResponding = 6; // ApplicationExitInfo.REASON_ANR

    /// <summary>
    /// For a native crash or a freeze ("not responding"), sends Android's own report once: the readable parts of the native crash
    /// report (libraries, functions, abort message) or the first lines of the freeze's thread dump. A native crash never reaches the
    /// app's own crash catcher, so this is the only record of where it happened.
    /// </summary>
    private static void LogCrashReport(global::Android.Content.Context context, global::Android.App.ApplicationExitInfo exit, DateTime at)
    {
        var reason = (int)exit.Reason;
        if ((reason != NativeCrash && reason != NotResponding) || !OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return;
        }

        var preferences = context.GetSharedPreferences("diagnostics", global::Android.Content.FileCreationMode.Private);
        if (preferences is null || preferences.GetLong("reported-exit", 0) >= exit.Timestamp)
        {
            return; // already sent
        }

        preferences.Edit()?.PutLong("reported-exit", exit.Timestamp)?.Apply();
        using var stream = exit.TraceInputStream;
        if (stream is null)
        {
            DiagnosticLog.Write("exit", $"no report kept for the {at:yyyy-MM-ddTHH:mm:ssZ} {(reason == NativeCrash ? "native crash" : "freeze")}");
            return;
        }

        var bytes = new byte[2 * 1024 * 1024];
        var length = 0;
        int read;
        while (length < bytes.Length && (read = stream.Read(bytes, length, bytes.Length - length)) > 0)
        {
            length += read;
        }

        var lines = reason == NativeCrash ? ReadableParts(bytes.AsSpan(0, length)) : System.Text.Encoding.UTF8.GetString(bytes, 0, length).Split('\n');
        DiagnosticLog.Write("exit", $"report for the {at:yyyy-MM-ddTHH:mm:ssZ} {(reason == NativeCrash ? "native crash" : "freeze")} ({length} bytes):");
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0).Take(120))
        {
            DiagnosticLog.Write("exit", line);
        }
    }

    /// <summary>The printable strings of a native crash report (a protobuf), in order and without repeats.</summary>
    private static IEnumerable<string> ReadableParts(ReadOnlySpan<byte> data)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= data.Length; i++)
        {
            var printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7f;
            if (printable && start < 0)
            {
                start = i;
            }
            else if (!printable && start >= 0)
            {
                if (i - start >= 6)
                {
                    var text = System.Text.Encoding.ASCII.GetString(data[start..i]);
                    if (seen.Add(text))
                    {
                        parts.Add(text);
                    }
                }

                start = -1;
            }
        }

        return parts;
    }

    private static string Version =>
        typeof(AndroidApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var view = new MainView();
            single.MainView = view;
            _ = AttachAsync(view);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose()
    {
        model?.Dispose();
        model = null;
    }

    private async Task AttachAsync(MainView view)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var runtime = await MobileRuntimeHost.GetAsync(context).ConfigureAwait(true);
            if (PeerForegroundService.ResumeIfLeftOn(context))
            {
                DiagnosticLog.Write("node", "resuming monitoring on app open (it was left on)");
            }

            var source = new InProcessPeerDataSource(
                runtime,
                startHost: () => PeerForegroundService.Start(context),
                stopHost: () => PeerForegroundService.Stop(context),
                qrScanner: new AndroidQrScanner(context),
                updater: MobileRuntimeHost.GetUpdater(context));
            LogWhyThePreviousRunEnded(context);
            var crash = journal?.Take();
            if (crash is not null)
            {
                // Also into the log this device sends to its computers, so a crash can be read there without the screen.
                // One line per stack line: computers keep at most 1000 characters of each uploaded line.
                DiagnosticLog.Write("crash", "the previous run ended with this problem:");
                foreach (var line in crash.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(60))
                {
                    DiagnosticLog.Write("crash", line);
                }
            }

            model = new MainViewModel(source) { CrashNotice = crash };
            WatchScreenLag();
            view.DataContext = model;
            await model.ConnectAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            view.Content = new TextBlock
            {
                Text = $"Peer runtime unavailable: {exception.Message}",
                Margin = new Thickness(16),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            };
        }
    }
}
