using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Replication;

/// <summary>
/// Face-to-face mesh join (T085) for two devices that cannot dial each other. The codes travel only as QR codes between
/// two screens the owner holds, which is what authenticates them; the signatures bind each code to its sender's key and
/// the welcome to one join request (its nonce), so a code cannot be altered, replayed to another device, or reused.
/// </summary>
public static class MeshFaceToFace
{
    public const string JoinPrefix = "monitor-join1.";
    public const string WelcomePrefix = "monitor-welcome1.";
    public const int MaximumCodeBytes = 4096;
    // Two keep the welcome QR small enough to scan from a screen; the joiner learns the rest from the devices it reaches.
    public const int MaximumWelcomeStatements = 2;
    public static readonly TimeSpan JoinLifetime = TimeSpan.FromMinutes(10);
    private const int NonceSize = 16;

    public static bool IsMeshCode(string? text) =>
        text is not null && (text.StartsWith(JoinPrefix, StringComparison.Ordinal) || text.StartsWith(WelcomePrefix, StringComparison.Ordinal));

    public static MeshJoinRequest CreateJoinRequest(IPeerIdentityKey key, PeerPublicIdentity identity, string displayName,
        IEnumerable<MeshLocator> selfLocators, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(identity);
        var request = new MeshJoinRequest
        {
            PeerId = identity.PeerId,
            PublicKeySpki = ByteString.CopyFrom(identity.PublicKeySpki),
            DisplayName = Bounded(displayName),
            Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(NonceSize)),
            CreatedAt = Timestamp.FromDateTimeOffset(now.ToUniversalTime()),
        };
        request.SelfLocators.Add(selfLocators.Take(MeshStatementCryptography.MaximumLocators).Select(item => item.Clone()));
        request.Signature = ByteString.CopyFrom(key.Sign(Payload(request)));
        return request;
    }

    public static string Encode(MeshJoinRequest request) => JoinPrefix + Base64Url(request.ToByteArray());

    public static string Encode(MeshWelcome welcome) => WelcomePrefix + Base64Url(welcome.ToByteArray());

    /// <summary>Decodes and checks a join code: well formed, self-signed by the key it carries, and not expired.</summary>
    public static MeshJoinRequest DecodeJoin(string code, DateTimeOffset now)
    {
        var request = MeshJoinRequest.Parser.ParseFrom(Decode(code, JoinPrefix));
        if (string.IsNullOrWhiteSpace(request.PeerId) || request.PeerId.Length > MeshStatementCryptography.MaximumIdentifierLength ||
            request.DisplayName.Length > MeshStatementCryptography.MaximumDisplayNameLength || request.Nonce.Length != NonceSize ||
            request.CreatedAt is null || request.SelfLocators.Count > MeshStatementCryptography.MaximumLocators ||
            request.SelfLocators.Any(item => !IsLocator(item)))
        {
            throw new FormatException("This is not a valid join code.");
        }

        var identity = Identity(request.PeerId, request.PublicKeySpki);
        if (!Verify(identity, Payload(request), request.Signature))
        {
            throw new FormatException("This join code's signature does not match its device.");
        }

        var created = request.CreatedAt.ToDateTimeOffset();
        if (created > now + MeshMembership.MaximumClockSkew || now - created > JoinLifetime)
        {
            throw new FormatException("This join code has expired. Show a new one.");
        }

        return request;
    }

    public static MeshWelcome CreateWelcome(IPeerIdentityKey key, PeerPublicIdentity member, string displayName,
        IEnumerable<MeshLocator> memberLocators, MeshJoinRequest request, IEnumerable<MeshStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(request);
        var welcome = new MeshWelcome
        {
            MemberPeerId = member.PeerId,
            MemberPublicKeySpki = ByteString.CopyFrom(member.PublicKeySpki),
            MemberDisplayName = Bounded(displayName),
            JoinerPeerId = request.PeerId,
            JoinerNonce = request.Nonce,
        };
        welcome.MemberLocators.Add(memberLocators.Take(MeshStatementCryptography.MaximumLocators).Select(item => item.Clone()));
        welcome.Statements.Add(statements.Take(MaximumWelcomeStatements).Select(item => item.Clone()));
        welcome.Signature = ByteString.CopyFrom(key.Sign(Payload(welcome)));
        return welcome;
    }

    /// <summary>Decodes and checks a welcome: signed by the member it names, and answering exactly this device's request.</summary>
    public static MeshWelcome DecodeWelcome(string code, string joinerPeerId, ReadOnlySpan<byte> joinerNonce)
    {
        var welcome = MeshWelcome.Parser.ParseFrom(Decode(code, WelcomePrefix));
        if (string.IsNullOrWhiteSpace(welcome.MemberPeerId) || welcome.MemberPeerId.Length > MeshStatementCryptography.MaximumIdentifierLength ||
            welcome.MemberDisplayName.Length > MeshStatementCryptography.MaximumDisplayNameLength ||
            welcome.MemberLocators.Count > MeshStatementCryptography.MaximumLocators || welcome.MemberLocators.Any(item => !IsLocator(item)) ||
            welcome.Statements.Count > MaximumWelcomeStatements)
        {
            throw new FormatException("This is not a valid welcome code.");
        }

        if (welcome.JoinerPeerId != joinerPeerId || !CryptographicOperations.FixedTimeEquals(welcome.JoinerNonce.Span, joinerNonce))
        {
            throw new FormatException("This welcome code answers a different join code. Show your code again and rescan.");
        }

        var member = Identity(welcome.MemberPeerId, welcome.MemberPublicKeySpki);
        if (member.PeerId == joinerPeerId || !Verify(member, Payload(welcome), welcome.Signature))
        {
            throw new FormatException("This welcome code's signature does not match its device.");
        }

        return welcome;
    }

    public static PeerPublicIdentity JoinerIdentity(MeshJoinRequest request) => Identity(request.PeerId, request.PublicKeySpki);

    public static PeerPublicIdentity MemberIdentity(MeshWelcome welcome) => Identity(welcome.MemberPeerId, welcome.MemberPublicKeySpki);

    /// <summary>A short code both screens show, so the owner can see they are the two devices in their hands.</summary>
    public static string VerificationCode(byte[] fingerprint)
    {
        var hex = Convert.ToHexString(fingerprint, 0, 4);
        return hex[..4] + "-" + hex[4..];
    }

    private static PeerPublicIdentity Identity(string peerId, ByteString spki)
    {
        try
        {
            var bytes = spki.ToByteArray();
            return new PeerPublicIdentity(peerId, bytes, SHA256.HashData(bytes));
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new FormatException("The code carries an invalid device key.", exception);
        }
    }

    private static bool Verify(PeerPublicIdentity identity, byte[] payload, ByteString signature)
    {
        if (signature.Length != OriginRecordCryptography.P256P1363SignatureSize) return false;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(identity.PublicKeySpki, out _);
        return key.VerifyData(payload, signature.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static byte[] Decode(string code, string prefix)
    {
        var text = code?.Trim() ?? string.Empty;
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length > MaximumCodeBytes * 2)
        {
            throw new FormatException(prefix == JoinPrefix ? "This is not a join code." : "This is not a welcome code.");
        }

        try
        {
            var bytes = Convert.FromBase64String(Pad(text[prefix.Length..].Replace('-', '+').Replace('_', '/')));
            return bytes.Length <= MaximumCodeBytes ? bytes : throw new FormatException("The code is too large.");
        }
        catch (FormatException exception)
        {
            throw new FormatException("The code could not be read.", exception);
        }
    }

    private static string Pad(string value) => (value.Length % 4) switch { 2 => value + "==", 3 => value + "=", _ => value };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Bounded(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length <= MeshStatementCryptography.MaximumDisplayNameLength ? text : text[..MeshStatementCryptography.MaximumDisplayNameLength];
    }

    private static bool IsLocator(MeshLocator locator) =>
        System.Net.IPAddress.TryParse(locator.Host, out _) && locator.Port is >= 1 and <= 65535;

    private static byte[] Payload(MeshJoinRequest request)
    {
        using var stream = new MemoryStream();
        Write(stream, "monitor-mesh-join-v1");
        Write(stream, request.PeerId);
        Write(stream, request.PublicKeySpki.Span);
        Write(stream, request.DisplayName);
        Write(stream, request.Nonce.Span);
        Write(stream, request.CreatedAt?.Seconds ?? 0);
        Write(stream, request.CreatedAt?.Nanos ?? 0);
        WriteLocators(stream, request.SelfLocators);
        return stream.ToArray();
    }

    private static byte[] Payload(MeshWelcome welcome)
    {
        using var stream = new MemoryStream();
        Write(stream, "monitor-mesh-welcome-v1");
        Write(stream, welcome.MemberPeerId);
        Write(stream, welcome.MemberPublicKeySpki.Span);
        Write(stream, welcome.MemberDisplayName);
        WriteLocators(stream, welcome.MemberLocators);
        Write(stream, welcome.JoinerPeerId);
        Write(stream, welcome.JoinerNonce.Span);
        Write(stream, welcome.Statements.Count);
        foreach (var statement in welcome.Statements)
        {
            Write(stream, SHA256.HashData(statement.ToByteArray()));
        }

        return stream.ToArray();
    }

    private static void WriteLocators(Stream stream, Google.Protobuf.Collections.RepeatedField<MeshLocator> locators)
    {
        Write(stream, locators.Count);
        foreach (var locator in locators)
        {
            Write(stream, locator.Host);
            Write(stream, (int)locator.Port);
        }
    }

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
