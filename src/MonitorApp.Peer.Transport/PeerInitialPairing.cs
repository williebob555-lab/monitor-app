using Google.Protobuf;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using ContractPeerProof = MonitorApp.Contracts.V1.PeerProof;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Transport;

public sealed record PeerInitialPairingRequest(
    string PairingId,
    bool InitiatingUserApproved,
    byte[]? ExpectedRemotePublicKeySha256 = null);

public sealed record PeerInitialPairingOptions(int MaximumApprovalPolls, TimeSpan InitialPollDelay, TimeSpan MaximumPollDelay)
{
    public static PeerInitialPairingOptions Default { get; } = new(30, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    internal void Validate()
    {
        if (MaximumApprovalPolls is < 1 or > 10_000 || InitialPollDelay <= TimeSpan.Zero || MaximumPollDelay < InitialPollDelay)
            throw new ArgumentOutOfRangeException(nameof(PeerInitialPairingOptions));
    }
}

public sealed record PairingRemoteCandidate(PeerPublicIdentity Identity, string DisplayName);

public interface IRemotePairingApproval
{
    ValueTask<bool> ApproveAsync(PairingRemoteCandidate candidate, CancellationToken cancellationToken);
}

public interface IPeerTrustSink
{
    ValueTask PersistTrustedPeerAsync(PairingRemoteCandidate candidate, DateTimeOffset pairedAt, CancellationToken cancellationToken);
}

public interface IPeerInitialPairingCoordinator
{
    PairingHello CreateLocalHello(string pairingId);
    ValueTask<PairingRemoteCandidate> ApproveAndPinRemoteAsync(PairingHello remoteHello, string expectedPeerId,
        byte[]? expectedFingerprint, CancellationToken cancellationToken);
    ValueTask<ContractPeerProof> VerifyRemoteAndCreateLocalProofAsync(PairingHello localHello, PairingHello remoteHello,
        PeerChallenge challenge, ContractPeerProof remoteProof, CancellationToken cancellationToken);
    ValueTask PersistTrustAsync(PairingRemoteCandidate candidate, PairingConfirmation confirmation, CancellationToken cancellationToken);
}

public sealed class P256PeerInitialPairingCoordinator : IPeerInitialPairingCoordinator
{
    private readonly PeerPublicIdentity localIdentity;
    private readonly IPeerIdentityKey localKey;
    private readonly string displayName;
    private readonly IRemotePairingApproval approval;
    private readonly IPeerTrustSink trustSink;
    private readonly IPeerChallengeReplayGuard replayGuard;

    public P256PeerInitialPairingCoordinator(PeerPublicIdentity localIdentity, IPeerIdentityKey localKey, string displayName,
        IRemotePairingApproval approval, IPeerTrustSink trustSink, IPeerChallengeReplayGuard? replayGuard = null)
    {
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.localKey = localKey ?? throw new ArgumentNullException(nameof(localKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        this.displayName = displayName;
        this.approval = approval ?? throw new ArgumentNullException(nameof(approval));
        this.trustSink = trustSink ?? throw new ArgumentNullException(nameof(trustSink));
        this.replayGuard = replayGuard ?? new InMemoryPeerChallengeReplayGuard();
    }

    public PairingHello CreateLocalHello(string pairingId) => new()
    {
        ProtocolVersion = new ProtocolVersion { Major = PeerContractRules.ProtocolMajor, Minor = PeerContractRules.BilateralSessionProofMinor },
        PairingId = pairingId,
        PeerId = localIdentity.PeerId,
        DisplayName = displayName,
        PublicKeySpki = ByteString.CopyFrom(localIdentity.PublicKeySpki),
        PublicKeySha256 = ByteString.CopyFrom(localIdentity.PublicKeyFingerprint),
    };

    public async ValueTask<PairingRemoteCandidate> ApproveAndPinRemoteAsync(PairingHello remoteHello, string expectedPeerId,
        byte[]? expectedFingerprint, CancellationToken cancellationToken)
    {
        if (!PeerContractValidator.Validate(remoteHello).IsValid || remoteHello.PeerId != expectedPeerId ||
            expectedFingerprint is not null && !remoteHello.PublicKeySha256.Span.SequenceEqual(expectedFingerprint))
            throw new PeerAuthenticationException("Remote pairing identity does not match the expected identity.");
        PeerPublicIdentity identity;
        try
        {
            identity = new PeerPublicIdentity(remoteHello.PeerId, remoteHello.PublicKeySpki.ToByteArray(), remoteHello.PublicKeySha256.ToByteArray());
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            throw new PeerAuthenticationException("Remote pairing identity key is invalid.");
        }

        var candidate = new PairingRemoteCandidate(identity, remoteHello.DisplayName);
        if (!await approval.ApproveAsync(candidate, cancellationToken).ConfigureAwait(false))
            throw new PeerAuthenticationException("Local user rejected the remote pairing identity.");
        return candidate;
    }

    public ValueTask<ContractPeerProof> VerifyRemoteAndCreateLocalProofAsync(PairingHello localHello, PairingHello remoteHello,
        PeerChallenge challenge, ContractPeerProof remoteProof, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remote = new PeerPublicIdentity(remoteHello.PeerId, remoteHello.PublicKeySpki.ToByteArray(), remoteHello.PublicKeySha256.ToByteArray());
        if (challenge.ChallengerPeerId != remote.PeerId || challenge.ChallengedPeerId != localIdentity.PeerId)
            throw new PeerAuthenticationException("Remote pairing challenge identity is invalid.");
        var coreChallenge = PeerProofService.CreateChallenge(challenge.SessionId, challenge.ChallengeId,
            localIdentity, remote,
            new PeerProtocolVersion(localHello.ProtocolVersion.Major, localHello.ProtocolVersion.Minor),
            challenge.CreatedAt.ToDateTimeOffset(), challenge.ExpiresAt.ToDateTimeOffset() - challenge.CreatedAt.ToDateTimeOffset(),
            challenge.ProofChallenge.ToByteArray());
        const PeerProofRole remoteRole = PeerProofRole.Responder;
        if (remoteProof.SessionId != challenge.SessionId || remoteProof.ChallengeId != challenge.ChallengeId ||
            remoteProof.SignerPeerId != remote.PeerId || remoteProof.VerifierPeerId != localIdentity.PeerId ||
            remoteProof.SignatureAlgorithm != SignatureAlgorithm.EcdsaP256Sha256)
            throw new PeerAuthenticationException("Remote pairing proof binding is invalid.");
        var verified = PeerProofService.VerifyAndConsume(coreChallenge,
            new CorePeerProof(PeerProofPurpose.Pairing, remoteRole, remoteProof.ProofSignature.ToByteArray()),
            DateTimeOffset.UtcNow, replayGuard);
        if (!verified.IsTrusted || verified.TrustedPeerId != remote.PeerId)
            throw new PeerAuthenticationException("Remote pairing proof is invalid, expired, or replayed.");
        var localProof = PeerProofService.CreateProof(localKey, coreChallenge, PeerProofPurpose.Pairing, PeerProofRole.Initiator);
        return ValueTask.FromResult(new ContractPeerProof
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            SignerPeerId = localIdentity.PeerId,
            VerifierPeerId = remote.PeerId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(localProof.Signature),
        });
    }

    public ValueTask PersistTrustAsync(PairingRemoteCandidate candidate, PairingConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (confirmation.LocalPeerId != candidate.Identity.PeerId || confirmation.RemotePeerId != localIdentity.PeerId ||
            !confirmation.RemotePublicKeySha256.Span.SequenceEqual(localIdentity.PublicKeyFingerprint))
            throw new PeerAuthenticationException("Pairing confirmation identity is invalid.");
        return trustSink.PersistTrustedPeerAsync(candidate, confirmation.PairedAt.ToDateTimeOffset(), cancellationToken);
    }
}
