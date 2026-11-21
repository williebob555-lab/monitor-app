using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MonitorApp.Contracts;

public static class EnrollmentContractRules
{
    public const int EnrollmentSecretSize = 32;

    public const int ProofChallengeSize = 32;

    public const int EcdsaP256SignatureSize = 64;

    public const int PublicKeySha256Size = 32;
}

public static class ProofPayload
{
    private static readonly byte[] EnrollmentDomain =
        Encoding.ASCII.GetBytes("MonitorApp.EnrollmentProof.v1");

    private static readonly byte[] DeviceDomain =
        Encoding.ASCII.GetBytes("MonitorApp.DeviceProof.v1");

    private static readonly byte[] AgentSessionDomain =
        Encoding.ASCII.GetBytes("MonitorApp.AgentSessionProof.v1");

    private static readonly byte[] PeerSessionDomain =
        Encoding.ASCII.GetBytes("MonitorApp.PeerSessionProof.v1");

    public static byte[] CreateEnrollment(
        string enrollmentId,
        ReadOnlySpan<byte> proofChallenge,
        ReadOnlySpan<byte> publicKeySpki)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentId);
        RequireLength(
            proofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            nameof(proofChallenge));
        if (publicKeySpki.IsEmpty)
        {
            throw new ArgumentException("Public key is required.", nameof(publicKeySpki));
        }

        var publicKeySha256 = SHA256.HashData(publicKeySpki);
        return CreateFramedPayload(
            EnrollmentDomain,
            Encoding.UTF8.GetBytes(enrollmentId),
            proofChallenge,
            publicKeySha256);
    }

    public static byte[] CreateDevice(
        string agentId,
        string challengeId,
        ReadOnlySpan<byte> proofChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        RequireLength(
            proofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            nameof(proofChallenge));

        return CreateFramedPayload(
            DeviceDomain,
            Encoding.UTF8.GetBytes(agentId),
            Encoding.UTF8.GetBytes(challengeId),
            proofChallenge);
    }

    public static byte[] CreateAgentSession(
        string agentId,
        string sessionId,
        string challengeId,
        ReadOnlySpan<byte> proofChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        RequireLength(
            proofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            nameof(proofChallenge));

        var agent = Encoding.UTF8.GetBytes(agentId);
        var session = Encoding.UTF8.GetBytes(sessionId);
        var challenge = Encoding.UTF8.GetBytes(challengeId);
        var length = checked(
            (5 * sizeof(uint)) +
            AgentSessionDomain.Length +
            agent.Length +
            session.Length +
            challenge.Length +
            proofChallenge.Length);
        var payload = new byte[length];
        var offset = 0;
        WriteField(payload, ref offset, AgentSessionDomain);
        WriteField(payload, ref offset, agent);
        WriteField(payload, ref offset, session);
        WriteField(payload, ref offset, challenge);
        WriteField(payload, ref offset, proofChallenge);
        return payload;
    }

    public static byte[] CreatePeerSession(
        string sessionId,
        string challengeId,
        string signerPeerId,
        string verifierPeerId,
        ReadOnlySpan<byte> proofChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signerPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifierPeerId);
        RequireLength(
            proofChallenge,
            EnrollmentContractRules.ProofChallengeSize,
            nameof(proofChallenge));

        var session = Encoding.UTF8.GetBytes(sessionId);
        var challenge = Encoding.UTF8.GetBytes(challengeId);
        var signer = Encoding.UTF8.GetBytes(signerPeerId);
        var verifier = Encoding.UTF8.GetBytes(verifierPeerId);
        var length = checked(
            (6 * sizeof(uint)) +
            PeerSessionDomain.Length +
            session.Length +
            challenge.Length +
            signer.Length +
            verifier.Length +
            proofChallenge.Length);
        var payload = new byte[length];
        var offset = 0;
        WriteField(payload, ref offset, PeerSessionDomain);
        WriteField(payload, ref offset, session);
        WriteField(payload, ref offset, challenge);
        WriteField(payload, ref offset, signer);
        WriteField(payload, ref offset, verifier);
        WriteField(payload, ref offset, proofChallenge);
        return payload;
    }

    private static byte[] CreateFramedPayload(
        ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> third)
    {
        var length = checked(
            (4 * sizeof(uint)) +
            domain.Length +
            first.Length +
            second.Length +
            third.Length);
        var payload = new byte[length];
        var offset = 0;
        WriteField(payload, ref offset, domain);
        WriteField(payload, ref offset, first);
        WriteField(payload, ref offset, second);
        WriteField(payload, ref offset, third);
        return payload;
    }

    private static void WriteField(
        Span<byte> destination,
        ref int offset,
        ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(offset, sizeof(uint)),
            checked((uint)value.Length));
        offset += sizeof(uint);
        value.CopyTo(destination[offset..]);
        offset += value.Length;
    }

    private static void RequireLength(
        ReadOnlySpan<byte> value,
        int expectedLength,
        string parameterName)
    {
        if (value.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Value must contain exactly {expectedLength} bytes.",
                parameterName);
        }
    }
}
