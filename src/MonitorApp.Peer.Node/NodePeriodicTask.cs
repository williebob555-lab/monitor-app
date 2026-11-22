namespace MonitorApp.Peer.Node;

/// <summary>
/// A named background job the node runs on a fixed schedule (optionally once at startup). Scheduling uses the
/// supplied <see cref="TimeProvider"/>, so tests drive it with a manual clock. A failed run is recorded and the
/// schedule continues; runs never overlap.
/// </summary>
public sealed class NodePeriodicTask : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> work;
    private readonly TimeProvider time;
    private readonly bool runAtStart;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private Task? loop;
    private long runCount;
    private long failureCount;

    public NodePeriodicTask(string name, TimeSpan interval, Func<CancellationToken, Task> work, TimeProvider? time = null, bool runAtStart = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        Name = name;
        Interval = interval;
        this.work = work;
        this.time = time ?? TimeProvider.System;
        this.runAtStart = runAtStart;
    }

    public string Name { get; }

    public TimeSpan Interval { get; }

    /// <summary>Completed runs, successful or not.</summary>
    public long RunCount => Interlocked.Read(ref runCount);

    public long FailureCount => Interlocked.Read(ref failureCount);

    /// <summary>Exception type of the most recent failed run; null after a successful run.</summary>
    public string? LastFailureKind { get; private set; }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref loop, Task.CompletedTask, null) is not null)
        {
            throw new InvalidOperationException($"{Name} is already started.");
        }

        loop = RunLoopAsync();
    }

    /// <summary>Runs the job now (serialised with scheduled runs).</summary>
    public async Task RunNowAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
            LastFailureKind = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastFailureKind = exception.GetType().Name;
            Interlocked.Increment(ref failureCount);
        }
        finally
        {
            Interlocked.Increment(ref runCount);
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        var pending = Volatile.Read(ref loop);
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        lifetime.Dispose();
        gate.Dispose();
    }

    private async Task RunLoopAsync()
    {
        try
        {
            if (runAtStart)
            {
                await RunNowAsync(lifetime.Token).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await RunNowAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
