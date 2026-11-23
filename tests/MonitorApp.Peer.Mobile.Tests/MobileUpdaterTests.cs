using System.Security.Cryptography;
using MonitorApp.Update;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

public sealed class MobileUpdaterTests : IDisposable
{
    private static readonly byte[] Apk = "signed-apk-0.1.13"u8.ToArray();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-mobile-updater-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task FindsVerifiesAndStagesTheApkButInstallsOnlyWhenAsked()
    {
        var installer = new RecordingInstaller();
        using var updater = Create(installer, Envelope("0.1.13", Apk), Apk);

        await updater.CheckNowAsync(default);

        var status = updater.GetStatus();
        Assert.Equal("0.1.12", status.InstalledVersion);
        Assert.Equal("Staged", status.Stage);
        Assert.Equal("0.1.13", status.TargetVersion);
        Assert.True(status.ApprovalPending);
        Assert.NotNull(status.LastCheckedUtc);
        Assert.Null(status.LastError);
        Assert.Empty(installer.Paths);

        Assert.Null(await updater.InstallAsync("0.1.13", default));
        var path = Assert.Single(installer.Paths);
        Assert.Equal(Apk, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ATamperedDownloadOrWrongVersionIsNeverHandedToTheInstaller()
    {
        var installer = new RecordingInstaller();
        using var updater = Create(installer, Envelope("0.1.13", Apk), Apk);
        await updater.CheckNowAsync(default);

        Assert.NotNull(await updater.InstallAsync("0.1.14", default));
        var staged = Directory.GetFiles(Path.Combine(directory, "staging")).Single();
        await File.WriteAllBytesAsync(staged, "tampered-apk-0.1.13"u8.ToArray());
        Assert.NotNull(await updater.InstallAsync("0.1.13", default));
        Assert.Empty(installer.Paths);
    }

    [Fact]
    public async Task AManifestSignedByAnotherKeyIsRejectedAndReported()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = new RecordingInstaller();
        using var updater = Create(installer, Envelope("0.1.13", Apk, other), Apk);

        await updater.CheckNowAsync(default);

        var status = updater.GetStatus();
        Assert.False(status.ApprovalPending);
        Assert.NotNull(status.LastError);
        Assert.Empty(installer.Paths);
    }

    [Fact]
    public async Task WithoutAPackagedKeyUpdatesAreOffAndSaySo()
    {
        using var updater = new MobileUpdater(directory, ReleaseVersion.Parse("0.1.12"), null, new RecordingInstaller());
        await updater.CheckNowAsync(default);
        var status = updater.GetStatus();
        Assert.Equal("Disabled", status.Stage);
        Assert.Contains("update key", status.Detail, StringComparison.Ordinal);
        Assert.Null(MobileUpdateTrust.TryParse("{}"u8));
    }

    public void Dispose()
    {
        key.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private MobileUpdater Create(RecordingInstaller installer, byte[] envelope, byte[] download) =>
        new(
            directory,
            ReleaseVersion.Parse("0.1.12"),
            new MobileUpdateTrust("preview-key", key.ExportSubjectPublicKeyInfo(), "owner/repo"),
            installer,
            new FakeFetcher(envelope),
            new FakeDownloader(download));

    private byte[] Envelope(string version, byte[] artifact, ECDsa? signer = null) =>
        UpdateManifestCodec.CreateSignedEnvelope(
            new UpdateManifest(
                UpdateManifestCodec.CurrentSchemaVersion,
                "preview",
                ReleaseVersion.Parse(version),
                ReleaseVersion.Parse("0.1.0"),
                new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero),
                [
                    new UpdateArtifact(
                        "mobile",
                        "android",
                        "universal",
                        new Uri($"https://github.com/o/r/releases/download/preview-{version}/monitorapp-mobile-android-universal-{version}.apk"),
                        artifact.Length,
                        Convert.ToHexStringLower(SHA256.HashData(artifact))),
                ]),
            "preview-key",
            signer ?? key);

    private sealed class RecordingInstaller : IApkInstaller
    {
        public List<string> Paths { get; } = [];

        public Task<string?> InstallAsync(string apkPath, CancellationToken cancellationToken)
        {
            Paths.Add(apkPath);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeFetcher(byte[] envelope) : IManifestFetcher
    {
        public Task<byte[]> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(envelope);
    }

    private sealed class FakeDownloader(byte[] content) : IArtifactDownloader
    {
        public Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken) =>
            destination.WriteAsync(content, cancellationToken).AsTask();
    }
}
