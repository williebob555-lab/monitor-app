namespace MonitorApp.Client.Core;

// Client-side copies of the local API wire shapes. The client deliberately does not reference server projects:
// the HTTP contract is the boundary, so a node and a client can evolve and be diagnosed independently.

public sealed record RuntimeDto(
    string Status,
    string Scope,
    LocalPeerDto? LocalPeer,
    int TrustedPeerCount,
    int PendingPairingCount);

public sealed record LocalPeerDto(string PeerId, string FingerprintSha256);

public sealed record TrustedPeerDto(
    string PeerId,
    string? DisplayName,
    string FingerprintSha256,
    string TrustState,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt);

public sealed record PendingPairingDto(
    string PairingId,
    string RemotePeerId,
    string? DisplayName,
    string FingerprintSha256,
    string ApprovalState,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>What a trusted peer may ask this desktop to do. A peer with no stored grant is denied everything.</summary>
public sealed record ControlGrantDto(
    string PeerId,
    bool MonitoringControls,
    bool PairingApproval,
    bool UpdateApproval,
    DateTimeOffset? UpdatedAt)
{
    public bool AllowsAnything => MonitoringControls || PairingApproval || UpdateApproval;

    public static ControlGrantDto Default(string peerId) => new(peerId, false, false, false, null);
}

/// <summary>A phone's presence-key enrollment awaiting approval on this desktop. Compare <see cref="FingerprintSha256"/> with the phone.</summary>
public sealed record PresenceEnrollmentDto(
    string EnrollmentId,
    string PeerId,
    string? DisplayName,
    DateTimeOffset RequestedAt,
    string FingerprintSha256);

public sealed record ObservationValueDto(string Kind, double? Number, string? Text, bool? Boolean);

public sealed record HistoryPointDto(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string SignalId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string Source,
    string Provenance,
    string Quality,
    string Freshness,
    double AgeSeconds,
    ObservationValueDto? Value,
    string? Unit,
    string? Detail);

public sealed record HistoryPageDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit,
    string Resolution,
    int Count,
    bool Truncated,
    string? NextCursor,
    IReadOnlyList<HistoryPointDto> Points);

public sealed record StateDto(
    DateTimeOffset AsOf,
    DateTimeOffset LookbackFrom,
    int Limit,
    string Resolution,
    bool Truncated,
    IReadOnlyList<HistoryPointDto> Points);

public sealed record CatalogSignalDto(
    string SignalId,
    string? Unit,
    string Kind,
    string LatestQuality,
    DateTimeOffset LatestObservedAt);

public sealed record CatalogSubjectDto(string SubjectEntityId, IReadOnlyList<CatalogSignalDto> Signals);

public sealed record CatalogDto(IReadOnlyList<CatalogSubjectDto> Subjects, IReadOnlyList<string> OriginPeers);

public sealed record AlertDto(
    string AlertId,
    uint Revision,
    string RuleId,
    string SubjectId,
    string OriginPeerId,
    string Severity,
    string State,
    string Title,
    string Detail,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Query for a bounded history window. Paging is handled by the client.</summary>
public sealed record HistoryQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    string? SubjectEntityId = null,
    string? SignalId = null);

/// <summary>All pages fetched for a history query. <see cref="MoreAvailable"/> is true when the page cap stopped paging.</summary>
public sealed record HistoryResult(
    IReadOnlyList<HistoryPointDto> Points,
    string Resolution,
    int PagesFetched,
    bool MoreAvailable);
