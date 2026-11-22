using System.Security.Cryptography;
using MonitorApp.Update.Apply;

namespace MonitorApp.Update.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "monitor-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class SignedRelease : IDisposable
{
    public SignedRelease()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Trusted = new TrustedUpdateKey("release-test", Key.ExportSubjectPublicKeyInfo());
    }

    public ECDsa Key { get; }

    public TrustedUpdateKey Trusted { get; }

    public byte[] Envelope(string version, byte[] artifact, string platform = "linux", string? sha = null) =>
        UpdateManifestCodec.CreateSignedEnvelope(
            new UpdateManifest(
                UpdateManifestCodec.CurrentSchemaVersion,
                "alpha",
                ReleaseVersion.Parse(version),
                ReleaseVersion.Parse("1.0.0"),
                new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero),
                [
                    new UpdateArtifact(
                        "peer",
                        platform,
                        "x64",
                        new Uri($"https://github.example/releases/peer-{version}.pkg"),
                        artifact.Length,
                        sha ?? Convert.ToHexStringLower(SHA256.HashData(artifact))),
                ]),
            "release-test",
            Key);

    public void Dispose() => Key.Dispose();
}

internal sealed class FakeFetcher(byte[] envelope) : IManifestFetcher
{
    public byte[] Envelope { get; set; } = envelope;

    public Task<byte[]> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(Envelope);
}

internal sealed class FakeDownloader(byte[] content) : IArtifactDownloader
{
    public byte[] Content { get; set; } = content;

    public async Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken) =>
        await destination.WriteAsync(Content, cancellationToken);
}

internal sealed class RecordingApplier(ApplyOutcome outcome) : IUpdateApplier
{
    public List<ApplyRequest> Requests { get; } = [];

    public Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(outcome);
    }
}
