using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;

namespace MonitorApp.Peer.Node;

public sealed class NodeCollectionScheduler : IAsyncDisposable
{
    private readonly PeerSystemMetricsIngestionBridge collection;
    private readonly TimeSpan interval;
    private readonly LiveInterestRegistry? interests;
    private readonly string? subjectPeerId;
    private readonly MonitoringControlState? control;
    private readonly string collectorId;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    private long currentIntervalTicks;

    /// <param name="interests">When set (with <paramref name="subjectPeerId"/>), live interest in this peer's own data
    /// switches sampling to the registry's fast interval until the interest lapses.</param>
    public NodeCollectionScheduler(
        PeerSystemMetricsIngestionBridge collection,
        TimeSpan? interval = null,
        LiveInterestRegistry? interests = null,
        string? subjectPeerId = null,
        MonitoringControlState? control = null,
        string collectorId = "monitor.system")
    {
        this.control = control;
        this.collectorId = collectorId;
        this.interests = interests;
        this.subjectPeerId = subjectPeerId;
        this.collection = collection ?? throw new ArgumentNullException(nameof(collection));
        this.interval = interval ?? TimeSpan.FromMinutes(1);
        if (this.interval < TimeSpan.FromSeconds(10) || this.interval > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public string? LastFailureKind { get; private set; }

    /// <summary>The sampling interval currently in force (fast while live interest exists).</summary>
    public TimeSpan CurrentInterval => TimeSpan.FromTicks(Interlocked.Read(ref currentIntervalTicks));

    public void Start()
    {
        if (Interlocked.CompareExchange(ref loop, Task.CompletedTask, null) is not null)
        {
            throw new InvalidOperationException("Collection scheduling is already started.");
        }

        loop = RunAsync();
    }

    public Task<PeerCollectionRunResult> RunOnceAsync(
        CancellationToken cancellationToken = default) => collection.RunOnceAsync(cancellationToken);

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
    }

    private async Task RunAsync()
    {
        Interlocked.Exchange(ref currentIntervalTicks, interval.Ticks);
        try
        {
            await AdaptiveSampler.RunAsync(
                CollectAsync,
                subjectPeerId ?? "local",
                interval,
                subjectPeerId is null ? null : interests,
                collectImmediately: false,
                onInterval: value => Interlocked.Exchange(ref currentIntervalTicks, value.Ticks),
                lifetime.Token,
                control).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>True while an operator has paused this collector (scheduled collection is skipped).</summary>
    public bool IsPaused => control?.IsPaused(collectorId) == true;

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        if (IsPaused)
        {
            return;
        }

        try
        {
            var result = await collection.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == PeerCollectionRunStatus.Completed)
            {
                LastFailureKind = null;
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidOperationException or
            TimeoutException)
        {
            LastFailureKind = exception.GetType().Name;
        }
    }
}
