using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitorApp.Peer.Application;

namespace MonitorApp.Peer.LocalApi;

/// <summary>When the node demands a desktop-local user-presence proof for high-risk local mutations.</summary>
public enum LocalPresencePolicy
{
    /// <summary>Never demanded (the pre-T045 behavior).</summary>
    Off = 0,

    /// <summary>Demanded once a presence key is enrolled; unenrolled nodes behave as before. The default.</summary>
    RequiredWhenEnrolled = 1,

    /// <summary>Always demanded; with no enrolled key the high-risk actions are refused (fail closed).</summary>
    Required = 2,
}

/// <summary>One-shot challenge handed to the desktop client; <see cref="Payload"/> is the exact byte string it signs.</summary>
public sealed record LocalPresenceChallenge(
    string ChallengeId,
    string Nonce,
    string Action,
    string Subject,
    DateTimeOffset ExpiresAt,
    byte[] Payload);

/// <summary>Public, non-secret view of the presence configuration for the UI ("fingerprint not set up").</summary>
public sealed record LocalPresenceStatus(
    string Policy,
    bool Enrolled,
    bool Enforced,
    string? FingerprintSha256,
    DateTimeOffset? EnrolledAt,
    int ChallengeLifetimeSeconds);

public enum LocalPresenceOutcome
{
    Satisfied,
    Required,
    NotEnrolled,
    Invalid,
}

/// <summary>
/// Desktop-local user-presence verification for high-risk local administration (T045). The node runs as a service
/// without an interactive desktop, so the person is verified (Windows Hello, fprintd) in the desktop client process,
/// which holds a locally enrolled presence key and signs a short-lived, single-use challenge bound to one action and
/// subject. This class holds only the enrolled public key and the pending challenges; it never sees a fingerprint.
/// </summary>
public sealed class LocalPresenceService
{
    public const string ProtocolTag = "monitor-local-presence-v1";
    public const string ChallengeHeader = "X-Presence-Challenge";
    public const string SignatureHeader = "X-Presence-Signature";
    public const string EnrollAction = "presence.enroll";
    public const string RemoveAction = "presence.remove";
    public const string PairingApproveAction = "pairing.approve";
    public const string PresenceEnrollmentApproveAction = "presence-enrollment.approve";
    public const string ControlGrantAction = "control.grant";
    public const string UpdateApproveAction = "update.approve";
    public const string NetworkReachAction = "network.reach";

    private const int MaxPendingChallenges = 64;
    private const int MaxSubjectLength = 256;

    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        EnrollAction, RemoveAction, PairingApproveAction, PresenceEnrollmentApproveAction, ControlGrantAction,
        UpdateApproveAction, NetworkReachAction,
    };

    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, LocalPresenceChallenge> challenges = new(StringComparer.Ordinal);
    private readonly string? storePath;
    private readonly IPeerClock clock;
    private byte[]? enrolledSpki;
    private DateTimeOffset? enrolledAt;

    private LocalPresenceService(string? storePath, LocalPresencePolicy policy, IPeerClock clock, TimeSpan lifetime)
    {
        this.storePath = storePath;
        Policy = policy;
        this.clock = clock;
        ChallengeLifetime = lifetime;
    }

    public LocalPresencePolicy Policy { get; }

    public TimeSpan ChallengeLifetime { get; }

    /// <summary>
    /// Opens (or starts) the presence state. A <paramref name="storePath"/> of null keeps enrollment in memory.
    /// An existing but unreadable store throws: the node must not silently fall back to "not enrolled".
    /// </summary>
    public static LocalPresenceService Create(
        string? storePath,
        LocalPresencePolicy policy,
        IPeerClock clock,
        TimeSpan? challengeLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var lifetime = challengeLifetime ?? TimeSpan.FromSeconds(60);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(challengeLifetime));
        }

        var service = new LocalPresenceService(storePath, policy, clock, lifetime);
        if (storePath is not null && File.Exists(storePath))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredEnrollment>(File.ReadAllText(storePath), JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException("empty");
                var spki = Convert.FromBase64String(stored.KeySpki);
                using var probe = PresenceKey.Import(spki);
                service.enrolledSpki = spki;
                service.enrolledAt = stored.EnrolledAt;
            }
            catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException
                or InvalidOperationException or IOException)
            {
                throw new InvalidOperationException(
                    $"The local presence store '{storePath}' is unreadable. Fix or remove it with the node stopped.", exception);
            }
        }

        return service;
    }

    public LocalPresenceStatus GetStatus()
    {
        lock (gate)
        {
            var enrolled = enrolledSpki is not null;
            return new LocalPresenceStatus(
                Policy.ToString(),
                enrolled,
                Policy == LocalPresencePolicy.Required || (Policy == LocalPresencePolicy.RequiredWhenEnrolled && enrolled),
                enrolled ? Fingerprint(enrolledSpki!) : null,
                enrolledAt,
                (int)ChallengeLifetime.TotalSeconds);
        }
    }

    public static string Fingerprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki));

    public static byte[] BuildPayload(string challengeId, string nonce, string action, string subject, DateTimeOffset expiresAt) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\n',
            ProtocolTag,
            challengeId,
            nonce,
            action,
            subject,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

    /// <summary>Issues a challenge, or null when the action is unknown, the subject is malformed, or too many are pending.</summary>
    public LocalPresenceChallenge? CreateChallenge(string? action, string? subject)
    {
        if (action is null || !KnownActions.Contains(action) || subject is null || subject.Length > MaxSubjectLength ||
            subject.Any(char.IsControl))
        {
            return null;
        }

        var now = clock.UtcNow;
        foreach (var pair in challenges)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                challenges.TryRemove(pair.Key, out _);
            }
        }

        if (challenges.Count >= MaxPendingChallenges)
        {
            return null;
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expires = DateTimeOffset.FromUnixTimeSeconds((now + ChallengeLifetime).ToUnixTimeSeconds());
        var challenge = new LocalPresenceChallenge(
            id, nonce, action, subject, expires, BuildPayload(id, nonce, action, subject, expires));
        challenges[id] = challenge;
        return challenge;
    }

    /// <summary>
    /// Checks the headers of a high-risk mutation against the policy. A challenge is consumed by its first use, valid
    /// or not, so a captured proof cannot be replayed and a guess costs a fresh challenge.
    /// </summary>
    public LocalPresenceOutcome Check(string? challengeId, string? signatureBase64, string action, string subject)
    {
        byte[]? spki;
        lock (gate)
        {
            spki = enrolledSpki;
        }

        switch (Policy)
        {
            case LocalPresencePolicy.Off:
                return LocalPresenceOutcome.Satisfied;
            case LocalPresencePolicy.RequiredWhenEnrolled when spki is null:
                return LocalPresenceOutcome.Satisfied;
            case LocalPresencePolicy.Required when spki is null:
                return LocalPresenceOutcome.NotEnrolled;
        }

        if (string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(signatureBase64))
        {
            return LocalPresenceOutcome.Required;
        }

        return VerifyProof(spki!, challengeId, signatureBase64, action, subject)
            ? LocalPresenceOutcome.Satisfied
            : LocalPresenceOutcome.Invalid;
    }

    /// <summary>Enrolls the first presence key; the new key proves possession by signing an enroll challenge for itself.</summary>
    public bool TryEnroll(string? spkiBase64, string? challengeId, string? signatureBase64)
    {
        if (!TryDecode(spkiBase64, out var spki) || spki.Length > 1024 || string.IsNullOrEmpty(challengeId) ||
            string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }

        try
        {
            using var probe = PresenceKey.Import(spki);
        }
        catch (CryptographicException)
        {
            return false;
        }

        lock (gate)
        {
            if (enrolledSpki is not null)
            {
                return false;
            }

            if (!VerifyProof(spki, challengeId, signatureBase64, EnrollAction, Fingerprint(spki)))
            {
                return false;
            }

            Persist(spki, clock.UtcNow);
            enrolledSpki = spki;
            enrolledAt = clock.UtcNow;
            return true;
        }
    }

    /// <summary>Removes the enrolled key; needs a proof from that key (break-glass without it is stopping the node and deleting the store).</summary>
    public bool TryRemove(string? challengeId, string? signatureBase64)
    {
        lock (gate)
        {
            if (enrolledSpki is null || string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(signatureBase64) ||
                !VerifyProof(enrolledSpki, challengeId, signatureBase64, RemoveAction, Fingerprint(enrolledSpki)))
            {
                return false;
            }

            if (storePath is not null && File.Exists(storePath))
            {
                File.Delete(storePath);
            }

            enrolledSpki = null;
            enrolledAt = null;
            return true;
        }
    }

    private bool VerifyProof(byte[] spki, string challengeId, string signatureBase64, string action, string subject)
    {
        if (!challenges.TryRemove(challengeId, out var challenge) ||
            challenge.ExpiresAt <= clock.UtcNow ||
            !string.Equals(challenge.Action, action, StringComparison.Ordinal) ||
            !string.Equals(challenge.Subject, subject, StringComparison.Ordinal) ||
            !TryDecode(signatureBase64, out var signature))
        {
            return false;
        }

        try
        {
            using var key = PresenceKey.Import(spki);
            return key.Verify(challenge.Payload, signature);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private void Persist(byte[] spki, DateTimeOffset at)
    {
        if (storePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(storePath))!;
        Directory.CreateDirectory(directory);
        var temporary = storePath + ".tmp";
        var json = JsonSerializer.Serialize(new StoredEnrollment(Convert.ToBase64String(spki), at), JsonSerializerOptions.Web);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }

        File.Move(temporary, storePath, overwrite: true);
    }

    private static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(base64) || base64.Length > 4096)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record StoredEnrollment(string KeySpki, DateTimeOffset EnrolledAt);

    /// <summary>ECDSA P-256 (IEEE P1363; software and fprintd-gated keys) or RSA PKCS#1 v1.5 (Windows Hello key credentials).</summary>
    private sealed class PresenceKey : IDisposable
    {
        private readonly ECDsa? ecdsa;
        private readonly RSA? rsa;

        private PresenceKey(ECDsa? ecdsa, RSA? rsa)
        {
            this.ecdsa = ecdsa;
            this.rsa = rsa;
        }

        public static PresenceKey Import(byte[] spki)
        {
            var ec = ECDsa.Create();
            try
            {
                ec.ImportSubjectPublicKeyInfo(spki, out _);
                if (ec.KeySize != 256)
                {
                    throw new CryptographicException("Only P-256 presence keys are accepted.");
                }

                return new PresenceKey(ec, null);
            }
            catch (CryptographicException)
            {
                ec.Dispose();
            }

            var rsa = RSA.Create();
            try
            {
                rsa.ImportSubjectPublicKeyInfo(spki, out _);
                if (rsa.KeySize < 2048)
                {
                    throw new CryptographicException("RSA presence keys must be at least 2048 bits.");
                }

                return new PresenceKey(null, rsa);
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }

        public bool Verify(byte[] data, byte[] signature) => ecdsa is not null
            ? ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            : rsa!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        public void Dispose()
        {
            ecdsa?.Dispose();
            rsa?.Dispose();
        }
    }
}
