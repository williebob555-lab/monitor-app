using MonitorApp.Update.Apply;

namespace MonitorApp.Update.Tests;

public sealed class UpdateServiceTests
{
    private static readonly UpdateTarget Target = new("alpha", "peer", "linux", "x64");
    private static readonly byte[] Package = "peer-package-1.3.0"u8.ToArray();
    private static readonly UpdateApproval Approval = new("owner", DateTimeOffset.UnixEpoch);
    private static readonly ApplyOutcome Applied = new(ApplyOutcomeKind.Applied, "ok");

    private static UpdateService Create(
        TempDir dir, SignedRelease release, byte[] envelope, byte[] download, out FakeFetcher fetcher) =>
        Create(dir, release, fetcher = new FakeFetcher(envelope), new FakeDownloader(download));

    private static UpdateService Create(TempDir dir, SignedRelease release, FakeFetcher fetcher, FakeDownloader downloader) =>
        new(Target, release.Trusted, fetcher, downloader, new UpdateStateStore(dir.Combine("state")), dir.Combine("staging"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckStageApplyPersistsEachStageAcrossRestartAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        var envelope = release.Envelope("1.3.0", Package);
        var installed = ReleaseVersion.Parse("1.2.0");

        using (var first = Create(dir, release, envelope, Package, out _))
        {
            var available = await first.CheckAsync(installed, null, default);
            Assert.Equal(UpdateStage.Available, available!.Stage);
            await first.StageAsync(default);
        }

        // New instance, same state directory: a restarted process sees Staged, with the file really on disk.
        using var second = Create(dir, release, envelope, Package, out _);
        var staged = second.Current!;
        Assert.Equal(UpdateStage.Staged, staged.Stage);
        Assert.Equal(Package, await File.ReadAllBytesAsync(staged.StagedPath!));
        Assert.Empty(Directory.EnumerateFiles(dir.Combine("staging"), "*.partial"));

        var applier = new RecordingApplier(Applied);
        var done = await second.ApplyAsync(Approval, applier, TimeSpan.FromSeconds(1), default);
        Assert.Equal(UpdateStage.Applied, done.Stage);
        Assert.Equal(ReleaseVersion.Parse("1.3.0"), applier.Requests.Single().TargetVersion);
        Assert.Equal(installed, applier.Requests.Single().PreviousVersion);
        Assert.Contains(new UpdateStateStore(dir.Combine("state")).ReadAudit(), l => l.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpToDateReturnsNullAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.2.0", Package), Package, out _);
        Assert.Null(await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TamperedDownloadIsNeverStagedOrAppliedAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        var tampered = (byte[])Package.Clone();
        tampered[0] ^= 0xFF;
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), tampered, out _);
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);

        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => service.StageAsync(default));
        Assert.Equal(UpdateVerificationError.ArtifactHashMismatch, ex.Error);
        Assert.Equal(UpdateStage.Failed, service.Current!.Stage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Combine("staging")));

        var applier = new RecordingApplier(Applied);
        await Assert.ThrowsAsync<UpdateStateException>(() =>
            service.ApplyAsync(Approval, applier, TimeSpan.FromSeconds(1), default));
        Assert.Empty(applier.Requests);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StagedFileModifiedAfterStagingIsRejectedAtApplyAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), Package, out _);
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);
        var staged = await service.StageAsync(default);
        await File.AppendAllTextAsync(staged.StagedPath!, "evil");

        var applier = new RecordingApplier(Applied);
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.ApplyAsync(Approval, applier, TimeSpan.FromSeconds(1), default));
        Assert.Empty(applier.Requests);
        Assert.Equal(UpdateStage.Failed, service.Current!.Stage);
        Assert.False(File.Exists(staged.StagedPath));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ApplyBeforeStagingIsRefusedAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), Package, out _);
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);
        var applier = new RecordingApplier(Applied);
        await Assert.ThrowsAsync<UpdateStateException>(() =>
            service.ApplyAsync(Approval, applier, TimeSpan.FromSeconds(1), default));
        Assert.Empty(applier.Requests);
    }

    [Theory]
    [InlineData(ApplyOutcomeKind.RolledBack, UpdateStage.RolledBack)]
    [InlineData(ApplyOutcomeKind.Failed, UpdateStage.Failed)]
    [Trait("Category", "Unit")]
    public async Task ApplierOutcomeBecomesExplicitStageAsync(ApplyOutcomeKind kind, UpdateStage expected)
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), Package, out _);
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);
        await service.StageAsync(default);
        var done = await service.ApplyAsync(Approval, new RecordingApplier(new ApplyOutcome(kind, "why")), TimeSpan.FromSeconds(1), default);
        Assert.Equal(expected, done.Stage);
        Assert.Equal("why", done.Detail);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OlderManifestIsRejectedUnlessExplicitlyAuthorizedAndAuditedAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.1.0", Package), Package, out _);
        var installed = ReleaseVersion.Parse("1.2.0");

        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => service.CheckAsync(installed, null, default));
        Assert.Equal(UpdateVerificationError.DowngradeOrReinstall, ex.Error);

        var wrongVersion = new DowngradeAuthorization(ReleaseVersion.Parse("1.0.5"), "owner", "bad release");
        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.CheckAsync(installed, wrongVersion, default));

        var authorized = new DowngradeAuthorization(ReleaseVersion.Parse("1.1.0"), "owner", "1.2.0 crashes on boot");
        var state = await service.CheckAsync(installed, authorized, default);
        Assert.Equal("1.1.0", state!.TargetVersion);
        var audit = new UpdateStateStore(dir.Combine("state")).ReadAudit();
        Assert.Contains(audit, l => l.Contains("downgrade-authorized", StringComparison.Ordinal) && l.Contains("crashes on boot", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReplayOfManifestOlderThanHighestSeenIsRejectedEvenIfNewerThanInstalledAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        var fetcher = new FakeFetcher(release.Envelope("1.5.0", Package));
        using var service = Create(dir, release, fetcher, new FakeDownloader(Package));
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);

        fetcher.Envelope = release.Envelope("1.3.0", Package);
        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default));
        Assert.Equal(UpdateVerificationError.DowngradeOrReinstall, ex.Error);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ManifestSignedByUntrustedKeyIsRejectedAndAuditedAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var attacker = new SignedRelease();
        using var service = Create(dir, release, attacker.Envelope("1.3.0", Package), Package, out _);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default));
        Assert.Null(service.Current);
        Assert.Contains(new UpdateStateStore(dir.Combine("state")).ReadAudit(), l => l.Contains("check-rejected", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InterruptedApplyBecomesExplicitFailedOnRestartAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), Package, out _);
        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);
        var staged = await service.StageAsync(default);

        // Simulate a crash mid-apply by persisting Applying directly, as the service did before calling the applier.
        var store = new UpdateStateStore(dir.Combine("state"));
        var snapshot = store.Read();
        store.Write(snapshot with { Active = staged with { Stage = UpdateStage.Applying } });

        var reconciled = await service.ReconcileAsync(ReleaseVersion.Parse("1.2.0"), default);
        Assert.Equal(UpdateStage.Failed, reconciled!.Stage);
        Assert.Contains("interrupted", reconciled.Detail, StringComparison.Ordinal);

        store.Write(snapshot with { Active = staged with { Stage = UpdateStage.Applying } });
        Assert.Equal(UpdateStage.Applied, (await service.ReconcileAsync(ReleaseVersion.Parse("1.3.0"), default))!.Stage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AndroidOfferExposesOnlyAVerifiedStagedApkAndReconcilesAfterInstallAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        var apk = "apk-bytes"u8.ToArray();
        var target = new UpdateTarget("alpha", "peer", "android", "x64");
        using var service = new UpdateService(
            target, release.Trusted, new FakeFetcher(release.Envelope("1.3.0", apk, "android")), new FakeDownloader(apk),
            new UpdateStateStore(dir.Combine("state")), dir.Combine("staging"));

        await service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default);
        Assert.Null(service.GetAndroidOffer());
        await service.StageAsync(default);

        var offer = service.GetAndroidOffer()!;
        Assert.Equal(ReleaseVersion.Parse("1.3.0"), offer.Version);
        Assert.Equal(apk, await File.ReadAllBytesAsync(offer.ApkPath));

        // User installed the APK through the system installer; next launch reports the new version.
        Assert.Equal(UpdateStage.Applied, (await service.ReconcileAsync(ReleaseVersion.Parse("1.3.0"), default))!.Stage);
        Assert.Null(service.GetAndroidOffer());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CorruptStateFileFailsClosedAsync()
    {
        using var dir = new TempDir();
        using var release = new SignedRelease();
        using var service = Create(dir, release, release.Envelope("1.3.0", Package), Package, out _);
        await File.WriteAllTextAsync(dir.Combine("state", "update-state.json"), "{not json");
        await Assert.ThrowsAsync<UpdateStateCorruptException>(() => service.CheckAsync(ReleaseVersion.Parse("1.2.0"), null, default));
    }
}
