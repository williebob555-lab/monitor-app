using MonitorApp.Peer.Persistence;
namespace MonitorApp.Peer.Replication;

public sealed record OriginReplicationOptions(int MaximumBatchRecords, int MaximumBatchBytes, int CursorScanLimit)
{
    public static OriginReplicationOptions Default { get; } = new(512, 4 * 1024 * 1024, 10_000);

    internal void Validate()
    {
        if (MaximumBatchRecords is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBatchRecords));
        }

        if (MaximumBatchBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBatchBytes));
        }

        if (CursorScanLimit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(CursorScanLimit));
        }
    }
}

public class OriginReplicationException : Exception
{
    public OriginReplicationException(string message) : base(message) { }
}

public sealed class OriginReplicationValidationException : OriginReplicationException
{
    public OriginReplicationValidationException(string message) : base(message) { }
}

public sealed class OriginReplicationLimitException : OriginReplicationException
{
    public OriginReplicationLimitException(string message) : base(message) { }
}

public sealed class OriginReplicationTrustException : OriginReplicationException
{
    public OriginReplicationTrustException(string message) : base(message) { }
}

/// <summary>
/// The requested origin sequence was removed by retention (compacted into rollups). This is an explicit outcome,
/// not a hole: the replica must resume at <see cref="ResumeSequence"/> and treat the range as intentionally absent.
/// </summary>
public sealed class OriginRangePrunedException : OriginReplicationException
{
    public OriginRangePrunedException(ulong requestedSequence, ulong prunedThroughSequence)
        : base($"Origin sequences through {prunedThroughSequence} were pruned by retention; resume at {prunedThroughSequence + 1}.")
    {
        RequestedSequence = requestedSequence;
        PrunedThroughSequence = prunedThroughSequence;
    }

    public OriginRangePrunedException(ulong requestedSequence, PrunedOriginRange range)
        : this(requestedSequence, (range ?? throw new ArgumentNullException(nameof(range))).LastSequence)
    {
        Range = range;
    }

    /// <summary>The exact pruned range, when known; replicas record it as intentionally absent.</summary>
    public PrunedOriginRange? Range { get; }

    public ulong RequestedSequence { get; }

    public ulong PrunedThroughSequence { get; }

    public ulong ResumeSequence => PrunedThroughSequence + 1;
}
