using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorApp.Peer.Transport;

/// <summary>
/// Compact, versioned invitation a desktop peer hands to a phone (QR payload or typed/pasted text).
/// It carries only reachability and the identity the phone must pin; it grants no trust by itself.
/// Wire form: <c>monitor-pair1.</c> + base64url(UTF-8 JSON).
/// </summary>
public sealed record PeerPairingInvitation
{
    public const string Prefix = "monitor-pair1.";
    private const int MaximumEncodedLength = 1024;

    public PeerPairingInvitation(IPAddress address, int port, string peerId, byte[] fingerprintSha256, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(fingerprintSha256);
        if (fingerprintSha256.Length != 32) throw new FormatException("Invitation fingerprint must be a SHA-256 value.");
        if (peerId.Length > 128) throw new FormatException("Invitation peer id is too long.");
        if (expiresAt is { } expiry && expiry.Offset != TimeSpan.Zero) throw new FormatException("Invitation expiry must be UTC.");
        Address = address;
        Port = port;
        PeerId = peerId;
        FingerprintSha256 = fingerprintSha256.ToArray();
        ExpiresAt = expiresAt;
        // Reuse the production endpoint policy (explicit IP, private/loopback only, valid port).
        PeerTransportOptions.Create(ToEndpoint()).Validate();
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public string PeerId { get; }

    public byte[] FingerprintSha256 { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public PeerTransportEndpoint ToEndpoint() => new(
        new UriBuilder(Uri.UriSchemeHttp, Address.ToString(), Port).Uri,
        PeerId,
        IPAddress.IsLoopback(Address) ? PeerTransportSecurity.LoopbackDevelopment : PeerTransportSecurity.TailscaleEncrypted);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;

    public string Encode()
    {
        var wire = new Wire(1, Address.ToString(), Port, PeerId, Convert.ToHexString(FingerprintSha256),
            ExpiresAt?.ToUnixTimeSeconds());
        return Prefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(wire));
    }

    public static PeerPairingInvitation Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaximumEncodedLength || !trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Not a Monitor App pairing invitation, or an unsupported invitation version.");
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(FromBase64Url(trimmed[Prefix.Length..]))
                ?? throw new FormatException("Pairing invitation is empty.");
            if (wire.Version != 1) throw new FormatException("Unsupported pairing invitation version.");
            if (wire.Address is null || wire.PeerId is null || wire.Fingerprint is null ||
                !IPAddress.TryParse(wire.Address, out var address))
                throw new FormatException("Pairing invitation is missing required fields.");
            return new PeerPairingInvitation(
                address, wire.Port, wire.PeerId, Convert.FromHexString(wire.Fingerprint),
                wire.Expires is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Pairing invitation is malformed or violates the peer transport policy.", exception);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record Wire(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("a")] string? Address,
        [property: JsonPropertyName("p")] int Port,
        [property: JsonPropertyName("id")] string? PeerId,
        [property: JsonPropertyName("fp")] string? Fingerprint,
        [property: JsonPropertyName("exp")] long? Expires);
}
