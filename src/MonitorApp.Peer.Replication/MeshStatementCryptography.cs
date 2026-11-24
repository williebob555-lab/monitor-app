using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Replication;

/// <summary>
/// Signs and verifies mesh membership statements (T080) over an explicit, length-prefixed canonical encoding with its
/// own domain label, so a statement signature can never be confused with an origin record, pairing or session proof.
/// </summary>
public static class MeshStatementCryptography
{
    public const int MaximumIdentifierLength = 128;
    public const int MaximumDisplayNameLength = 64;
    public const int MaximumLocators = 4;
    public const int MaximumHostLength = 255;
    public const int MaximumSpkiLength = 256;

    /// <summary>Most removals one re-admission may lift.</summary>
    public const int MaximumLiftedStatements = 32;
    private const string Domain = "monitor-mesh-statement-v1";

    public static MeshStatement CreateSigned(
        IPeerIdentityKey issuerKey,
        PeerPublicIdentity issuer,
        MeshStatementKind kind,
        PeerPublicIdentity subject,
        string? subjectDisplayName,
        IEnumerable<MeshLocator> subjectLocators,
        DateTimeOffset issuedAt,
        IEnumerable<string>? liftedStatementIds = null)
    {
        ArgumentNullException.ThrowIfNull(issuerKey);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);
        var statement = new MeshStatement
        {
            StatementId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            Kind = kind,
            IssuerPeerId = issuer.PeerId,
            IssuerPublicKeySha256 = ByteString.CopyFrom(issuer.PublicKeyFingerprint),
            SubjectPeerId = subject.PeerId,
            SubjectPublicKeySpki = ByteString.CopyFrom(subject.PublicKeySpki),
            SubjectDisplayName = Truncate(subjectDisplayName ?? string.Empty, MaximumDisplayNameLength),
            IssuedAt = Timestamp.FromDateTimeOffset(TruncateToMicroseconds(issuedAt.ToUniversalTime())),
        };
        statement.SubjectLocators.Add(subjectLocators.Take(MaximumLocators).Select(item => item.Clone()));
        statement.LiftedStatementIds.Add((liftedStatementIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var error = ValidateShape(statement);
        if (error is not null) throw new ArgumentException(error);
        statement.IssuerSignature = ByteString.CopyFrom(issuerKey.Sign(BuildSignaturePayload(statement)));
        return statement;
    }

    /// <summary>Checks everything that does not need the issuer's key. Returns a reason, or null when well formed.</summary>
    public static string? ValidateShape(MeshStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!IsIdentifier(statement.StatementId) || !IsIdentifier(statement.IssuerPeerId) || !IsIdentifier(statement.SubjectPeerId))
            return "Statement, issuer and subject ids are required and bounded.";
        if (statement.Kind is not (MeshStatementKind.Admit or MeshStatementKind.Remove or MeshStatementKind.Readmit)) return "Unknown statement kind.";
        if (statement.Kind == MeshStatementKind.Readmit)
        {
            if (statement.LiftedStatementIds.Count is 0 or > MaximumLiftedStatements) return "A re-admission names between 1 and 32 removals.";
            if (statement.LiftedStatementIds.Any(id => !IsIdentifier(id)) ||
                statement.LiftedStatementIds.Distinct(StringComparer.Ordinal).Count() != statement.LiftedStatementIds.Count)
                return "Lifted removal ids must be distinct and bounded.";
            if (statement.SubjectLocators.Count > 0) return "A re-admission carries no locators.";
        }
        else if (statement.LiftedStatementIds.Count > 0)
        {
            return "Only a re-admission lifts removals.";
        }

        if (statement.IssuerPublicKeySha256.Length != 32) return "Issuer key hash must be 32 bytes.";
        if (statement.SubjectPublicKeySpki.Length is 0 or > MaximumSpkiLength) return "Subject key is required and bounded.";
        if (statement.SubjectDisplayName.Length > MaximumDisplayNameLength) return "Display name is too long.";
        if (statement.IssuedAt is null) return "Issued time is required.";
        if (statement.SubjectLocators.Count > MaximumLocators) return "Too many locators.";
        if (statement.Kind == MeshStatementKind.Remove && statement.SubjectLocators.Count > 0) return "A removal carries no locators.";
        foreach (var locator in statement.SubjectLocators)
        {
            if (string.IsNullOrWhiteSpace(locator.Host) || locator.Host.Length > MaximumHostLength || locator.Port is < 1 or > 65535)
                return "Locator host and port are invalid.";
        }

        if (string.Equals(statement.IssuerPeerId, statement.SubjectPeerId, StringComparison.Ordinal) &&
            statement.Kind is MeshStatementKind.Admit or MeshStatementKind.Readmit)
            return "A peer cannot admit itself.";
        try
        {
            _ = Subject(statement);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return "Subject key is not a P-256 public key.";
        }

        return null;
    }

    /// <summary>The subject's identity as the statement names it (fingerprint computed from the key).</summary>
    public static PeerPublicIdentity Subject(MeshStatement statement)
    {
        var spki = statement.SubjectPublicKeySpki.ToByteArray();
        return new PeerPublicIdentity(statement.SubjectPeerId, spki, SHA256.HashData(spki));
    }

    /// <summary>Verifies the issuer signature with <paramref name="issuer"/>, which must be the identity the statement names.</summary>
    public static bool Verify(MeshStatement statement, PeerPublicIdentity issuer)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(issuer);
        if (ValidateShape(statement) is not null ||
            statement.IssuerSignature.Length != OriginRecordCryptography.P256P1363SignatureSize ||
            !string.Equals(statement.IssuerPeerId, issuer.PeerId, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(statement.IssuerPublicKeySha256.Span, issuer.PublicKeyFingerprint))
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(issuer.PublicKeySpki, out var read);
        return read == issuer.PublicKeySpki.Length && key.VerifyData(
            BuildSignaturePayload(statement),
            statement.IssuerSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal static byte[] BuildSignaturePayload(MeshStatement statement)
    {
        using var stream = new MemoryStream();
        Write(stream, Domain);
        Write(stream, statement.StatementId);
        Write(stream, (int)statement.Kind);
        Write(stream, statement.IssuerPeerId);
        Write(stream, statement.IssuerPublicKeySha256.Span);
        Write(stream, statement.SubjectPeerId);
        Write(stream, statement.SubjectPublicKeySpki.Span);
        Write(stream, statement.SubjectDisplayName);
        Write(stream, statement.SubjectLocators.Count);
        foreach (var locator in statement.SubjectLocators)
        {
            Write(stream, locator.Host);
            Write(stream, (int)locator.Port);
        }

        Write(stream, statement.IssuedAt?.Seconds ?? 0);
        Write(stream, statement.IssuedAt?.Nanos ?? 0);
        if (statement.Kind == MeshStatementKind.Readmit)
        {
            // Only re-admissions sign the lifted ids, so admit and remove signatures stay exactly as before.
            Write(stream, statement.LiftedStatementIds.Count);
            foreach (var id in statement.LiftedStatementIds)
            {
                Write(stream, id);
            }
        }

        return stream.ToArray();
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdentifierLength;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);

    private static void Write(Stream stream, string value) => Write(stream, Encoding.UTF8.GetBytes(value));

    private static void Write(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
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
