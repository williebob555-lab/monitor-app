using Avalonia;
using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Views;

public sealed partial class DevicesView : UserControl, IDisposable
{
    private LiveRun? live;

    public DevicesView() => InitializeComponent();

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
