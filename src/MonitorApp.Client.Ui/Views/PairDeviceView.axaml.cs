using Avalonia.Controls;
using Avalonia.Interactivity;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class PairDeviceView : UserControl
{
    public PairDeviceView() => InitializeComponent();

    private async void OnOpenPhonePairing(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PairDeviceViewModel { PhonePairingPage: { } page } && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(page);
        }
    }
}
