namespace MonitorApp.Peer.LocalApi;

public sealed record LocalRuntimeView(
    string Status,
    string Scope,
    LocalPeerView? LocalPeer,
    int TrustedPeerCount,
    int PendingPairingCount);

public sealed record LocalPeerView(string PeerId, string FingerprintSha256);

public sealed record TrustedPeerView(
    string PeerId,
    string? DisplayName,
    string FingerprintSha256,
    string TrustState,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt,
    string? Provenance = null);

public sealed record PendingPairingView(
    string PairingId,
    string RemotePeerId,
    string? DisplayName,
    string FingerprintSha256,
    string ApprovalState,
    uint ProtocolMajor,
    uint ProtocolMinor,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record OriginObservationView(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string Source,
    string SignalId,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceivedTime,
    string? ReceivedFromPeerId,
    string Provenance,
    string Quality,
    string ContentSha256,
    int PayloadSizeBytes);
