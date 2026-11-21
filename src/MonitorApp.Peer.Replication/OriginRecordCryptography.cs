using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Replication;

public static class OriginRecordCryptography
{
    public const int P256P1363SignatureSize = 64;

    public static OriginRecord CreateSigned(
        IPeerIdentityKey key,
        string originPeerId,
        string originStreamId,
        ulong sequence,
        string subjectEntityId,
        string capabilityId,
        string signalId,
        ByteString recordBytes,
        Google.Protobuf.WellKnownTypes.Timestamp observedAt,
        ObservationQuality quality)
    {
        ArgumentNullException.ThrowIfNull(key);
        var record = new OriginRecord
        {
            OriginPeerId = originPeerId,
            OriginStreamId = originStreamId,
            Sequence = sequence,
            SubjectEntityId = subjectEntityId,
            CapabilityId = capabilityId,
            SignalId = signalId,
            RecordBytes = recordBytes,
            ContentSha256 = ByteString.CopyFrom(SHA256.HashData(recordBytes.Span)),
            ObservedAt = observedAt,
            Quality = quality,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
        };
        record.OriginSignature = ByteString.CopyFrom(key.Sign(BuildSignaturePayload(record)));
        return record;
    }

    public static bool Verify(OriginRecord record, PeerPublicIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(identity);
        if (record.SignatureAlgorithm != SignatureAlgorithm.EcdsaP256Sha256 ||
            record.OriginSignature.Length != P256P1363SignatureSize)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(identity.PublicKeySpki, out var bytesRead);
        if (bytesRead != identity.PublicKeySpki.Length)
        {
            return false;
        }

        return key.VerifyData(
            BuildSignaturePayload(record),
            record.OriginSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal static byte[] BuildSignaturePayload(OriginRecord record)
    {
        using var stream = new MemoryStream();
        Write(stream, "monitor-origin-record-v1");
        Write(stream, record.OriginPeerId);
        Write(stream, record.OriginStreamId);
        Write(stream, record.Sequence);
        Write(stream, record.SubjectEntityId);
        Write(stream, record.CapabilityId);
        Write(stream, record.SignalId);
        Write(stream, record.ContentSha256.Span);
        Write(stream, record.ObservedAt?.Seconds ?? 0);
        Write(stream, record.ObservedAt?.Nanos ?? 0);
        Write(stream, (int)record.Quality);
        Write(stream, (int)record.SignatureAlgorithm);
        return stream.ToArray();
    }

    private static void Write(Stream stream, string value) => Write(stream, Encoding.UTF8.GetBytes(value));

    private static void Write(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static void Write(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Write(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Write(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
