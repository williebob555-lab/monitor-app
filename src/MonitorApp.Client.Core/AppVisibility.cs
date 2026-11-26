namespace MonitorApp.Client.Core;

/// <summary>
/// Whether anyone can see the app. On Android the activity is stopped (screen off, another app in front) without
/// its views being detached, so view lifetimes alone keep live loops refreshing and asking peers for fast readings in
/// the dark. The Android host reports its visibility here; desktop hosts never change it, so it stays visible.
/// </summary>
public static class AppVisibility
{
    private static readonly object Gate = new();
    private static TaskCompletionSource visible = Completed();

    /// <summary>Raised on every change, with the new value.</summary>
    public static event Action<bool>? Changed;

    public static bool IsVisible
    {
        get
        {
            lock (Gate)
            {
                return visible.Task.IsCompleted;
            }
        }
    }

    public static void Set(bool isVisible)
    {
        lock (Gate)
        {
            if (isVisible == visible.Task.IsCompleted)
            {
                return;
            }

            if (isVisible)
            {
                visible.TrySetResult();
            }
            else
            {
                visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Changed?.Invoke(isVisible);
    }

    /// <summary>Completes at once while visible; otherwise when the app is next shown.</summary>
    public static Task WhenVisibleAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (Gate)
        {
            task = visible.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
