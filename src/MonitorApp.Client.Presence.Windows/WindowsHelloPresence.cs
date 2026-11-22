using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MonitorApp.Client.Core;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace MonitorApp.Client.Presence.Windows;

/// <summary>
/// Windows Hello user-presence check (fingerprint, face, or PIN) through <c>UserConsentVerifier</c>. It only proves
/// presence to this process; use <see cref="WindowsHelloPresenceProvider"/> for a hardware-backed key the node accepts.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsHelloUserPresence : ILocalUserPresence
{
    public bool IsAvailable => CheckAvailability() == UserConsentVerifierAvailability.Available;

    public async Task<LocalPresenceVerification> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (CheckAvailability() != UserConsentVerifierAvailability.Available)
        {
            return LocalPresenceVerification.Unavailable;
        }

        var result = await UserConsentVerifier.RequestVerificationAsync(reason).AsTask(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            UserConsentVerificationResult.Verified => LocalPresenceVerification.Verified,
            UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser or
                UserConsentVerificationResult.DisabledByPolicy => LocalPresenceVerification.Unavailable,
            _ => LocalPresenceVerification.Cancelled,
        };
    }

    private static UserConsentVerifierAvailability CheckAvailability()
    {
        try
        {
            return UserConsentVerifier.CheckAvailabilityAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return UserConsentVerifierAvailability.DeviceNotPresent;
        }
    }
}

/// <summary>
/// Hardware-backed (TPM when present) Windows Hello key credential. The private key never leaves the platform and every
/// signature shows the Windows Hello prompt itself. The key is RSA, signed with PKCS#1 v1.5 and SHA-256, which the node
/// accepts alongside ECDSA P-256.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsHelloPresenceProvider : ILocalPresenceProvider
{
    private const string CredentialName = "MonitorApp.LocalPresence";

    public bool IsAvailable => IsSupported();

    public string MethodName => "Windows Hello";

    public async Task<byte[]?> GetPublicKeySpkiAsync(CancellationToken cancellationToken)
    {
        var credential = await OpenOrCreateAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return null;
        }

        CryptographicBuffer.CopyToByteArray(credential.RetrievePublicKey(CryptographicPublicKeyBlobType.X509SubjectPublicKeyInfo), out var spki);
        return spki;
    }

    public async Task<LocalPresenceSignature> SignAsync(byte[] payload, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var credential = await OpenOrCreateAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return new LocalPresenceSignature(LocalPresenceVerification.Unavailable, null);
        }

        // RequestSignAsync shows the Windows Hello prompt itself; the platform dialog supplies the wording.
        var signed = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(payload))
            .AsTask(cancellationToken).ConfigureAwait(false);
        switch (signed.Status)
        {
            case KeyCredentialStatus.Success:
                CryptographicBuffer.CopyToByteArray(signed.Result, out var signature);
                return new LocalPresenceSignature(LocalPresenceVerification.Verified, signature);
            case KeyCredentialStatus.UserCanceled:
            case KeyCredentialStatus.UserPrefersPassword:
                return new LocalPresenceSignature(LocalPresenceVerification.Cancelled, null);
            default:
                return new LocalPresenceSignature(LocalPresenceVerification.Unavailable, null);
        }
    }

    private static bool IsSupported()
    {
        try
        {
            return KeyCredentialManager.IsSupportedAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static async Task<KeyCredential?> OpenOrCreateAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported())
        {
            return null;
        }

        var opened = await KeyCredentialManager.OpenAsync(CredentialName).AsTask(cancellationToken).ConfigureAwait(false);
        if (opened.Status == KeyCredentialStatus.Success)
        {
            return opened.Credential;
        }

        var created = await KeyCredentialManager
            .RequestCreateAsync(CredentialName, KeyCredentialCreationOption.FailIfExists)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return created.Status == KeyCredentialStatus.Success ? created.Credential : null;
    }
}
