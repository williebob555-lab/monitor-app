using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

/// <summary>
/// Port between a held-open authenticated peer session (client connector or server handler) and the local
/// application: which origin batches to push, which interests to send, and what to do with remote interests.
/// Origin data still flows through the ordinary signed per-origin replication messages.
/// </summary>
public interface IPeerLiveSession : IDisposable
{
    /// <summary>Client side: ask the remote to push its origin records and interests over this session.</summary>
    bool RequestRemotePush { get; }

    /// <summary>
    /// True when this session should end so the peers reconnect and swap session-start state again (mesh membership
    /// changed after this session exchanged it). Both ends check it between sends.
    /// </summary>
    bool RenewRequested => false;

    /// <summary>Maximum unacknowledged origin batches this side keeps in flight.</summary>
    int MaximumInFlightBatches { get; }

    /// <summary>The next not-yet-sent local origin batch, or null when caught up or the in-flight window is full.</summary>
    ValueTask<OriginRecordBatch?> TryNextBatchAsync(CancellationToken cancellationToken);

    /// <summary>Records the remote's acknowledgement of a pushed batch and frees in-flight capacity.</summary>
    void OnBatchAcknowledged(OriginCursor cursor);

    /// <summary>Interests that must be sent now (first declaration, lease renewal, or withdrawal).</summary>
    IReadOnlyList<LiveInterest> DrainOutgoingInterests();

    /// <summary>Applies an interest declared by the authenticated remote peer.</summary>
    ValueTask<LiveInterestAck> AcceptRemoteInterestAsync(LiveInterest interest, CancellationToken cancellationToken);

    /// <summary>Wakes the sender loop (for example when the receive side has follow-up work).</summary>
    void Nudge();

    /// <summary>Completes when new local records, acknowledgements or interest changes may need attention, or after the timeout.</summary>
    Task WaitForActivityAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
