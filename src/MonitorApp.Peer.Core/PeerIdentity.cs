using System.Security.Cryptography;

namespace MonitorApp.Peer.Core;

public interface IPeerIdentityKey : IDisposable
{
    byte[] ExportSubjectPublicKeyInfo();

    byte[] Sign(ReadOnlySpan<byte> payload);
}

public sealed class EcdsaPeerIdentityKey : IPeerIdentityKey
{
    private readonly ECDsa _key;

    private EcdsaPeerIdentityKey(ECDsa key)
    {
        _key = key;
    }

    public static EcdsaPeerIdentityKey Create() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static EcdsaPeerIdentityKey ImportPkcs8(ReadOnlySpan<byte> privateKeyPkcs8)
    {
        if (privateKeyPkcs8.IsEmpty)
        {
            throw new ArgumentException("Private key is required.", nameof(privateKeyPkcs8));
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
            if (bytesRead != privateKeyPkcs8.Length)
            {
                throw new CryptographicException("Private key contains trailing data.");
            }

            PeerKeyValidation.RequireP256(key);
            return new EcdsaPeerIdentityKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportSubjectPublicKeyInfo() => _key.ExportSubjectPublicKeyInfo();

    public byte[] Sign(ReadOnlySpan<byte> payload) =>
        _key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

public sealed record PeerPublicIdentity
{
    private readonly byte[] _publicKeySpki;
    private readonly byte[] _publicKeyFingerprint;

    public PeerPublicIdentity(
        string peerId,
        byte[] publicKeySpki,
        byte[] publicKeyFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        ArgumentNullException.ThrowIfNull(publicKeyFingerprint);

        using var key = PeerKeyValidation.ImportP256PublicKey(publicKeySpki);
        var expectedFingerprint = SHA256.HashData(publicKeySpki);
        if (!CryptographicOperations.FixedTimeEquals(expectedFingerprint, publicKeyFingerprint))
        {
            throw new ArgumentException(
                "Public key fingerprint does not match the public key.",
                nameof(publicKeyFingerprint));
        }

        PeerId = peerId;
        _publicKeySpki = publicKeySpki.ToArray();
        _publicKeyFingerprint = publicKeyFingerprint.ToArray();
    }

    public string PeerId { get; }

    public byte[] PublicKeySpki => _publicKeySpki.ToArray();

    public byte[] PublicKeyFingerprint => _publicKeyFingerprint.ToArray();

    public static PeerPublicIdentity Create(string peerId, IPeerIdentityKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return new PeerPublicIdentity(peerId, publicKey, SHA256.HashData(publicKey));
    }
}

internal static class PeerKeyValidation
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static ECDsa ImportP256PublicKey(ReadOnlySpan<byte> publicKeySpki)
    {
        if (publicKeySpki.IsEmpty)
        {
            throw new ArgumentException("Public key is required.", nameof(publicKeySpki));
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out var bytesRead);
            if (bytesRead != publicKeySpki.Length)
            {
                throw new CryptographicException("Public key contains trailing data.");
            }

            RequireP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static void RequireP256(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        if (!string.Equals(parameters.Curve.Oid.Value, P256Oid, StringComparison.Ordinal))
        {
            throw new CryptographicException("Peer identity keys must use ECDSA P-256.");
        }
    }
}
