using System.Security.Cryptography;
using MonitorApp.Peer.Core;
using Xunit;

namespace MonitorApp.Peer.Core.Tests;

public sealed class PeerIdentityTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ExportedPrivateKeyImportsWithStablePublicIdentity()
    {
        using var source = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = source.ExportPkcs8PrivateKey();

        using var imported = EcdsaPeerIdentityKey.ImportPkcs8(privateKey);
        var importedIdentity = PeerPublicIdentity.Create("peer-a", imported);

        Assert.Equal(source.ExportSubjectPublicKeyInfo(), importedIdentity.PublicKeySpki);
        Assert.Equal(
            SHA256.HashData(source.ExportSubjectPublicKeyInfo()),
            importedIdentity.PublicKeyFingerprint);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PublicIdentityRejectsWrongFingerprintAndWrongCurve()
    {
        using var p256 = EcdsaPeerIdentityKey.Create();
        var spki = p256.ExportSubjectPublicKeyInfo();
        Assert.Throws<ArgumentException>(() =>
            new PeerPublicIdentity("peer-a", spki, RandomNumberGenerator.GetBytes(32)));

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<CryptographicException>(() =>
            new PeerPublicIdentity(
                "peer-a",
                p384.ExportSubjectPublicKeyInfo(),
                SHA256.HashData(p384.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PublicIdentityDefensivelyCopiesKeyMaterial()
    {
        using var key = EcdsaPeerIdentityKey.Create();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var fingerprint = SHA256.HashData(publicKey);
        var expectedPublicKey = publicKey.ToArray();
        var expectedFingerprint = fingerprint.ToArray();
        var identity = new PeerPublicIdentity("peer-a", publicKey, fingerprint);

        publicKey.AsSpan().Clear();
        fingerprint.AsSpan().Clear();
        identity.PublicKeySpki.AsSpan().Fill(1);
        identity.PublicKeyFingerprint.AsSpan().Fill(1);

        Assert.Equal(expectedPublicKey, identity.PublicKeySpki);
        Assert.Equal(expectedFingerprint, identity.PublicKeyFingerprint);
    }
}
