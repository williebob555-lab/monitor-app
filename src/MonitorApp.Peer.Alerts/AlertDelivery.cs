using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Alerts;

/// <summary>
/// Desktop side of alert delivery over an already authenticated PeerSession. Only active trusted peers may pull;
/// a recipient sees only outbox rows addressed to its own peer id. Delivery is at-least-once: rows are cleared only
/// by the recipient's acknowledgement, which it sends after a durable write.
/// </summary>
public sealed class AlertDeliveryService(
    PeerStore peers,
    AlertStore alerts,
    AlertRulesConfig config,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> waiters =
        new(StringComparer.Ordinal);
    private int subscribed;

    /// <summary>
    /// Live-session push: returns the next pending batch for an open session to this recipient as soon as one exists
    /// (immediately if already pending), or null after <paramref name="timeout"/>. The batch has an empty request id;
    /// outbox and acknowledgement semantics are identical to <see cref="PullAsync"/>, so a session may mix both.
    /// </summary>
    public async Task<AlertBatch?> WaitForPushAsync(
        string remotePeerId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref subscribed, 1, 0) == 0)
        {
            alerts.OutboxEnqueued += Signal;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var waiter = waiters.GetOrAdd(remotePeerId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var batch = await PullAsync(remotePeerId, new AlertPull { MaximumCount = (uint)PeerContractRules.MaximumAlertsPerBatch }, deadline.Token)
                    .ConfigureAwait(false);
                if (batch.Alerts.Count > 0)
                {
                    return batch;
                }

                await waiter.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Signal(IReadOnlyCollection<string> recipients)
    {
        foreach (var recipient in recipients)
        {
            if (waiters.TryRemove(recipient, out var waiter))
            {
                waiter.TrySetResult();
            }
        }
    }

    public async Task<AlertBatch> PullAsync(string remotePeerId, AlertPull pull, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pull);
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().ToUniversalTime();
        await alerts.TouchPeerAsync(remotePeerId, now, cancellationToken).ConfigureAwait(false);
        await alerts.RegisterRecipientAsync(remotePeerId, now, config.OutboxTtl, cancellationToken).ConfigureAwait(false);
        var maximum = (int)Math.Clamp(pull.MaximumCount, 1u, (uint)PeerContractRules.MaximumAlertsPerBatch);
        var (pending, hasMore) = await alerts.ReadPendingAsync(remotePeerId, now, maximum, cancellationToken)
            .ConfigureAwait(false);
        await alerts.RecordAttemptsAsync(remotePeerId, pending.Select(item => item.AlertId), now, cancellationToken)
            .ConfigureAwait(false);
        var batch = new AlertBatch { RequestId = pull.RequestId, HasMore = hasMore };
        batch.Alerts.Add(pending.Select(item => item.ToContract()));
        return batch;
    }

    public async Task AcknowledgeAsync(string remotePeerId, AlertAck ack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ack);
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        await alerts.AcknowledgeAsync(
            remotePeerId,
            ack.Entries.Select(item => (item.AlertId, item.Revision)),
            clock.GetUtcNow().ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireActiveAsync(string remotePeerId, CancellationToken cancellationToken)
    {
        var trusted = await peers.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null || trusted.TrustState != PeerTrustState.Active)
        {
            throw new AlertDeliveryUnauthorizedException("Peer trust is not active.");
        }
    }
}

/// <summary>Periodic owner of rule evaluation inside one peer process; also trims delivery bookkeeping (never alert events).</summary>
public sealed class AlertEngine : IAsyncDisposable
{
    private static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);
    private readonly AlertEvaluator evaluator;
    private readonly AlertStore store;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;

    public AlertEngine(AlertEvaluator evaluator, AlertStore store, TimeSpan? interval = null)
    {
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.interval = interval ?? TimeSpan.FromSeconds(30);
        if (this.interval < TimeSpan.FromMilliseconds(50) || this.interval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
    }

    public string? LastFailureKind { get; private set; }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref loop, Task.CompletedTask, null) is not null)
        {
            throw new InvalidOperationException("Alert evaluation is already started.");
        }

        loop = Task.Run(RunAsync, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        var pending = Volatile.Read(ref loop);
        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }

        lifetime.Dispose();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await evaluator.EvaluateOnceAsync(lifetime.Token).ConfigureAwait(false);
                await store.PurgeOutboxAsync(DateTimeOffset.UtcNow - OutboxRetention, lifetime.Token).ConfigureAwait(false);
                LastFailureKind = null;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or
                InvalidOperationException or TimeoutException)
            {
                LastFailureKind = exception.GetType().Name;
            }
        }
        while (await WaitAsync(timer).ConfigureAwait(false));
    }

    private async Task<bool> WaitAsync(PeriodicTimer timer)
    {
        try
        {
            return await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
