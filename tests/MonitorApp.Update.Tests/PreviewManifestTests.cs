using System.Security.Cryptography;

namespace MonitorApp.Update.Tests;

public sealed class PreviewManifestTests
{
    private static readonly string[] Platforms = ["ubuntu", "fedora", "windows", "android"];

    private static (UpdateManifest Manifest, byte[] Envelope, TrustedUpdateKey Trusted) Create(string version)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifacts = new List<UpdateArtifact>();
        foreach (var platform in Platforms)
        {
            artifacts.Add(Artifact("node", platform, "x64", version));
            artifacts.Add(Artifact("desktop", platform, "x64", version));
        }

        var manifest = new UpdateManifest(
            UpdateManifestCodec.CurrentSchemaVersion,
            "preview",
            ReleaseVersion.Parse(version),
            ReleaseVersion.Parse("0.1.0"),
            new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
            artifacts);
        return (manifest, UpdateManifestCodec.CreateSignedEnvelope(manifest, "preview-key", key), new TrustedUpdateKey("preview-key", key.ExportSubjectPublicKeyInfo()));
    }

    private static UpdateArtifact Artifact(string component, string platform, string arch, string version) =>
        new(
            component,
            platform,
            arch,
            new Uri($"https://github.com/o/r/releases/download/preview-{version}/monitorapp-{component}-{platform}-{arch}-{version}.zip"),
            1234,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(component + platform + version))));

    [Fact]
    [Trait("Category", "Unit")]
    public void MultiArtifactPreviewManifestRoundTripsAndSelectsEachTarget()
    {
        var (manifest, envelope, trusted) = Create("0.1.42");

        var verified = UpdateManifestCodec.VerifyAndRead(envelope, trusted);

        Assert.Equal("preview", verified.Channel);
        Assert.Equal(manifest.Artifacts.Count, verified.Artifacts.Count);
        foreach (var platform in Platforms)
        {
            var selected = UpdatePolicy.SelectArtifact(verified, ReleaseVersion.Parse("0.1.41"), "preview", "node", platform, "x64");
            Assert.Contains($"-node-{platform}-x64-0.1.42", selected.Url.AbsoluteUri, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TamperedArtifactListFailsSignatureVerification()
    {
        var (_, envelope, trusted) = Create("0.1.42");
        // Re-encode the signed payload with one artifact size changed; the signature no longer matches.
        using var document = System.Text.Json.JsonDocument.Parse(envelope);
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString()!));
        var changed = payload.Replace("1234", "1235", StringComparison.Ordinal);
        Assert.NotEqual(payload, changed);
        var forged = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = document.RootElement.GetProperty("schemaVersion").GetInt32(),
            keyId = "preview-key",
            algorithm = document.RootElement.GetProperty("algorithm").GetString(),
            payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(changed)),
            signature = document.RootElement.GetProperty("signature").GetString(),
        });

        Assert.Throws<UpdateVerificationException>(
            () => UpdateManifestCodec.VerifyAndRead(System.Text.Encoding.UTF8.GetBytes(forged), trusted));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PreviewVersionsOrderNumericallyByRunNumberAndOlderOnesAreRejected()
    {
        Assert.True(ReleaseVersion.Parse("0.1.10") > ReleaseVersion.Parse("0.1.9"));
        Assert.True(ReleaseVersion.Parse("0.1.100") > ReleaseVersion.Parse("0.1.99"));
        Assert.True(ReleaseVersion.Parse("0.2.1") > ReleaseVersion.Parse("0.1.999"));

        var (manifest, _, _) = Create("0.1.9");
        var older = Assert.Throws<UpdateVerificationException>(
            () => UpdatePolicy.SelectArtifact(manifest, ReleaseVersion.Parse("0.1.10"), "preview", "node", "ubuntu", "x64"));
        Assert.Equal(UpdateVerificationError.DowngradeOrReinstall, older.Error);
        Assert.Throws<UpdateVerificationException>(
            () => UpdatePolicy.SelectArtifact(manifest, ReleaseVersion.Parse("0.1.8"), "alpha", "node", "ubuntu", "x64"));
        Assert.False(ReleaseVersion.TryParse("0.1.5-preview", out _));
    }
}
