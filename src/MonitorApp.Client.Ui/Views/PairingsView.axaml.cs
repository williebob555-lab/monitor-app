using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class PairingsView : UserControl
{
    /// <summary>While the screen is open, each device's connection line is kept current.</summary>
    private readonly DispatcherTimer connections = new() { Interval = TimeSpan.FromSeconds(5) };

    public PairingsView()
    {
        InitializeComponent();
        connections.Tick += async (_, _) =>
        {
            if (DataContext is PairingsViewModel model)
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
