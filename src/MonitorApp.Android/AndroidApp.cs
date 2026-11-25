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
            var source = new InProcessPeerDataSource(
                runtime,
                startHost: () => PeerForegroundService.Start(context),
                stopHost: () => PeerForegroundService.Stop(context),
                qrScanner: new AndroidQrScanner(context),
                updater: MobileRuntimeHost.GetUpdater(context));
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
