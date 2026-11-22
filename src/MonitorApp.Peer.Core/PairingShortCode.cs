using System.Security.Cryptography;
using System.Text;

namespace MonitorApp.Peer.Core;

/// <summary>
/// Typeable one-time pairing code (10 Crockford base32 characters, 50 bits, shown as XXXXX-XXXXX).
/// The code is never transmitted: both sides derive a lookup id (sent to find the invitation) and a proof key
/// (used to authenticate the desktop identity in the reply) from it with PBKDF2, so the code alone grants no trust
/// and a listener that does not know it cannot present a forged identity.
/// </summary>
public static class PairingShortCode
{
    public const int Length = 10;
    public const int LookupIdSize = 16;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Iterations = 100_000;
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("monitor-app/pair-code/v1");
    private static readonly byte[] ProofContext = Encoding.ASCII.GetBytes("monitor-app/pair-lookup-proof/v1");

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var chars = new char[Length];
        for (var index = 0; index < Length; index++)
        {
            chars[index] = Alphabet[bytes[index] & 31];
        }

        return new string(chars);
    }

    public static string Format(string normalized) => $"{normalized[..5]}-{normalized[5..]}";

    /// <summary>Normalizes typed input (case, separators, Crockford look-alikes); null when it cannot be a code.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null || typed.Length > 32)
        {
            return null;
        }

        var builder = new StringBuilder(Length);
        foreach (var raw in typed)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return null;
            }

            builder.Append(c);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    public static PairingCodeKeys Derive(string normalizedCode)
    {
        var material = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.ASCII.GetBytes(normalizedCode), Salt, Iterations, HashAlgorithmName.SHA256, LookupIdSize + 32);
        return new PairingCodeKeys(material[..LookupIdSize], material[LookupIdSize..]);
    }

    public static byte[] ComputeProof(byte[] proofKey, byte[] lookupId, string peerId, byte[] fingerprintSha256)
    {
        using var hmac = new HMACSHA256(proofKey);
        var id = Encoding.UTF8.GetBytes(peerId);
        var message = new byte[ProofContext.Length + lookupId.Length + 4 + id.Length + fingerprintSha256.Length];
        var offset = 0;
        foreach (var part in new[] { ProofContext, lookupId, BitConverter.GetBytes(id.Length), id, fingerprintSha256 })
        {
            part.CopyTo(message, offset);
            offset += part.Length;
        }

        return hmac.ComputeHash(message);
    }
}

public sealed record PairingCodeKeys(byte[] LookupId, byte[] ProofKey);

public sealed record PairingCodeIssue(string Code, DateTimeOffset ExpiresAt);

public sealed record PairingCodeRedemption(string PeerId, byte[] FingerprintSha256, byte[] Proof);

/// <summary>
/// In-memory registry of live short codes held by the desktop. Codes are single-use and expire; redemption failures
/// (unknown, expired, already used, or rate limited) are indistinguishable to the caller. A global failure budget
/// bounds online guessing of the 50-bit space to a handful of attempts per window.
/// </summary>
public sealed class PairingShortCodeRegistry(
    TimeProvider? timeProvider = null,
    int maximumFailuresPerWindow = 8,
    TimeSpan? failureWindow = null,
    int maximumLiveCodes = 8)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan window = failureWindow ?? TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, Entry> live = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> failures = new();

    public PairingCodeIssue Issue(string peerId, byte[] fingerprintSha256, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(fingerprintSha256);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var code = PairingShortCode.Generate();
        var keys = PairingShortCode.Derive(code);
        var proof = PairingShortCode.ComputeProof(keys.ProofKey, keys.LookupId, peerId, fingerprintSha256);
        var now = clock.GetUtcNow();
        var expires = now + lifetime;
        lock (gate)
        {
            Prune(now);
            while (live.Count >= maximumLiveCodes)
            {
                live.Remove(live.MinBy(pair => pair.Value.ExpiresAt).Key);
            }

            live[Convert.ToHexString(keys.LookupId)] = new Entry(
                new PairingCodeRedemption(peerId, fingerprintSha256.ToArray(), proof), expires);
        }

        return new PairingCodeIssue(PairingShortCode.Format(code), expires);
    }

    public PairingCodeRedemption? Redeem(byte[] lookupId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            Prune(now);
            while (failures.Count > 0 && now - failures.Peek() >= window)
            {
                failures.Dequeue();
            }

            if (failures.Count >= maximumFailuresPerWindow)
            {
                return null;
            }

            if (lookupId is { Length: PairingShortCode.LookupIdSize } &&
                live.Remove(Convert.ToHexString(lookupId), out var entry))
            {
                return entry.Redemption;
            }

            failures.Enqueue(now);
            return null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in live.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            live.Remove(key);
        }
    }

    private sealed record Entry(PairingCodeRedemption Redemption, DateTimeOffset ExpiresAt);
}
