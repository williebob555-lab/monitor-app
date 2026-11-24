using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;


namespace MonitorApp.Client.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UpdatesViewModel.Restarter = new DesktopRestarter(desktop);
            var address = ResolveAddress();
            var model = new MainViewModel(address);
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => model.Dispose();
            window.Opened += async (_, _) =>
            {
                await model.ConnectAsync();
                // The node runs as a service account; give it this person's Syncthing key so it can read folder status.
                _ = SyncthingHandover.HandOverAsync(model.CurrentSource, address);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The node address comes from MONITOR_NODE_ADDRESS when set, else the loopback default; it can be edited in the window.</summary>
    private static Uri ResolveAddress() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("MONITOR_NODE_ADDRESS"), UriKind.Absolute, out var uri)
            ? uri
            : LocalApiClient.DefaultAddress;
}
