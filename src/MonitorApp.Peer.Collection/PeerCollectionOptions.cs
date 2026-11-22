namespace MonitorApp.Peer.Collection;

public sealed record PeerCollectionOptions(
    string SubjectEntityId,
    string CollectorId,
    string BootId,
    TimeSpan CollectionTimeout,
    int MaximumRecordsPerRun)
{
    public static PeerCollectionOptions Default(string subjectEntityId, string bootId) => new(
        subjectEntityId,
        "monitor.system",
        bootId,
        TimeSpan.FromSeconds(30),
        256);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SubjectEntityId) || SubjectEntityId.Length > 512)
        {
            throw new ArgumentException(
                "Subject entity id is required and must not exceed 512 characters.",
                nameof(SubjectEntityId));
        }

        if (string.IsNullOrWhiteSpace(CollectorId) || CollectorId.Length > 512)
        {
            throw new ArgumentException(
                "Collector id is required and must not exceed 512 characters.",
                nameof(CollectorId));
        }

        if (string.IsNullOrWhiteSpace(BootId) || BootId.Length > 512)
        {
            throw new ArgumentException(
                "Boot id is required and must not exceed 512 characters.",
                nameof(BootId));
        }

        if (CollectionTimeout <= TimeSpan.Zero || CollectionTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(CollectionTimeout));
        }

        if (MaximumRecordsPerRun is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRecordsPerRun));
        }
    }
}

public enum PeerCollectionRunStatus
{
    Completed = 1,
    AlreadyRunning = 2,
}

public sealed record PeerCollectionRunResult(
    PeerCollectionRunStatus Status,
    int AppendedRecords,
    ulong? FirstSequence,
    ulong? LastSequence,
    DateTimeOffset? ObservedAt);
