using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// Runs a section's live loop for exactly as long as its view is on screen (attach to detach), which also bounds the
/// viewing interest the loop declares to the peers being shown.
/// </summary>
internal sealed class LiveRun : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource cancellation = new();

    private LiveRun(LiveSectionViewModel model) => _ = model.RunLiveAsync(PollInterval, cancellation.Token);

    public static LiveRun? Start(object? dataContext) =>
        dataContext is LiveSectionViewModel model ? new LiveRun(model) : null;

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
