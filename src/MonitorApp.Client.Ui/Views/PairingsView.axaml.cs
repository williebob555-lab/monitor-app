using Avalonia.Controls;
using Avalonia.Interactivity;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class PairingsView : UserControl
{
    public PairingsView() => InitializeComponent();

    private async void OnOpenPhonePairing(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PairingsViewModel { PhonePairingPage: { } page } && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(page);
        }
    }
}
