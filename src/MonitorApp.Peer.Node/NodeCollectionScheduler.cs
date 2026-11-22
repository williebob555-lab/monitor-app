using MonitorApp.Peer.Collection;

namespace MonitorApp.Peer.Node;

public sealed class NodeCollectionScheduler : IAsyncDisposable
{
    private readonly PeerSystemMetricsIngestionBridge collection;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;

    public NodeCollectionScheduler(
        PeerSystemMetricsIngestionBridge collection,
        TimeSpan? interval = null)
    {
        this.collection = collection ?? throw new ArgumentNullException(nameof(collection));
        this.interval = interval ?? TimeSpan.FromMinutes(1);
        if (this.interval < TimeSpan.FromSeconds(10) || this.interval > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public string? LastFailureKind { get; private set; }

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
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
        {
            try
            {
                var result = await collection.RunOnceAsync(lifetime.Token).ConfigureAwait(false);
                if (result.Status == PeerCollectionRunStatus.Completed)
                {
                    LastFailureKind = null;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                throw;
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
}
