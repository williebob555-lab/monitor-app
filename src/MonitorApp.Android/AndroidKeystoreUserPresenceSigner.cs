using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Security.Keystore;
using Java.Security;
using Java.Security.Spec;
using MonitorApp.Android.Core;
using MonitorApp.Peer.Control;

namespace MonitorApp.Android;

/// <summary>
/// The phone's biometric user-presence key: a P-256 key in Android Keystore that needs a fresh strong-biometric
/// verification for every single signature (per-use authentication, no validity window), plus the framework
/// BiometricPrompt bound to that signature through a CryptoObject. The key is separate from the peer identity key and
/// is invalidated by the platform when a new fingerprint is enrolled, which forces a new desktop-approved enrollment.
/// Uses only framework APIs (no AndroidX package): BiometricPrompt needs API 28, strong-only authenticators API 30.
/// </summary>
public sealed class AndroidKeystoreUserPresenceSigner : IUserPresenceSigner
{
    private const string AndroidKeyStore = "AndroidKeyStore";
    private const int BiometricStrongAuthenticators = 0x0000000F;
    private const int BiometricSuccess = 0;
    private readonly Context context;
    private readonly string alias;

    public AndroidKeystoreUserPresenceSigner(Context context, string alias)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        this.context = context.ApplicationContext ?? context;
        this.alias = alias;
    }

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                return false;
            }

            using var manager = context.GetSystemService(Context.BiometricService) as BiometricManager;
            if (manager is null)
            {
                return false;
            }

            return OperatingSystem.IsAndroidVersionAtLeast(30)
                ? manager.CanAuthenticate(BiometricStrongAuthenticators) == BiometricSuccess
                : manager.CanAuthenticate() == BiometricSuccess;
        }
    }

    public ValueTask<byte[]> GetPublicKeySpkiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new UserPresenceDeniedException("Biometric verification requires Android 9 or newer.");
        }

        EnsureKeyExists();
        using var store = LoadStore();
        using var certificate = store.GetCertificate(alias)
            ?? throw new InvalidOperationException("Android Keystore presence certificate is unavailable.");
        using var publicKey = certificate.PublicKey
            ?? throw new InvalidOperationException("Android Keystore presence public key is unavailable.");
        return ValueTask.FromResult(publicKey.GetEncoded()
            ?? throw new InvalidOperationException("Android Keystore did not export the presence public key."));
    }

    public async ValueTask<byte[]> SignAsync(
        ReadOnlyMemory<byte> data,
        UserPresenceChallenge challenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (!OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            throw new UserPresenceDeniedException("Biometric verification requires Android 9 or newer.");
        }

        EnsureKeyExists();
        using var signature = Signature.GetInstance("SHA256withECDSA")
            ?? throw new InvalidOperationException("Android ECDSA signer is unavailable.");
        try
        {
            using var store = LoadStore();
            using var entry = store.GetEntry(alias, null) as KeyStore.PrivateKeyEntry
                ?? throw new InvalidOperationException("Android Keystore presence key is unavailable.");
            signature.InitSign(entry.PrivateKey);
        }
        catch (KeyPermanentlyInvalidatedException exception)
        {
            // A fingerprint was added or removed: the platform destroyed the key's usability. Start over.
            DeleteKey();
            throw new UserPresenceDeniedException("The biometric key was invalidated; enroll this phone again.", exception);
        }

        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationSignal();
        using var registration = cancellationToken.Register(cancel.Cancel);
        using var builder = new BiometricPrompt.Builder(context);
        builder.SetTitle(challenge.Title);
        builder.SetDescription(challenge.Description);
        using var executor = context.MainExecutor
            ?? throw new InvalidOperationException("Android main executor is unavailable.");
        builder.SetNegativeButton("Cancel", executor, new CancelListener(completion));
        using var prompt = builder.Build();
        using var crypto = new BiometricPrompt.CryptoObject(signature);
        var payload = data.ToArray();
        prompt.Authenticate(crypto, cancel, executor, new Callback(payload, completion));
        return await completion.Task.ConfigureAwait(false);
    }

    private void EnsureKeyExists()
    {
        using var store = LoadStore();
        if (store.ContainsAlias(alias))
        {
            return;
        }

        using var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, AndroidKeyStore)
            ?? throw new InvalidOperationException("Android Keystore EC generator is unavailable.");
        using var parameterBuilder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Sign | KeyStorePurpose.Verify);
        _ = parameterBuilder.SetAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"));
        _ = parameterBuilder.SetDigests(KeyProperties.DigestSha256);
        _ = parameterBuilder.SetUserAuthenticationRequired(true);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Timeout 0: every signature needs its own strong-biometric verification through BiometricPrompt.
            _ = parameterBuilder.SetUserAuthenticationParameters(0, (int)KeyPropertiesAuthType.BiometricStrong);
        }

        _ = parameterBuilder.SetInvalidatedByBiometricEnrollment(true);
        using var parameterSpec = parameterBuilder.Build()
            ?? throw new InvalidOperationException("Android Keystore key parameters are unavailable.");
        generator.Initialize(parameterSpec);
        using var pair = generator.GenerateKeyPair();
        if (pair is null)
        {
            throw new InvalidOperationException("Android Keystore did not generate a presence key.");
        }
    }

    private void DeleteKey()
    {
        using var store = LoadStore();
        if (store.ContainsAlias(alias))
        {
            store.DeleteEntry(alias);
        }
    }

    private static KeyStore LoadStore()
    {
        var store = KeyStore.GetInstance(AndroidKeyStore)
            ?? throw new InvalidOperationException("Android Keystore is unavailable.");
        store.Load(null);
        return store;
    }

    // Only constructed after the API 28 check in the signing path (BiometricPrompt itself is API 28+).
    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private sealed class Callback(byte[] payload, TaskCompletionSource<byte[]> completion)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            try
            {
                using var signature = result?.CryptoObject?.Signature
                    ?? throw new InvalidOperationException("The biometric prompt returned no signing object.");
                signature.Update(payload);
                var der = signature.Sign()
                    ?? throw new InvalidOperationException("Android ECDSA signing failed.");
                completion.TrySetResult(EcdsaSignatureEncoding.DerToP1363P256(der));
            }
            catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.Exception)
            {
                completion.TrySetException(exception);
            }
        }

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            completion.TrySetException(new UserPresenceDeniedException(
                "Biometric verification was cancelled or failed (" + errorCode + ")."));

        public override void OnAuthenticationFailed()
        {
            // A non-matching finger: the prompt stays up and lets the person retry until it reports an error.
        }
    }

    private sealed class CancelListener(TaskCompletionSource<byte[]> completion)
        : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) =>
            completion.TrySetException(new UserPresenceDeniedException("Biometric verification was cancelled."));
    }
}
