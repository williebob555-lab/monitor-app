using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using Xunit;

namespace MonitorApp.Contracts.Tests;

public sealed class AgentSessionAuthenticationTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void StreamProofRoundTripsAndBindsEveryIdentityField()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = RandomNumberGenerator.GetBytes(EnrollmentContractRules.ProofChallengeSize);
        var payload = ProofPayload.CreateAgentSession("agent-1", "session-1", "challenge-1", challenge);
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var proof = new AgentSessionProof
        {
            AgentId = "agent-1",
            SessionId = "session-1",
            ChallengeId = "challenge-1",
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(signature),
        };

        var parsed = AgentSessionProof.Parser.ParseFrom(proof.ToByteArray());
        Assert.True(ContractValidator.Validate(parsed).IsValid);
        Assert.True(key.VerifyData(
            ProofPayload.CreateAgentSession(
                parsed.AgentId,
                parsed.SessionId,
                parsed.ChallengeId,
                challenge),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(key.VerifyData(
            ProofPayload.CreateAgentSession("agent-1", "other-session", "challenge-1", challenge),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ChallengeRequiresExactSizesAlgorithmAndTimeWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var challenge = new AgentSessionChallenge
        {
            AgentId = "agent-1",
            SessionId = "session-1",
            ChallengeId = "challenge-1",
            ProofChallenge = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(now),
            ExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(2)),
        };
        Assert.True(ContractValidator.Validate(challenge).IsValid);

        challenge.ProofChallenge = ByteString.CopyFrom(new byte[31]);
        challenge.SignatureAlgorithm = SignatureAlgorithm.Unspecified;
        challenge.ExpiresAt = challenge.CreatedAt;
        var invalid = ContractValidator.Validate(challenge);
        Assert.False(invalid.IsValid);
        Assert.DoesNotContain(Convert.ToBase64String(challenge.ProofChallenge.ToByteArray()), string.Join(' ', invalid.Errors), StringComparison.Ordinal);
    }
}
