using Android.Security.Keystore;
using Java.Security;
using Java.Security.Spec;
using MonitorApp.Android.Core;

namespace MonitorApp.Android;

public sealed class AndroidKeystoreDeviceKeySigner : IDeviceKeySigner
{
    private const string AndroidKeyStore = "AndroidKeyStore";
    private readonly string alias;

    public AndroidKeystoreDeviceKeySigner(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        this.alias = alias;
        EnsureKeyExists();
    }

    public byte[] ExportPublicKeySpki()
    {
        using var store = LoadStore();
        using var certificate = store.GetCertificate(alias)
            ?? throw new InvalidOperationException("Android Keystore device certificate is unavailable.");
        using var publicKey = certificate.PublicKey
            ?? throw new InvalidOperationException("Android Keystore device public key is unavailable.");
        return publicKey.GetEncoded()
            ?? throw new InvalidOperationException("Android Keystore did not export the public key.");
    }

    public byte[] SignP1363Sha256(ReadOnlySpan<byte> payload)
    {
        using var store = LoadStore();
        using var entry = store.GetEntry(alias, null) as KeyStore.PrivateKeyEntry
            ?? throw new InvalidOperationException("Android Keystore device key is unavailable.");
        using var signer = Signature.GetInstance("SHA256withECDSA")
            ?? throw new InvalidOperationException("Android ECDSA signer is unavailable.");
        signer.InitSign(entry.PrivateKey);
        signer.Update(payload.ToArray());
        var der = signer.Sign()
            ?? throw new InvalidOperationException("Android ECDSA signing failed.");
        return EcdsaSignatureEncoding.DerToP1363P256(der);
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
        using var parameterBuilder = new KeyGenParameterSpec.Builder(
            alias,
            KeyStorePurpose.Sign | KeyStorePurpose.Verify);
        _ = parameterBuilder.SetAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"));
        _ = parameterBuilder.SetDigests(KeyProperties.DigestSha256);
        _ = parameterBuilder.SetUserAuthenticationRequired(false);
        using var parameterSpec = parameterBuilder.Build()
            ?? throw new InvalidOperationException("Android Keystore key parameters are unavailable.");
        generator.Initialize(parameterSpec);
        using var pair = generator.GenerateKeyPair();
        if (pair is null)
        {
            throw new InvalidOperationException("Android Keystore did not generate a device key.");
        }
    }

    private static KeyStore LoadStore()
    {
        var store = KeyStore.GetInstance(AndroidKeyStore)
            ?? throw new InvalidOperationException("Android Keystore is unavailable.");
        store.Load(null);
        return store;
    }
}
