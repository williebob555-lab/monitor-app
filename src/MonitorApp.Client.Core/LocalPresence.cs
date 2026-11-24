using System.Globalization;
using System.Text;

namespace MonitorApp.Client.Core;

/// <summary>Result of asking the person to prove they are present (fingerprint, face, PIN on the desktop).</summary>
public enum LocalPresenceVerification
{
    Verified,
    Cancelled,
    Unavailable,
}

/// <summary>
/// A platform "is the person here" check: Windows Hello (<c>UserConsentVerifier</c>), Linux fprintd, or nothing. It
/// proves presence to this process only; <see cref="ILocalPresenceProvider"/> turns it into a proof the node accepts.
/// </summary>
public interface ILocalUserPresence
{
    bool IsAvailable { get; }

    Task<LocalPresenceVerification> VerifyAsync(string reason, CancellationToken cancellationToken);
}

public sealed record LocalPresenceSignature(LocalPresenceVerification Outcome, byte[]? Signature);

/// <summary>
/// What the UI team calls: holds the locally enrolled presence key and signs node challenges after verifying the
/// person. The node runs as a service with no desktop session, so this runs in the interactive client process.
/// <see cref="LocalApiClient"/> uses it automatically when a high-risk action is refused with presence_required.
/// </summary>
public interface ILocalPresenceProvider
{
    /// <summary>False when no verification method is usable; enrollment and proofs are impossible.</summary>
    bool IsAvailable { get; }

    /// <summary>Short name of the method for the UI, such as "Windows Hello" or "Fingerprint (fprintd)".</summary>
    string MethodName { get; }

    /// <summary>The SubjectPublicKeyInfo of the presence key (P-256 or RSA 2048+), creating it if needed. Needs no verification.</summary>
    Task<byte[]?> GetPublicKeySpkiAsync(CancellationToken cancellationToken);

    /// <summary>Verifies the person, then signs <paramref name="payload"/>. ECDSA is IEEE P1363 (r||s); RSA is PKCS#1 v1.5. Both SHA-256.</summary>
    Task<LocalPresenceSignature> SignAsync(byte[] payload, string reason, CancellationToken cancellationToken);
}

/// <summary>Fallback when no biometric or Hello is configured; the UI shows "fingerprint not set up".</summary>
public sealed class UnavailableLocalUserPresence : ILocalUserPresence
{
    public bool IsAvailable => false;

    public Task<LocalPresenceVerification> VerifyAsync(string reason, CancellationToken cancellationToken) =>
        Task.FromResult(LocalPresenceVerification.Unavailable);
}

public sealed class UnavailableLocalPresenceProvider : ILocalPresenceProvider
{
    public bool IsAvailable => false;

    public string MethodName => "Unavailable";

    public Task<byte[]?> GetPublicKeySpkiAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public Task<LocalPresenceSignature> SignAsync(byte[] payload, string reason, CancellationToken cancellationToken) =>
        Task.FromResult(new LocalPresenceSignature(LocalPresenceVerification.Unavailable, null));
}

/// <summary>Node-reported presence configuration, for "fingerprint set up / not set up" indicators.</summary>
public sealed record LocalPresenceStatusDto(
    string Policy,
    bool Enrolled,
    bool Enforced,
    string? FingerprintSha256,
    DateTimeOffset? EnrolledAt,
    int ChallengeLifetimeSeconds);

/// <summary>The node needs a presence proof and this client has no provider, or the provider is unavailable.</summary>
public sealed class LocalPresenceRequiredException(string message) : LocalApiException(message);

/// <summary>The person cancelled or failed the verification; the action was not performed.</summary>
public sealed class LocalPresenceCancelledException(string message) : LocalApiException(message);

/// <summary>The node refused the proof (stale, replayed, wrong key) or presence is required but nothing is enrolled.</summary>
public sealed class LocalPresenceRefusedException(string message) : LocalApiException(message);

/// <summary>The byte string both sides sign; the client rebuilds it so a mismatching node response is never signed.</summary>
public static class LocalPresenceProtocol
{
    public const string ProtocolTag = "monitor-local-presence-v1";
    public const string EnrollAction = "presence.enroll";
    public const string RemoveAction = "presence.remove";

    public static byte[] BuildPayload(string challengeId, string nonce, string action, string subject, long expiresAtUnixSeconds) =>
        Encoding.UTF8.GetBytes(string.Join(
            '\n',
            ProtocolTag,
            challengeId,
            nonce,
            action,
            subject,
            expiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)));

    public static string Fingerprint(byte[] spki) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(spki));

    /// <summary>Plain-language text for the verification prompt.</summary>
    public static string Describe(string action) => action switch
    {
        "pairing.approve" => "Approve a pairing request on this computer",
        "presence-enrollment.approve" => "Approve a phone's fingerprint key",
        "control.grant" => "Allow a paired device to control this computer",
        "update.approve" => "Approve a Monitor App update install",
        "peer.revoke" => "Remove a device from all your devices",
        "network.reach" => "Change which networks can reach this computer",
        EnrollAction => "Set up fingerprint verification for Monitor App",
        RemoveAction => "Remove fingerprint verification for Monitor App",
        _ => "Confirm a Monitor App administration action",
    };
}
