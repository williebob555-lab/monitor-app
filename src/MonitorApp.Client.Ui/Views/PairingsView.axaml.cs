using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class PairingsView : UserControl
{
    /// <summary>While the screen is open, each device's connection line and any new pairing request are kept current.</summary>
    private readonly DispatcherTimer connections = new() { Interval = TimeSpan.FromSeconds(3) };

    public PairingsView()
    {
        InitializeComponent();
        connections.Tick += async (_, _) =>
        {
            if (DataContext is PairingsViewModel model && MonitorApp.Client.Core.AppVisibility.IsVisible)
            {
                await model.RefreshConnectionsAsync();
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        connections.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        connections.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
