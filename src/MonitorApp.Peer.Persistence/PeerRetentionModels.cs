namespace MonitorApp.Peer.Persistence;

/// <summary>
/// One UTC hour of one (origin, subject, signal) series. Numeric statistics cover only Valid or Estimated numeric
/// samples; unavailable, denied and error states are counted, never averaged into numbers.
/// </summary>
public sealed record HourlyRollup(
    string OriginPeerId,
    string SubjectEntityId,
    string SignalId,
    DateTimeOffset HourStart,
    int SampleCount,
    int NumericCount,
    double? MinValue,
    double? MaxValue,
    double? SumValue,
    string? Unit,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string WorstQuality,
    int ValidCount,
    int EstimatedCount,
    int UnavailableCount,
    int DeniedCount,
    int ErrorCount,
    int OtherQualityCount);

public sealed record HourlyRollupKey(string OriginPeerId, string SubjectEntityId, string SignalId, DateTimeOffset HourStart);

public sealed record HourlyRollupQuery(
    DateTimeOffset From,
    DateTimeOffset ToExclusive,
    string? SubjectEntityId,
    string? SignalId,
    string? OriginPeerId,
    HourlyRollupKey? After,
    int Limit);

public sealed record HourlyRollupPage(IReadOnlyList<HourlyRollup> Rollups, bool Truncated);

/// <summary>A user-pinned time window whose raw records are exempt from retention pruning.</summary>
public sealed record PinnedIncident(string IncidentId, string Label, DateTimeOffset From, DateTimeOffset To, DateTimeOffset CreatedAt);

/// <summary>A contiguous run of origin sequences removed by retention; replicas get an explicit outcome for it.</summary>
public sealed record PrunedOriginRange(string OriginPeerId, string OriginStreamId, ulong FirstSequence, ulong LastSequence, DateTimeOffset PrunedAt);

/// <param name="RawCutoff">Raw records observed before this are prunable.</param>
/// <param name="EventCutoff">Transition records observed at or after this are kept even when older than RawCutoff.</param>
/// <param name="ReplicationHardCutoff">Locally originated records observed before this are prunable even if a trusted peer has not acknowledged them.</param>
/// <param name="LocalPeerId">Owner of the replication rule; null when this peer has no identity yet.</param>
/// <param name="PrunedAt">Time recorded on pruned-range tombstones.</param>
public sealed record RawPruneRequest(
    DateTimeOffset RawCutoff,
    DateTimeOffset EventCutoff,
    DateTimeOffset ReplicationHardCutoff,
    string? LocalPeerId,
    DateTimeOffset PrunedAt);

public sealed record RawPruneResult(
    int Deleted,
    int RetainedPinned,
    int RetainedTransitions,
    int RetainedAwaitingReplication,
    int PrunedRangesWritten);
