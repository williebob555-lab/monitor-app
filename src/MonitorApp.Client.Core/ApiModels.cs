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
