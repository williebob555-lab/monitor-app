using Avalonia.Controls;
using Avalonia.Interactivity;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await model.RefreshAsync();
        await clipboard.SetTextAsync(model.Text);
        this.FindControl<TextBlock>("CopyResult")!.Text = "Copied. Paste it into a message.";
    }
}
