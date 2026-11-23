using System.Threading.Channels;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Application;

public sealed record PeerLiveSessionOptions(
    PeerStore Store,
    string RemotePeerId,
    Func<OriginRangeRequest, CancellationToken, ValueTask<OriginRecordBatch>> ReadOrigin,
    Func<CancellationToken, ValueTask> RequireActive,
    LiveInterestRegistry? Interests,
    bool RequestRemotePush,
    int MaximumInFlightBatches = 4,
    int MaximumBatchRecords = 256,
    int MaximumBatchBytes = 1024 * 1024,
    bool LocallyInitiated = false,
    Func<bool>? RenewRequested = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<OriginRangeRequest>>>? RelayRequests = null)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Store);
        ArgumentException.ThrowIfNullOrWhiteSpace(RemotePeerId);
        ArgumentNullException.ThrowIfNull(ReadOrigin);
        ArgumentNullException.ThrowIfNull(RequireActive);
        if (MaximumInFlightBatches is < 1 or > 64 || MaximumBatchRecords is < 1 or > 10_000 || MaximumBatchBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(PeerLiveSessionOptions));
        }
    }
}

/// <summary>
/// Live push state for one held-open session with one remote peer: wakes on newly committed local records, reads the
/// next unsent range of the local origin stream through the ordinary (authorising) range reader, and bounds the
/// unacknowledged batches. A receiver that acknowledges below what was sent (a gap) makes the sender rewind to the
/// acknowledged cursor, so cursors and gaps stay authoritative and delivery stays idempotent.
/// </summary>
public sealed class PeerLiveSession : IPeerLiveSession
{
    private readonly PeerLiveSessionOptions options;
    private readonly Channel<byte> signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Queue<ulong> inFlight = new();
    private readonly object gate = new();
    private readonly IDisposable storeSubscription;
    private readonly IDisposable? interestSubscription;
    private LocalPeerIdentity? local;
    private ulong sentThrough;
    private bool initialized;
    private readonly Dictionary<string, (DateTimeOffset Sent, string Key, uint Hops)> sentInterests = new(StringComparer.Ordinal);
    private LivePushChannels.Channel? channel;

    public PeerLiveSession(PeerLiveSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        storeSubscription = options.Store.Live.Subscribe(_ => Wake());
        interestSubscription = options.Interests?.Subscribe(Wake);
    }

    public bool RequestRemotePush => options.RequestRemotePush;

    public bool RenewRequested => options.RenewRequested?.Invoke() ?? false;

    public int MaximumInFlightBatches => options.MaximumInFlightBatches;

    public ValueTask<IReadOnlyList<OriginRangeRequest>> NextRelayRequestsAsync(CancellationToken cancellationToken) =>
        options.RelayRequests?.Invoke(cancellationToken) ?? ValueTask.FromResult<IReadOnlyList<OriginRangeRequest>>([]);

    public async ValueTask<OriginRecordBatch?> TryNextBatchAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (inFlight.Count >= options.MaximumInFlightBatches)
            {
                return null;
            }
        }

        if (options.Interests is { } relaying && relaying.HasRelayFor(options.RemotePeerId))
        {
            // Relayed interest is only passed to a peer that is still actively trusted here.
            await options.RequireActive(cancellationToken).ConfigureAwait(false);
        }

        var identity = await options.Store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Local peer identity is not initialized.");
        if (options.Interests is { } registry)
        {
            // Both peers may hold a session to each other; only the active channel pushes this origin.
            channel ??= registry.PushChannels.Join(options.RemotePeerId, identity.PeerId, options.LocallyInitiated, Wake);
            if (!channel.IsActive)
            {
                lock (gate)
                {
                    // Resume from the persisted cursor if this session becomes the channel later.
                    initialized = false;
                    inFlight.Clear();
                }

                return null;
            }
        }

        if (!initialized)
        {
            var retained = await options.Store.ReadReplicationCursorAsync(
                options.RemotePeerId, identity.PeerId, identity.OriginStreamId, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                local = identity;
                sentThrough = retained.Cursor?.AcceptedThrough ?? 0;
                initialized = true;
            }
        }

        var head = identity.NextSequence - 1;
        ulong first;
        lock (gate)
        {
            first = sentThrough + 1;
        }

        if (first > head)
        {
            return null;
        }

        var batch = await options.ReadOrigin(new OriginRangeRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            OriginPeerId = identity.PeerId,
            OriginStreamId = identity.OriginStreamId,
            FirstSequence = first,
            LastSequence = head,
            MaximumRecordCount = (uint)options.MaximumBatchRecords,
            MaximumBatchBytes = (uint)options.MaximumBatchBytes,
        }, cancellationToken).ConfigureAwait(false);
        if (batch.Records.Count == 0 && batch.PrunedRange is null)
        {
            return null;
        }

        lock (gate)
        {
            channel?.RecordsSent(batch.Records.Count);
            // A batch that is only a pruned-range statement advances the stream to the end of that range.
            sentThrough = batch.Records.Count == 0 ? batch.PrunedRange!.LastSequence : batch.Records[^1].Sequence;
            inFlight.Enqueue(sentThrough);
        }

        return batch;
    }

    public void OnBatchAcknowledged(OriginCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        lock (gate)
        {
            if (local is null || cursor.OriginPeerId != local.PeerId || cursor.OriginStreamId != local.OriginStreamId)
            {
                return;
            }

            if (inFlight.TryDequeue(out var batchLast) && cursor.AcceptedThroughSequence < batchLast)
            {
                // The receiver has a gap below what was sent: resend from its authoritative cursor.
                sentThrough = cursor.AcceptedThroughSequence;
                inFlight.Clear();
            }
        }

        Wake();
    }

    public IReadOnlyList<LiveInterest> DrainOutgoingInterests()
    {
        var interests = options.Interests;
        if (interests is null)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var renewEvery = interests.Policy.OutgoingLease / 3;
        var desired = interests.OutgoingInterestsFor(options.RemotePeerId);
        var drained = new List<LiveInterest>();
        lock (gate)
        {
            foreach (var item in desired)
            {
                var key = $"{item.RelayHops}|{string.Join('\n', item.Signals)}";
                if (sentInterests.TryGetValue(item.Subject, out var previous) && now - previous.Sent < renewEvery && previous.Key == key)
                {
                    continue;
                }

                sentInterests[item.Subject] = (now, key, item.RelayHops);
                var interest = new LiveInterest
                {
                    SubjectPeerId = item.Subject,
                    LeaseSeconds = (uint)item.Lease.TotalSeconds,
                    RelayHopsRemaining = item.RelayHops,
                };
                interest.SignalIds.AddRange(item.Signals);
                drained.Add(interest);
            }

            foreach (var (subject, state) in sentInterests.Where(entry => desired.All(item => item.Subject != entry.Key)).ToArray())
            {
                sentInterests.Remove(subject);
                drained.Add(new LiveInterest { SubjectPeerId = subject, LeaseSeconds = 0, RelayHopsRemaining = state.Hops });
            }
        }

        return drained;
    }

    public async ValueTask<LiveInterestAck> AcceptRemoteInterestAsync(
        LiveInterest interest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interest);
        await options.RequireActive(cancellationToken).ConfigureAwait(false);
        var identity = await options.Store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Local peer identity is not initialized.");
        if (options.Interests is null)
        {
            return new LiveInterestAck
            {
                SubjectPeerId = interest.SubjectPeerId,
                Accepted = false,
                SafeMessage = "Live interest is not supported by this peer.",
            };
        }

        return options.Interests.Grant(options.RemotePeerId, identity.PeerId, interest);
    }

    public async Task WaitForActivityAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var effective = timeout;
        var interests = options.Interests;
        if (interests is not null)
        {
            lock (gate)
            {
                foreach (var (_, state) in sentInterests)
                {
                    var due = interests.Policy.OutgoingLease / 3 - (DateTimeOffset.UtcNow - state.Sent);
                    effective = due < effective ? due : effective;
                }
            }
        }

        if (effective <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(effective);
        try
        {
            _ = await signal.Reader.ReadAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout: the caller re-evaluates heartbeats, interest renewal and the poll safety net.
        }
    }

    public void Dispose()
    {
        storeSubscription.Dispose();
        interestSubscription?.Dispose();
        channel?.Dispose();
    }

    public void Nudge() => Wake();

    private void Wake() => signal.Writer.TryWrite(0);
}
