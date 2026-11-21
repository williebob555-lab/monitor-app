using System.Security.Cryptography;
using MonitorApp.Peer.Core;
using Xunit;

namespace MonitorApp.Peer.Core.Tests;

public sealed class PeerProofTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PeerProofPurpose.Pairing)]
    [InlineData(PeerProofPurpose.Reconnect)]
    [Trait("Category", "Unit")]
    public void BilateralProofTrustsBothRealKeys(PeerProofPurpose purpose)
    {
        using var initiatorKey = EcdsaPeerIdentityKey.Create();
        using var responderKey = EcdsaPeerIdentityKey.Create();
        var challenge = CreateChallenge(initiatorKey, responderKey);
        var guard = new InMemoryPeerChallengeReplayGuard();

        var initiator = PeerProofService.VerifyAndConsume(
            challenge,
            PeerProofService.CreateProof(initiatorKey, challenge, purpose, PeerProofRole.Initiator),
            Now,
            guard);
        var responder = PeerProofService.VerifyAndConsume(
            challenge,
            PeerProofService.CreateProof(responderKey, challenge, purpose, PeerProofRole.Responder),
            Now,
            guard);

        Assert.True(initiator.IsTrusted);
        Assert.Equal("peer-a", initiator.TrustedPeerId);
        Assert.True(responder.IsTrusted);
        Assert.Equal("peer-b", responder.TrustedPeerId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WrongKeyCannotCreateProofForAnotherRole()
    {
        using var initiatorKey = EcdsaPeerIdentityKey.Create();
        using var responderKey = EcdsaPeerIdentityKey.Create();
        using var attackerKey = EcdsaPeerIdentityKey.Create();
        var challenge = CreateChallenge(initiatorKey, responderKey);

        Assert.Throws<CryptographicException>(() =>
            PeerProofService.CreateProof(
                attackerKey,
                challenge,
                PeerProofPurpose.Pairing,
                PeerProofRole.Initiator));

        var validProof = PeerProofService.CreateProof(
            initiatorKey,
            challenge,
            PeerProofPurpose.Pairing,
            PeerProofRole.Initiator);
        var forgedProof = validProof with
        {
            Signature = RandomNumberGenerator.GetBytes(PeerProofRules.SignatureSize),
        };
        Assert.Equal(
            PeerProofVerificationStatus.WrongKeyOrTranscript,
            PeerProofService.VerifyAndConsume(
                challenge,
                forgedProof,
                Now,
                new InMemoryPeerChallengeReplayGuard()).Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PeerSubstitutionAndTranscriptChangeInvalidateProof()
    {
        using var initiatorKey = EcdsaPeerIdentityKey.Create();
        using var responderKey = EcdsaPeerIdentityKey.Create();
        using var substituteKey = EcdsaPeerIdentityKey.Create();
        var original = CreateChallenge(initiatorKey, responderKey);
        var proof = PeerProofService.CreateProof(
            initiatorKey,
            original,
            PeerProofPurpose.Pairing,
            PeerProofRole.Initiator);

        var substituted = original with
        {
            Responder = PeerPublicIdentity.Create("peer-c", substituteKey),
        };
        var changedSession = original with { SessionId = "session-changed" };

        Assert.Equal(
            PeerProofVerificationStatus.WrongKeyOrTranscript,
            PeerProofService.VerifyAndConsume(
                substituted,
                proof,
                Now,
                new InMemoryPeerChallengeReplayGuard()).Status);
        Assert.Equal(
            PeerProofVerificationStatus.WrongKeyOrTranscript,
            PeerProofService.VerifyAndConsume(
                changedSession,
                proof,
                Now,
                new InMemoryPeerChallengeReplayGuard()).Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidProofIsSingleUseAndExpiredProofFails()
    {
        using var initiatorKey = EcdsaPeerIdentityKey.Create();
        using var responderKey = EcdsaPeerIdentityKey.Create();
        var challenge = CreateChallenge(initiatorKey, responderKey);
        var proof = PeerProofService.CreateProof(
            initiatorKey,
            challenge,
            PeerProofPurpose.Reconnect,
            PeerProofRole.Initiator);
        var guard = new InMemoryPeerChallengeReplayGuard();

        Assert.Equal(
            PeerProofVerificationStatus.Trusted,
            PeerProofService.VerifyAndConsume(challenge, proof, Now, guard).Status);
        Assert.Equal(
            PeerProofVerificationStatus.Replayed,
            PeerProofService.VerifyAndConsume(challenge, proof, Now, guard).Status);
        Assert.Equal(
            PeerProofVerificationStatus.Expired,
            PeerProofService.VerifyAndConsume(
                challenge,
                proof,
                challenge.ExpiresAt.AddTicks(1),
                new InMemoryPeerChallengeReplayGuard()).Status);
        Assert.Equal(
            PeerProofVerificationStatus.NotYetValid,
            PeerProofService.VerifyAndConsume(
                challenge,
                proof,
                challenge.CreatedAt.AddTicks(-1),
                new InMemoryPeerChallengeReplayGuard()).Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReplayConsumptionIsBoundToTheFullPeerTranscript()
    {
        using var firstInitiatorKey = EcdsaPeerIdentityKey.Create();
        using var firstResponderKey = EcdsaPeerIdentityKey.Create();
        using var secondInitiatorKey = EcdsaPeerIdentityKey.Create();
        using var secondResponderKey = EcdsaPeerIdentityKey.Create();
        var first = CreateChallenge(firstInitiatorKey, firstResponderKey);
        var second = PeerProofService.CreateChallenge(
            first.SessionId,
            first.ChallengeId,
            PeerPublicIdentity.Create("peer-c", secondInitiatorKey),
            PeerPublicIdentity.Create("peer-d", secondResponderKey),
            first.ProtocolVersion,
            first.CreatedAt,
            first.ExpiresAt - first.CreatedAt,
            first.Challenge);
        var guard = new InMemoryPeerChallengeReplayGuard();
        var firstProof = PeerProofService.CreateProof(
            firstInitiatorKey,
            first,
            PeerProofPurpose.Pairing,
            PeerProofRole.Initiator);
        var secondProof = PeerProofService.CreateProof(
            secondInitiatorKey,
            second,
            PeerProofPurpose.Pairing,
            PeerProofRole.Initiator);

        Assert.Equal(
            PeerProofVerificationStatus.Trusted,
            PeerProofService.VerifyAndConsume(first, firstProof, Now, guard).Status);
        Assert.Equal(
            PeerProofVerificationStatus.Trusted,
            PeerProofService.VerifyAndConsume(second, secondProof, Now, guard).Status);
        Assert.Equal(
            PeerProofVerificationStatus.Replayed,
            PeerProofService.VerifyAndConsume(first, firstProof, Now, guard).Status);
    }

    private static PeerProofChallenge CreateChallenge(
        IPeerIdentityKey initiatorKey,
        IPeerIdentityKey responderKey) =>
        PeerProofService.CreateChallenge(
            "session-1",
            "challenge-1",
            PeerPublicIdentity.Create("peer-a", initiatorKey),
            PeerPublicIdentity.Create("peer-b", responderKey),
            new PeerProtocolVersion(1, 0),
            Now.AddMinutes(-1),
            TimeSpan.FromMinutes(2),
            Enumerable.Range(0, PeerProofRules.ChallengeSize).Select(value => (byte)value).ToArray());
}
