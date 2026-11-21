namespace MonitorApp.Peer.Persistence;

public enum PeerTrustState
{
    Active = 1,
    Revoked = 2,
}

public enum RecordProvenance
{
    LocalOrigin = 1,
    Replicated = 2,
}

public enum IdentityStrength
{
    Weak = 1,
    Strong = 2,
}

public enum RelationshipEvidenceKind
{
    Reported = 1,
    Configured = 2,
    Inferred = 3,
    OverlaySession = 4,
}

public enum RelationshipKind
{
    AttachedTo = 1,
    CollectedBy = 2,
    ConnectedToPort = 3,
    MemberOfVlan = 4,
    UplinkTo = 5,
    ReachableOverOverlay = 6,
}

public enum RecordInsertOutcome
{
    Inserted = 1,
    IdenticalRetry = 2,
}

public sealed record LocalPeerIdentity(
    string PeerId,
    byte[] PublicKeySpki,
    byte[] PublicKeyFingerprint,
    string KeyProviderReference,
    string OriginStreamId,
    ulong NextSequence,
    DateTimeOffset CreatedAt);

public sealed record TrustedPeer(
    string PeerId,
    byte[] PublicKeySpki,
    byte[] PublicKeyFingerprint,
    string? DisplayName,
    PeerTrustState TrustState,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt);

public sealed record PeerLocator(
    string PeerId,
    string Host,
    int Port,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastConnectedAt);

public sealed record PairingSession(
    string PairingSessionId,
    string RemotePeerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Completed);

public sealed record OriginRecordWrite(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string AdapterInstanceId,
    string SignalId,
    byte[] RecordBytes,
    byte[] Signature,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string? ReceivedFromPeerId,
    RecordProvenance Provenance,
    string Quality);

public sealed record StoredOriginRecord(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string AdapterInstanceId,
    string SignalId,
    byte[] RecordBytes,
    byte[] ContentHash,
    byte[] Signature,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string? ReceivedFromPeerId,
    RecordProvenance Provenance,
    string Quality);

public sealed record CurrentObservation(
    string SubjectEntityId,
    string SignalId,
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    byte[] RecordBytes,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    RecordProvenance Provenance,
    string Quality);

public sealed record ObservedEntity(
    string EntityId,
    string EntityKind,
    DateTimeOffset CreatedAt);

public sealed record IdentityClaim(
    string EntityId,
    string ClaimType,
    string NormalizedValue,
    IdentityStrength Strength,
    string ObserverPeerId,
    RelationshipEvidenceKind EvidenceKind,
    double Confidence,
    DateTimeOffset ObservedAt);

public sealed record RelationshipObservation(
    string RelationshipId,
    string SourceEntityId,
    string TargetEntityId,
    RelationshipKind Kind,
    RelationshipEvidenceKind EvidenceKind,
    string ObserverPeerId,
    double Confidence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

public sealed record AdapterInstance(
    string AdapterInstanceId,
    string AdapterId,
    string AdapterVersion,
    string CollectorPeerId,
    string? ObservedEntityId,
    string? CredentialReference,
    bool Enabled);

public sealed record AdapterReplicationDescriptor(
    string AdapterInstanceId,
    string AdapterId,
    string AdapterVersion,
    string CollectorPeerId,
    string? ObservedEntityId,
    bool Enabled);

public sealed record AdapterStatus(
    string AdapterInstanceId,
    string State,
    string? Reason,
    DateTimeOffset ObservedAt);

public sealed record ReplicationCursor(
    string RemotePeerId,
    string OriginPeerId,
    string OriginStreamId,
    ulong AcceptedThrough,
    string Fidelity,
    DateTimeOffset UpdatedAt);

public sealed record ReplicationGap(ulong FirstSequence, ulong LastSequence);

public sealed record CapabilityCatalogEntry(
    string CapabilityId,
    string Namespace,
    string Version,
    byte[] Descriptor,
    DateTimeOffset UpdatedAt);

public sealed record CollectorAssignment(
    string ObservedEntityId,
    string AdapterInstanceId,
    string CollectorPeerId,
    DateTimeOffset AssignedAt);
