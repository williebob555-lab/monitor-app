using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Application;

public enum PairingApprovalState
{
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,
    Completed = 4,
    Expired = 5,
}

public enum PairingOperationStatus
{
    Accepted = 1,
    ApprovalRequired = 2,
    Rejected = 3,
    Expired = 4,
    Incompatible = 5,
    Untrusted = 6,
    Revoked = 7,
    InvalidProof = 8,
    Replayed = 9,
    Completed = 10,
}

public sealed record PeerCapability(
    string ModuleId,
    string ModuleVersion,
    byte[] DeclarationSha256)
{
    public static PeerCapability FromContract(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new PeerCapability(
            descriptor.ModuleId,
            descriptor.ModuleVersion,
            descriptor.DeclarationSha256.ToByteArray());
    }

    public CapabilityDescriptor ToContract() => new()
    {
        ModuleId = ModuleId,
        ModuleVersion = ModuleVersion,
        DeclarationSha256 = ByteString.CopyFrom(DeclarationSha256),
    };
}

public sealed record PairingCandidate(
    PeerPublicIdentity Identity,
    string? DisplayName,
    PeerProofRole LocalRole,
    PeerProtocolRange SupportedProtocol,
    IReadOnlyList<PeerCapability> Capabilities);

public sealed record PairingStartResult(
    PairingOperationStatus Status,
    string? PairingId,
    PeerProtocolVersion? SelectedVersion,
    IReadOnlyList<PeerCapability> RemoteCapabilities,
    string Reason);

public sealed record PairingSnapshot(
    string PairingId,
    PeerPublicIdentity RemoteIdentity,
    string? DisplayName,
    PeerProofRole LocalRole,
    PairingApprovalState State,
    PeerProtocolVersion SelectedVersion,
    IReadOnlyList<PeerCapability> RemoteCapabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record IssuedPeerChallenge(
    string ChallengeId,
    PeerProofChallenge Challenge,
    PeerProofPurpose Purpose,
    PeerProofRole LocalRole,
    IReadOnlyList<PeerCapability> RemoteCapabilities);

public sealed record SessionAdmissionResult(
    PairingOperationStatus Status,
    string? RemotePeerId,
    PeerProtocolVersion? SelectedVersion,
    IReadOnlyList<PeerCapability> RemoteCapabilities,
    string Reason)
{
    public bool IsAuthenticated => Status is PairingOperationStatus.Completed or PairingOperationStatus.Accepted;
}

internal sealed record StoredChallenge(
    string ChallengeId,
    string? PairingId,
    string RemotePeerId,
    PeerProofPurpose Purpose,
    PeerProofRole LocalRole,
    string SessionId,
    PeerProtocolVersion ProtocolVersion,
    byte[] ChallengeBytes,
    IReadOnlyList<PeerCapability> RemoteCapabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool LocalProofCreated);
