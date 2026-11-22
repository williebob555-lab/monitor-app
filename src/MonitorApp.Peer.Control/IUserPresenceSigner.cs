namespace MonitorApp.Peer.Control;

/// <summary>What the person is asked to confirm; shown by the platform's biometric prompt.</summary>
public sealed record UserPresenceChallenge(string Title, string Description);

/// <summary>The person declined, cancelled, or failed the biometric check; no signature was produced.</summary>
public sealed class UserPresenceDeniedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A device-bound signing key whose every use requires a fresh user verification (fingerprint on Android through
/// BiometricPrompt and a Keystore key with per-use authentication; Windows Hello or fprintd on laptops later). The key
/// is separate from the peer identity key: the identity key authenticates the device, this key attests that the person
/// was present for one specific high-risk command.
/// </summary>
public interface IUserPresenceSigner
{
    /// <summary>False when the device has no usable biometric (high-risk commands cannot be sent from it).</summary>
    bool IsAvailable { get; }

    /// <summary>The P-256 SubjectPublicKeyInfo of the presence key, creating the key if needed. Needs no verification.</summary>
    ValueTask<byte[]> GetPublicKeySpkiAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the person, then signs <paramref name="data"/> with ECDSA P-256/SHA-256 in IEEE P1363 (r||s) format.
    /// Throws <see cref="UserPresenceDeniedException"/> when verification fails or is cancelled.
    /// </summary>
    ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data,
        UserPresenceChallenge challenge,
        CancellationToken cancellationToken);
}
