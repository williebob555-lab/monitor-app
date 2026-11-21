using Google.Protobuf;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using ContractPeerProof = MonitorApp.Contracts.V1.PeerProof;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Transport;

public interface IPeerSessionAuthenticationProvider
{
    ValueTask<PeerHello> CreateHelloAsync(string sessionId, CancellationToken cancellationToken);
    ValueTask<ContractPeerProof> VerifyServerAndCreateClientProofAsync(PeerHello sentHello, PeerHello remoteHello,
        PeerChallenge challenge, ContractPeerProof serverProof, string expectedPeerId, CancellationToken cancellationToken);
}

public sealed class P256PeerSessionAuthenticationProvider : IPeerSessionAuthenticationProvider
{
    private readonly PeerPublicIdentity localIdentity;
    private readonly PeerPublicIdentity remoteIdentity;
    private readonly IPeerIdentityKey localKey;
    private readonly IPeerChallengeReplayGuard replayGuard;

    public P256PeerSessionAuthenticationProvider(PeerPublicIdentity localIdentity, PeerPublicIdentity remoteIdentity,
        IPeerIdentityKey localKey, IPeerChallengeReplayGuard? replayGuard = null)
    {
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.remoteIdentity = remoteIdentity ?? throw new ArgumentNullException(nameof(remoteIdentity));
        this.localKey = localKey ?? throw new ArgumentNullException(nameof(localKey));
        this.replayGuard = replayGuard ?? new InMemoryPeerChallengeReplayGuard();
    }

    public ValueTask<PeerHello> CreateHelloAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PeerHello
        {
            ProtocolVersion = new ProtocolVersion
            {
                Major = PeerContractRules.ProtocolMajor,
                Minor = PeerContractRules.BilateralSessionProofMinor,
            },
            PeerId = localIdentity.PeerId,
            PublicKeySha256 = ByteString.CopyFrom(localIdentity.PublicKeyFingerprint),
            SessionId = sessionId,
        });
    }

    public ValueTask<ContractPeerProof> VerifyServerAndCreateClientProofAsync(PeerHello sentHello, PeerHello remoteHello,
        PeerChallenge challenge, ContractPeerProof serverProof, string expectedPeerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (remoteHello.PeerId != expectedPeerId || remoteHello.PeerId != remoteIdentity.PeerId ||
            !remoteHello.PublicKeySha256.Span.SequenceEqual(remoteIdentity.PublicKeyFingerprint) || remoteHello.SessionId != sentHello.SessionId ||
            challenge.SessionId != sentHello.SessionId || challenge.ChallengerPeerId != remoteIdentity.PeerId ||
            challenge.ChallengedPeerId != localIdentity.PeerId || serverProof.SessionId != challenge.SessionId ||
            serverProof.ChallengeId != challenge.ChallengeId || serverProof.SignerPeerId != remoteIdentity.PeerId ||
            serverProof.VerifierPeerId != localIdentity.PeerId || serverProof.SignatureAlgorithm != SignatureAlgorithm.EcdsaP256Sha256)
            throw new PeerAuthenticationException("Remote application identity or proof binding is invalid.");

        var localFirst = string.CompareOrdinal(localIdentity.PeerId, remoteIdentity.PeerId) < 0;
        var coreChallenge = PeerProofService.CreateChallenge(challenge.SessionId, challenge.ChallengeId,
            localFirst ? localIdentity : remoteIdentity, localFirst ? remoteIdentity : localIdentity,
            new PeerProtocolVersion(sentHello.ProtocolVersion.Major, sentHello.ProtocolVersion.Minor),
            challenge.CreatedAt.ToDateTimeOffset(), challenge.ExpiresAt.ToDateTimeOffset() - challenge.CreatedAt.ToDateTimeOffset(),
            challenge.ProofChallenge.ToByteArray());
        var serverRole = localFirst ? PeerProofRole.Responder : PeerProofRole.Initiator;
        var verified = PeerProofService.VerifyAndConsume(coreChallenge,
            new CorePeerProof(PeerProofPurpose.Reconnect, serverRole, serverProof.ProofSignature.ToByteArray()),
            DateTimeOffset.UtcNow, replayGuard);
        if (!verified.IsTrusted || verified.TrustedPeerId != remoteIdentity.PeerId)
            throw new PeerAuthenticationException("Remote application proof is invalid.");

        var localRole = localFirst ? PeerProofRole.Initiator : PeerProofRole.Responder;
        var proof = PeerProofService.CreateProof(localKey, coreChallenge, PeerProofPurpose.Reconnect, localRole);
        return ValueTask.FromResult(new ContractPeerProof
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            SignerPeerId = localIdentity.PeerId,
            VerifierPeerId = remoteIdentity.PeerId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(proof.Signature),
        });
    }
}
