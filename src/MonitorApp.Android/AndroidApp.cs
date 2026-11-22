using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Mobile;
using MonitorApp.Client.Ui;
using MonitorApp.Client.Ui.Views;

namespace MonitorApp.Android;

/// <summary>
/// Avalonia application of the Android head: the shared <see cref="MainView"/> over the in-process phone peer.
/// The runtime itself is owned by <see cref="MobileRuntimeHost"/> (shared with the foreground service); this class
/// only reads it, so closing the UI never stops monitoring.
/// </summary>
public sealed class AndroidApp : Avalonia.Application, IDisposable
{
    private MainViewModel? model;

    public override void Initialize()
    {
        Styles.Add(new MonitorUiStyles());
        RequestedThemeVariant = ThemeVariant.Default;
    }

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
                qrScanner: new AndroidQrScanner(context));
            model = new MainViewModel(source);
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
