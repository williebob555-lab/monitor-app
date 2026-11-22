namespace MonitorApp.Peer.Service;

/// <summary>
/// Counts the live <c>PeerSession</c> handlers (including their push loops, which the handler awaits in its finally).
/// Kestrel's stop can return after its timeout while a handler is still unwinding; the owner of the host awaits
/// <see cref="WaitForIdleAsync"/> before it disposes the stores those handlers use.
/// </summary>
public sealed class PeerSessionHandlerTracker
{
    private readonly object gate = new();
    private int active;
    private TaskCompletionSource idle = NewCompleted();

    public int Active
    {
        get
        {
            lock (gate)
            {
                return active;
            }
        }
    }

    internal IDisposable Enter()
    {
        lock (gate)
        {
            if (active++ == 0)
            {
                idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new Scope(this);
    }

    /// <summary>Completes when no handler is running; false when <paramref name="timeout"/> elapsed first.</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task current;
        lock (gate)
        {
            current = idle.Task;
        }

        return await Task.WhenAny(current, Task.Delay(timeout)).ConfigureAwait(false) == current;
    }

    private void Exit()
    {
        lock (gate)
        {
            if (--active == 0)
            {
                idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource NewCompleted()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Scope(PeerSessionHandlerTracker owner) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
