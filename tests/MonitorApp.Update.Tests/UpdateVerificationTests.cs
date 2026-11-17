using System.Security.Cryptography;
using MonitorApp.Update;

namespace MonitorApp.Update.Tests;

public sealed class UpdateVerificationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SignedManifestSelectsTargetAndVerifiesArtifactAsync()
    {
        var artifactBytes = "monitor-app-release"u8.ToArray();
        using var signingKey = CreateSigningKey();
        var envelope = CreateEnvelope(signingKey, artifactBytes);

        var manifest = UpdateManifestCodec.VerifyAndRead(
            envelope,
            new TrustedUpdateKey("release-2026", signingKey.ExportSubjectPublicKeyInfo()));
        var artifact = UpdatePolicy.SelectArtifact(
            manifest,
            ReleaseVersion.Parse("1.2.2"),
            "alpha",
            "agent",
            "windows",
            "x64");

        var path = Path.Combine(Path.GetTempPath(), $"monitor-app-update-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(path, artifactBytes);
            await ArtifactIntegrityVerifier.VerifyFileAsync(path, artifact);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ManifestSignedByDifferentKeyIsRejected()
    {
        using var signingKey = CreateSigningKey();
        using var untrustedKey = CreateSigningKey();
        var envelope = CreateEnvelope(signingKey, "release"u8.ToArray());

        var exception = Assert.Throws<UpdateVerificationException>(() =>
            UpdateManifestCodec.VerifyAndRead(
                envelope,
                new TrustedUpdateKey("release-2026", untrustedKey.ExportSubjectPublicKeyInfo())));

        Assert.Equal(UpdateVerificationError.InvalidSignature, exception.Error);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("2.0.0")]
    [Trait("Category", "Unit")]
    public void EqualOrOlderReleaseIsRejected(string currentVersion)
    {
        var manifest = CreateManifest("release"u8.ToArray());

        var exception = Assert.Throws<UpdateVerificationException>(() =>
            UpdatePolicy.SelectArtifact(
                manifest,
                ReleaseVersion.Parse(currentVersion),
                "alpha",
                "agent",
                "windows",
                "x64"));

        Assert.Equal(UpdateVerificationError.DowngradeOrReinstall, exception.Error);
    }

    [Theory]
    [InlineData("linux", "x64")]
    [InlineData("windows", "arm64")]
    [InlineData("linux", "arm64")]
    [Trait("Category", "Unit")]
    public void PlatformOrArchitectureMismatchIsRejected(string platform, string architecture)
    {
        var manifest = CreateManifest("release"u8.ToArray());

        var exception = Assert.Throws<UpdateVerificationException>(() =>
            UpdatePolicy.SelectArtifact(
                manifest,
                ReleaseVersion.Parse("1.2.2"),
                "alpha",
                "agent",
                platform,
                architecture));

        Assert.Equal(UpdateVerificationError.NoMatchingArtifact, exception.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void VersionBelowMinimumCompatibleVersionRequiresManualUpgrade()
    {
        var manifest = CreateManifest("release"u8.ToArray()) with
        {
            MinimumCompatibleVersion = ReleaseVersion.Parse("1.1.0"),
        };

        var exception = Assert.Throws<UpdateVerificationException>(() =>
            UpdatePolicy.SelectArtifact(
                manifest,
                ReleaseVersion.Parse("1.0.9"),
                "alpha",
                "agent",
                "windows",
                "x64"));

        Assert.Equal(UpdateVerificationError.ManualUpgradeRequired, exception.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ArtifactWithDifferentContentIsRejectedAsync()
    {
        var expectedBytes = "expected"u8.ToArray();
        var actualBytes = "tampered"u8.ToArray();
        var artifact = CreateManifest(expectedBytes).Artifacts.Single() with
        {
            Size = actualBytes.Length,
        };
        var path = Path.Combine(Path.GetTempPath(), $"monitor-app-update-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(path, actualBytes);

            var exception = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                ArtifactIntegrityVerifier.VerifyFileAsync(path, artifact));

            Assert.Equal(UpdateVerificationError.ArtifactHashMismatch, exception.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ArtifactWithDifferentSizeIsRejectedBeforeHashingAsync()
    {
        var expectedBytes = "expected"u8.ToArray();
        var artifact = CreateManifest(expectedBytes).Artifacts.Single();
        var path = Path.Combine(Path.GetTempPath(), $"monitor-app-update-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(path, "short"u8.ToArray());

            var exception = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                ArtifactIntegrityVerifier.VerifyFileAsync(path, artifact));

            Assert.Equal(UpdateVerificationError.ArtifactSizeMismatch, exception.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("-1.2.3")]
    [Trait("Category", "Unit")]
    public void NonCanonicalVersionIsRejected(string value)
    {
        Assert.False(ReleaseVersion.TryParse(value, out _));
    }

    private static ECDsa CreateSigningKey() =>
        ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] CreateEnvelope(ECDsa signingKey, byte[] artifactBytes) =>
        UpdateManifestCodec.CreateSignedEnvelope(
            CreateManifest(artifactBytes),
            "release-2026",
            signingKey);

    private static UpdateManifest CreateManifest(byte[] artifactBytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(artifactBytes));
        return new UpdateManifest(
            UpdateManifestCodec.CurrentSchemaVersion,
            "alpha",
            ReleaseVersion.Parse("1.2.3"),
            ReleaseVersion.Parse("1.0.0"),
            new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero),
            [
                new UpdateArtifact(
                    "agent",
                    "windows",
                    "x64",
                    new Uri("https://github.example/releases/monitorapp-agent-windows-x64-1.2.3.zip"),
                    artifactBytes.Length,
                    hash),
            ]);
    }
}
