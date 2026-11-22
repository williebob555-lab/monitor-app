using Avalonia;
using Avalonia.Controls;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Desktop.Views;

// The CTS is disposed when the view detaches from the visual tree, which is this control's lifetime end.
#pragma warning disable CA1001
public sealed partial class StateView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? live;

    public StateView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is StateViewModel model)
        {
            // Updates run only while this view is on screen; the source decides between push and polling.
            live = new CancellationTokenSource();
            _ = model.RunLiveAsync(PollInterval, live.Token);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        live?.Cancel();
        live?.Dispose();
        live = null;
        base.OnDetachedFromVisualTree(e);
    }
}
#pragma warning restore CA1001
