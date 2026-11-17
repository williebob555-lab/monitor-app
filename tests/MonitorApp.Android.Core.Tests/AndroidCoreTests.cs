using System.Security.Cryptography;
using Google.Protobuf;
using MonitorApp.Android.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Android.Core.Tests;

public sealed class AndroidCoreTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void MissingBatteryCapacityIsUnavailableRatherThanZero()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        var reading = BatteryReadingNormalizer.NormalizeCapacity(-1, capturedAt);

        Assert.False(reading.IsAvailable);
        Assert.Null(reading.Value);
        Assert.Equal(capturedAt, reading.SourceTime);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidZeroBatteryCapacityRemainsPresent()
    {
        var reading = BatteryReadingNormalizer.NormalizeCapacity(0, DateTimeOffset.UtcNow);

        Assert.True(reading.IsAvailable);
        Assert.Equal(0, reading.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NetworkSummaryPreservesVpnAndUnderlyingTransport()
    {
        var summary = NetworkTransportSummary.Create(
            isConnected: true,
            isVpn: true,
            isWifi: true,
            isCellular: false,
            isEthernet: false,
            DateTimeOffset.UtcNow);

        Assert.True(summary.IsVpn);
        Assert.Equal("Wi-Fi + VPN", summary.DisplayText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ControllerAddressRequiresHttpsAndRejectsEmbeddedCredentials()
    {
        Assert.Equal(
            "https://controller.example.test/",
            ControllerUriPolicy.ParseHttps("https://controller.example.test/path").ToString());
        Assert.Throws<ArgumentException>(() => ControllerUriPolicy.ParseHttps("http://controller.example.test"));
        Assert.Throws<ArgumentException>(() => ControllerUriPolicy.ParseHttps("https://user:secret@controller.example.test"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnrollmentMaterialRequiresExactOneTimeFieldSizes()
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var challenge = Convert.ToBase64String(new byte[32]);

        var material = EnrollmentMaterial.Parse("enrollment-1", secret, challenge, "Phone");

        Assert.Equal(32, material.EnrollmentSecret.Length);
        Assert.Equal(32, material.ProofChallenge.Length);
        Assert.Throws<ArgumentException>(() => EnrollmentMaterial.Parse(
            "enrollment-1",
            Convert.ToBase64String(new byte[31]),
            challenge,
            "Phone"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnrollmentRequestUsesRealP256ProofAndContainsNoPrivateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new TestSigner(key);
        var material = EnrollmentMaterial.Parse(
            "enrollment-1",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            "Phone");

        var request = EnrollmentCompletionBuilder.Build(material, signer);
        var payload = ProofPayload.CreateEnrollment(
            request.EnrollmentId,
            material.ProofChallenge.Span,
            request.PublicKeySpki.Span);

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(request.PublicKeySpki.Span, out _);
        Assert.True(publicKey.VerifyData(
            payload,
            request.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(Platform.Android, request.Platform);
        Assert.DoesNotContain(
            key.ExportPkcs8PrivateKey(),
            request.ToByteArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AndroidDerSignatureConvertsToControllerP1363Format()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = RandomNumberGenerator.GetBytes(71);
        var der = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        var p1363 = EcdsaSignatureEncoding.DerToP1363P256(der);

        Assert.Equal(64, p1363.Length);
        Assert.True(key.VerifyData(
            payload,
            p1363,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private sealed class TestSigner(ECDsa key) : IDeviceKeySigner
    {
        public byte[] ExportPublicKeySpki() => key.ExportSubjectPublicKeyInfo();

        public byte[] SignP1363Sha256(ReadOnlySpan<byte> payload) =>
            key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
