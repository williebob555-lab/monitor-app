using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MonitorApp.Peer.Core;

public static class PeerProofRules
{
    public const int ChallengeSize = 32;

    public const int SignatureSize = 64;

    public static readonly TimeSpan MaximumChallengeLifetime = TimeSpan.FromMinutes(5);
}

public enum PeerProofPurpose
{
    Pairing = 1,
    Reconnect = 2,
}

public enum PeerProofRole
{
    Initiator = 1,
    Responder = 2,
}

public sealed record PeerProofChallenge(
    string SessionId,
    string ChallengeId,
    PeerPublicIdentity Initiator,
    PeerPublicIdentity Responder,
    PeerProtocolVersion ProtocolVersion,
    byte[] Challenge,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record PeerProof(
    PeerProofPurpose Purpose,
    PeerProofRole SignerRole,
    byte[] Signature);

public enum PeerProofVerificationStatus
{
    Trusted = 1,
    Malformed = 2,
    NotYetValid = 3,
    Expired = 4,
    WrongKeyOrTranscript = 5,
    Replayed = 6,
}

public sealed record PeerProofVerificationResult(
    PeerProofVerificationStatus Status,
    string? TrustedPeerId,
    string Reason)
{
    public bool IsTrusted => Status == PeerProofVerificationStatus.Trusted;
}

public interface IPeerChallengeReplayGuard
{
    bool TryConsume(string replayKey, DateTimeOffset expiresAt, DateTimeOffset now);
}

public sealed class InMemoryPeerChallengeReplayGuard : IPeerChallengeReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed =
        new(StringComparer.Ordinal);

    public bool TryConsume(string replayKey, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayKey);
        RequireUtc(expiresAt, nameof(expiresAt));
        RequireUtc(now, nameof(now));

        foreach (var entry in _consumed)
        {
            if (entry.Value < now)
            {
                _consumed.TryRemove(entry.Key, out _);
            }
        }

        return _consumed.TryAdd(replayKey, expiresAt);
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

public static class PeerProofService
{
    public static PeerProofChallenge CreateChallenge(
        string sessionId,
        string challengeId,
        PeerPublicIdentity initiator,
        PeerPublicIdentity responder,
        PeerProtocolVersion protocolVersion,
        DateTimeOffset createdAt,
        TimeSpan lifetime,
        byte[]? challenge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(responder);
        RequireUtc(createdAt, nameof(createdAt));
        if (lifetime <= TimeSpan.Zero || lifetime > PeerProofRules.MaximumChallengeLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Challenge lifetime must be positive and no greater than {PeerProofRules.MaximumChallengeLifetime}.");
        }

        if (protocolVersion.Major == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if (string.Equals(initiator.PeerId, responder.PeerId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Pairing requires two distinct peer identities.", nameof(responder));
        }

        var challengeBytes = challenge?.ToArray() ??
            RandomNumberGenerator.GetBytes(PeerProofRules.ChallengeSize);
        if (challengeBytes.Length != PeerProofRules.ChallengeSize)
        {
            throw new ArgumentException(
                $"Challenge must contain exactly {PeerProofRules.ChallengeSize} bytes.",
                nameof(challenge));
        }

        return new PeerProofChallenge(
            sessionId,
            challengeId,
            initiator,
            responder,
            protocolVersion,
            challengeBytes,
            createdAt,
            createdAt.Add(lifetime));
    }

    public static PeerProof CreateProof(
        IPeerIdentityKey signer,
        PeerProofChallenge challenge,
        PeerProofPurpose purpose,
        PeerProofRole signerRole)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var expectedIdentity = IdentityForRole(challenge, signerRole);
        var signerPublicKey = signer.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(signerPublicKey),
                expectedIdentity.PublicKeyFingerprint))
        {
            throw new CryptographicException("Signer does not match the selected peer role.");
        }

        return new PeerProof(
            purpose,
            signerRole,
            signer.Sign(PeerProofPayload.Create(challenge, purpose, signerRole)));
    }

    public static PeerProofVerificationResult VerifyAndConsume(
        PeerProofChallenge challenge,
        PeerProof proof,
        DateTimeOffset now,
        IPeerChallengeReplayGuard replayGuard)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(replayGuard);
        RequireUtc(now, nameof(now));

        var malformed = Validate(challenge, proof);
        if (malformed is not null)
        {
            return new PeerProofVerificationResult(
                PeerProofVerificationStatus.Malformed,
                null,
                malformed);
        }

        if (now < challenge.CreatedAt)
        {
            return new PeerProofVerificationResult(
                PeerProofVerificationStatus.NotYetValid,
                null,
                "Challenge is not yet valid.");
        }

        if (now > challenge.ExpiresAt)
        {
            return new PeerProofVerificationResult(
                PeerProofVerificationStatus.Expired,
                null,
                "Challenge has expired.");
        }

        var identity = IdentityForRole(challenge, proof.SignerRole);
        using var publicKey = PeerKeyValidation.ImportP256PublicKey(identity.PublicKeySpki);
        var proofPayload = PeerProofPayload.Create(
            challenge,
            proof.Purpose,
            proof.SignerRole);
        if (!publicKey.VerifyData(
                proofPayload,
                proof.Signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            return new PeerProofVerificationResult(
                PeerProofVerificationStatus.WrongKeyOrTranscript,
                null,
                "Peer proof does not match the expected key and transcript.");
        }

        var replayKey = Convert.ToHexString(SHA256.HashData(proofPayload));
        if (!replayGuard.TryConsume(replayKey, challenge.ExpiresAt, now))
        {
            return new PeerProofVerificationResult(
                PeerProofVerificationStatus.Replayed,
                null,
                "Peer proof challenge was already consumed.");
        }

        return new PeerProofVerificationResult(
            PeerProofVerificationStatus.Trusted,
            identity.PeerId,
            string.Empty);
    }

    private static string? Validate(PeerProofChallenge challenge, PeerProof proof)
    {
        if (!Enum.IsDefined(proof.Purpose) || !Enum.IsDefined(proof.SignerRole))
        {
            return "Proof purpose or signer role is invalid.";
        }

        if (string.IsNullOrWhiteSpace(challenge.SessionId) ||
            string.IsNullOrWhiteSpace(challenge.ChallengeId))
        {
            return "Session and challenge identifiers are required.";
        }

        if (challenge.Challenge is null ||
            proof.Signature is null ||
            challenge.Challenge.Length != PeerProofRules.ChallengeSize ||
            proof.Signature.Length != PeerProofRules.SignatureSize)
        {
            return "Challenge or signature has an invalid size.";
        }

        if (challenge.CreatedAt.Offset != TimeSpan.Zero ||
            challenge.ExpiresAt.Offset != TimeSpan.Zero ||
            challenge.ExpiresAt <= challenge.CreatedAt ||
            challenge.ExpiresAt - challenge.CreatedAt > PeerProofRules.MaximumChallengeLifetime ||
            challenge.ProtocolVersion.Major == 0)
        {
            return "Challenge timestamps or protocol version are invalid.";
        }

        return null;
    }

    private static PeerPublicIdentity IdentityForRole(
        PeerProofChallenge challenge,
        PeerProofRole role) => role switch
        {
            PeerProofRole.Initiator => challenge.Initiator,
            PeerProofRole.Responder => challenge.Responder,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

internal static class PeerProofPayload
{
    private static readonly byte[] PairingDomain = Encoding.ASCII.GetBytes("MonitorApp.PeerPairingProof.v1");
    private static readonly byte[] ReconnectDomain = Encoding.ASCII.GetBytes("MonitorApp.PeerReconnectProof.v1");

    public static byte[] Create(
        PeerProofChallenge challenge,
        PeerProofPurpose purpose,
        PeerProofRole signerRole)
    {
        var domain = purpose switch
        {
            PeerProofPurpose.Pairing => PairingDomain,
            PeerProofPurpose.Reconnect => ReconnectDomain,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

        var fields = new[]
        {
            domain,
            UInt32((uint)signerRole),
            UInt32(challenge.ProtocolVersion.Major),
            UInt32(challenge.ProtocolVersion.Minor),
            Encoding.UTF8.GetBytes(challenge.SessionId),
            Encoding.UTF8.GetBytes(challenge.ChallengeId),
            Encoding.UTF8.GetBytes(challenge.Initiator.PeerId),
            challenge.Initiator.PublicKeyFingerprint,
            Encoding.UTF8.GetBytes(challenge.Responder.PeerId),
            challenge.Responder.PublicKeyFingerprint,
            Int64(challenge.CreatedAt.ToUnixTimeMilliseconds()),
            Int64(challenge.ExpiresAt.ToUnixTimeMilliseconds()),
            challenge.Challenge,
        };
        var payload = new byte[checked(fields.Sum(field => sizeof(uint) + field.Length))];
        var offset = 0;
        foreach (var field in fields)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, sizeof(uint)), checked((uint)field.Length));
            offset += sizeof(uint);
            field.CopyTo(payload, offset);
            offset += field.Length;
        }

        return payload;
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
