using Avalonia;
using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class AlertsView : UserControl, IDisposable
{
    private LiveRun? live;

    public AlertsView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        live = LiveRun.Start(DataContext);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        live?.Dispose();
        live = null;
    }
}
