using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Node.Update;
using MonitorApp.Update;
using MonitorApp.Update.Apply;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class NodeUpdateAgentTests : IDisposable
{
    private static readonly byte[] Package = "node-package-0.1.2"u8.ToArray();

    /// <summary>Waits for the agent's startup work instead of a fixed sleep, which a busy CI runner can outlast.</summary>
    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-update-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task ApprovePolicyStagesAndWaitsThenApprovalAppliesOnce()
    {
        var applier = new RecordingApplier();
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, applier);

        await agent.RunCycleAsync(default);

        var status = agent.GetStatus();
        Assert.Equal("Staged", status.Stage);
        Assert.True(status.ApprovalPending);
        Assert.Equal("0.1.2", status.TargetVersion);
        Assert.Equal(0, applier.Calls);
        Assert.True(File.Exists(Path.Combine(directory, "update", "manifest.json")));

        // A second cycle while waiting never re-downloads or applies.
        await agent.RunCycleAsync(default);
        Assert.Equal(0, applier.Calls);

        Assert.True(await agent.ApproveAsync(default));
        await agent.WhenIdleAsync();
        Assert.Equal(1, applier.Calls);
        Assert.Equal("Applied", agent.GetStatus().Stage);
        Assert.False(await agent.ApproveAsync(default));
    }

    [Fact]
    public async Task CheckNowStagesWithoutInstallingAndRecordsWhenItLooked()
    {
        var applier = new RecordingApplier();
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, applier);
        Assert.Null(agent.GetStatus().LastCheckedUtc);

        await agent.CheckNowAsync(default);

        var status = agent.GetStatus();
        Assert.NotNull(status.LastCheckedUtc);
        Assert.Equal("Staged", status.Stage);
        Assert.True(status.ApprovalPending);
        Assert.Equal(0, applier.Calls);
    }

    [Fact]
    public void PeriodicCheckDefaultsToHourly()
    {
        var options = NodeUpdateOptions.FromEnvironment(
            new NodeOptions(directory, System.Net.IPAddress.Loopback, 55431, 55432, MonitorApp.Peer.LocalApi.LocalPresencePolicy.RequiredWhenEnrolled),
            name => name == "MONITOR_APP_UPDATE_PACKAGE" ? (OperatingSystem.IsWindows() ? "zip" : "deb") : null);
        Assert.Equal(TimeSpan.FromHours(1), options.Interval);
    }

    [Fact]
    public async Task UpdateHistoryListsOnlyInstallOutcomesWithVersionOldestFirstWithinTheRange()
    {
        var applier = new RecordingApplier(ApplyOutcomeKind.RolledBack);
        using var agent = Create(NodeUpdatePolicy.Auto, Envelope("0.1.2", Package), Package, applier);
        await agent.RunCycleAsync(default);
        Assert.Equal("RolledBack", agent.GetStatus().Stage);

        var all = ((MonitorApp.Peer.LocalApi.ILocalUpdateHistory)agent)
            .GetUpdateHistory(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var outcome = Assert.Single(all);
        Assert.Equal("RolledBack", outcome.Outcome);
        Assert.Equal("0.1.2", outcome.Version);
        Assert.Empty(((MonitorApp.Peer.LocalApi.ILocalUpdateHistory)agent)
            .GetUpdateHistory(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2)));
    }

    [Fact]
    public async Task BoundApprovalRequiresTheExactStagedVersionAndDigest()
    {
        var applier = new RecordingApplier();
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, applier);
        await agent.RunCycleAsync(default);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Package));
        Assert.Equal(digest, agent.GetStatus().TargetDigest);

        Assert.False(await agent.ApproveAsync("0.1.3", digest, default));
        Assert.False(await agent.ApproveAsync("0.1.2", new string('0', 64), default));
        await agent.WhenIdleAsync();
        Assert.Equal(0, applier.Calls);

        Assert.True(await agent.ApproveAsync("0.1.2", digest.ToUpperInvariant(), default));
        await agent.WhenIdleAsync();
        Assert.Equal(1, applier.Calls);
        Assert.False(await agent.ApproveAsync("0.1.2", digest, default));
    }

    [Fact]
    public async Task AutoPolicyAppliesWithoutApproval()
    {
        var applier = new RecordingApplier();
        using var agent = Create(NodeUpdatePolicy.Auto, Envelope("0.1.2", Package), Package, applier);

        await agent.RunCycleAsync(default);

        Assert.Equal(1, applier.Calls);
        Assert.Equal("Applied", agent.GetStatus().Stage);
    }

    [Fact]
    public async Task FailedApplyIsNotRetriedForTheSameVersion()
    {
        var applier = new RecordingApplier(ApplyOutcomeKind.RolledBack);
        using var agent = Create(NodeUpdatePolicy.Auto, Envelope("0.1.2", Package), Package, applier);

        await agent.RunCycleAsync(default);
        await agent.RunCycleAsync(default);

        Assert.Equal(1, applier.Calls);
        Assert.Equal("RolledBack", agent.GetStatus().Stage);
    }

    [Theory]
    [InlineData("untrusted-key")]
    [InlineData("bad-hash")]
    [InlineData("older-version")]
    public async Task VerificationFailureNeverStagesOrApplies(string scenario)
    {
        var applier = new RecordingApplier();
        byte[] envelope;
        byte[] download = Package;
        switch (scenario)
        {
            case "untrusted-key":
                using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    envelope = Envelope("0.1.2", Package, other);
                }

                break;
            case "bad-hash":
                envelope = Envelope("0.1.2", Package);
                download = "tampered"u8.ToArray();
                break;
            default:
                envelope = Envelope("0.1.0", Package);
                break;
        }

        using var agent = Create(NodeUpdatePolicy.Auto, envelope, download, applier);

        await agent.RunCycleAsync(default);

        var status = agent.GetStatus();
        Assert.Equal(0, applier.Calls);
        Assert.NotEqual("Staged", status.Stage);
        Assert.NotEqual("Applied", status.Stage);
        Assert.False(status.ApprovalPending);
        Assert.False(await agent.ApproveAsync(default));
        if (scenario != "bad-hash")
        {
            Assert.NotNull(status.LastError);
        }
    }

    [Fact]
    public async Task RestoredVersionReportsRolledBackWhenTheHelperResultArrivesAfterStartup()
    {
        // The helper restarts the restored 0.1.1 before it has confirmed it healthy, so the result file appears only
        // after this node has started and settled the interrupted apply as Failed.
        var update = Path.Combine(directory, "update");
        Directory.CreateDirectory(update);
        new UpdateStateStore(update).Write(new UpdateStateSnapshot("0.1.2", new UpdateState(
            UpdateStage.Applying, "node", "ubuntu", "x64", "0.1.2", "0.1.1",
            new Uri("https://github.com/o/r/releases/download/preview-0.1.2/monitorapp-node-ubuntu-x64-0.1.2.deb"),
            Package.Length, Convert.ToHexStringLower(SHA256.HashData(Package)), null, null, DateTimeOffset.UtcNow)));
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, new RecordingApplier(), interval: TimeSpan.FromHours(1));

        await agent.StartAsync(default);
        var settled = DateTime.UtcNow.AddSeconds(10);
        while (agent.GetStatus().Stage != "Failed" && DateTime.UtcNow < settled)
        {
            await Task.Delay(50);
        }

        Assert.Equal("Failed", agent.GetStatus().Stage);
        await File.WriteAllTextAsync(
            Path.Combine(update, "apply-result.json"),
            """{"TargetVersion":"0.1.2","Kind":"RolledBack","Detail":"Version 0.1.2 did not report healthy. Restored 0.1.1."}""");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (agent.GetStatus().Stage != "RolledBack" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await agent.StopAsync(default);
        var status = agent.GetStatus();
        Assert.True(status.Stage == "RolledBack", $"stage {status.Stage}, detail {status.Detail}, error {status.LastError}");
        Assert.Equal("0.1.2", status.TargetVersion);
        Assert.False(File.Exists(Path.Combine(update, "apply-result.json")));
        var outcome = Assert.Single(((MonitorApp.Peer.LocalApi.ILocalUpdateHistory)agent)
            .GetUpdateHistory(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)), entry => entry.Outcome == "RolledBack");
        Assert.Equal("0.1.2", outcome.Version);
    }

    [Fact]
    public async Task StartupWritesTheHealthMarkerWithTheInstalledVersion()
    {
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, new RecordingApplier(), interval: TimeSpan.FromHours(1));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var marker = Path.Combine(directory, "update", "health.json");
        await agent.StartAsync(default);
        await WaitForFileAsync(marker);
        await agent.StopAsync(default);

        Assert.True(File.Exists(marker));
        Assert.True(File.GetLastWriteTimeUtc(marker) >= before.UtcDateTime);
        using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
        Assert.Equal("0.1.1", document.RootElement.GetProperty("version").GetString());
        var probe = new FileHealthProbe(marker);
        Assert.True(await probe.WaitForHealthyAsync(ReleaseVersion.Parse("0.1.1"), before, TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task UnconfiguredAgentIsDisabledButStillWritesTheHealthMarker()
    {
        var options = Options(NodeUpdatePolicy.Approve, TimeSpan.FromHours(1));
        using var agent = new NodeUpdateAgent(options, null, null, null, new RecordingApplier(), NullLogger<NodeUpdateAgent>.Instance);

        await agent.StartAsync(default);
        await WaitForFileAsync(options.HealthMarkerPath);
        await agent.StopAsync(default);

        Assert.Equal("Disabled", agent.GetStatus().Stage);
        Assert.True(File.Exists(options.HealthMarkerPath));
        Assert.False(await agent.ApproveAsync(default));
    }

    [Fact]
    public void PolicyDefaultsToApproveAndOneLineSwitchesItToAuto()
    {
        var node = new NodeOptions(directory, System.Net.IPAddress.Loopback, 1, 0);

        Assert.Equal(NodeUpdatePolicy.Approve, NodeUpdateOptions.FromEnvironment(node, _ => null).Policy);
        Assert.Equal(NodeUpdatePolicy.Auto, NodeUpdateOptions.FromEnvironment(node, name => name == NodeUpdateOptions.PolicyVariable ? "auto" : null).Policy);
        Assert.Throws<ArgumentException>(() => NodeUpdateOptions.FromEnvironment(node, name => name == NodeUpdateOptions.PolicyVariable ? "yolo" : null));
        Assert.Throws<ArgumentException>(() => NodeUpdateOptions.FromEnvironment(node, name => name == "MONITOR_APP_UPDATE_API_URL" ? "http://example.test/" : null));
    }

    [Fact]
    public void PackageKindDrivesPlatformAndMustMatchTheHost()
    {
        var node = new NodeOptions(directory, System.Net.IPAddress.Loopback, 1, 0);
        var detected = NodeUpdateOptions.FromEnvironment(node, _ => null);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : detected.PackageKind == "rpm" ? "fedora" : "ubuntu", detected.Platform);

        var foreign = OperatingSystem.IsWindows() ? "deb" : "zip";
        Assert.Throws<ArgumentException>(() => NodeUpdateOptions.FromEnvironment(node, name => name == "MONITOR_APP_UPDATE_PACKAGE" ? foreign : null));
        Assert.Throws<ArgumentException>(() => NodeUpdateOptions.FromEnvironment(node, name => name == "MONITOR_APP_UPDATE_PACKAGE" ? "msi" : null));
    }

    [Fact]
    public async Task WindowsZipWithoutTheInstalledHelperStagesButNeverOffersApproval()
    {
        var applier = new RecordingApplier();
        var options = Options(NodeUpdatePolicy.Auto, TimeSpan.FromHours(6), "zip", Path.Combine(directory, "install"));
        using var agent = CreateWindows(options, applier);

        await agent.RunCycleAsync(default);

        var status = agent.GetStatus();
        Assert.Equal("Staged", status.Stage);
        Assert.False(status.ApprovalPending);
        Assert.False(await agent.ApproveAsync(default));
        Assert.Equal(0, applier.Calls);
    }

    [Fact]
    public async Task WindowsZipWithTheInstalledHelperWaitsForApprovalThenApplies()
    {
        var installRoot = Path.Combine(directory, "install");
        Directory.CreateDirectory(Path.Combine(installRoot, "versions"));
        File.WriteAllText(Path.Combine(installRoot, WindowsApplyHelper.MarkerFileName), "{}");
        var applier = new RecordingApplier();
        using var agent = CreateWindows(Options(NodeUpdatePolicy.Approve, TimeSpan.FromHours(6), "zip", installRoot), applier);

        await agent.RunCycleAsync(default);

        Assert.True(agent.GetStatus().ApprovalPending);
        Assert.Equal(0, applier.Calls);
        Assert.True(await agent.ApproveAsync(default));
        await agent.WhenIdleAsync();
        Assert.Equal(1, applier.Calls);
    }

    [Fact]
    public void InstallRootComesFromTheEnvironmentAndGatesWindowsApply()
    {
        var node = new NodeOptions(directory, System.Net.IPAddress.Loopback, 1, 0);
        var options = NodeUpdateOptions.FromEnvironment(node, name => name == NodeUpdateOptions.InstallRootVariable ? directory : null);
        Assert.Equal(directory, options.InstallRoot);
        Assert.Null(NodeUpdateOptions.FromEnvironment(node, _ => null).InstallRoot);
        Assert.True(Options(NodeUpdatePolicy.Approve, TimeSpan.FromHours(1), "deb").CanApplyHere);
        Assert.True(Options(NodeUpdatePolicy.Approve, TimeSpan.FromHours(1), "rpm").CanApplyHere);
        Assert.False(Options(NodeUpdatePolicy.Approve, TimeSpan.FromHours(1), "zip").CanApplyHere);
    }

    private NodeUpdateAgent CreateWindows(NodeUpdateOptions options, IUpdateApplier applier) =>
        new(
            options,
            new TrustedUpdateKey("preview-key", key.ExportSubjectPublicKeyInfo()),
            new FakeFetcher(Envelope("0.1.2", Package, platform: "windows", extension: "zip")),
            new FakeDownloader(Package),
            applier,
            NullLogger<NodeUpdateAgent>.Instance);

    [Fact]
    public void FetcherRejectsNonHttpsAndMalformedRepository()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new GitHubPreviewManifestFetcher(client, new Uri("http://api.github.com/"), "o/r"));
        Assert.Throws<ArgumentException>(() => new GitHubPreviewManifestFetcher(client, new Uri("https://api.github.com/"), "o/r/../x"));
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

    private NodeUpdateOptions Options(NodeUpdatePolicy policy, TimeSpan interval, string packageKind = "deb", string? installRoot = null) =>
        new(
            policy,
            "owner/repo",
            new Uri("https://api.github.com/"),
            "preview",
            interval,
            Path.Combine(directory, "update"),
            Path.Combine(directory, "update-key.json"),
            packageKind,
            ReleaseVersion.Parse("0.1.1"),
            TimeSpan.FromSeconds(1),
            installRoot);

    private NodeUpdateAgent Create(
        NodeUpdatePolicy policy,
        byte[] envelope,
        byte[] download,
        IUpdateApplier applier,
        TimeSpan? interval = null) =>
        new(
            Options(policy, interval ?? TimeSpan.FromHours(6)),
            new TrustedUpdateKey("preview-key", key.ExportSubjectPublicKeyInfo()),
            new FakeFetcher(envelope),
            new FakeDownloader(download),
            applier,
            NullLogger<NodeUpdateAgent>.Instance);

    private byte[] Envelope(string version, byte[] artifact, ECDsa? signer = null, string platform = "ubuntu", string extension = "deb") =>
        UpdateManifestCodec.CreateSignedEnvelope(
            new UpdateManifest(
                UpdateManifestCodec.CurrentSchemaVersion,
                "preview",
                ReleaseVersion.Parse(version),
                ReleaseVersion.Parse("0.1.0"),
                new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
                [
                    new UpdateArtifact(
                        "node",
                        platform,
                        "x64",
                        new Uri($"https://github.com/o/r/releases/download/preview-{version}/monitorapp-node-{platform}-x64-{version}.{extension}"),
                        artifact.Length,
                        Convert.ToHexStringLower(SHA256.HashData(artifact))),
                ]),
            "preview-key",
            signer ?? key);

    private sealed class FakeFetcher(byte[] envelope) : IManifestFetcher
    {
        public Task<byte[]> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(envelope);
    }

    private sealed class FakeDownloader(byte[] content) : IArtifactDownloader
    {
        public Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken) =>
            destination.WriteAsync(content, cancellationToken).AsTask();
    }

    private sealed class RecordingApplier(ApplyOutcomeKind kind = ApplyOutcomeKind.Applied) : IUpdateApplier
    {
        public int Calls { get; private set; }

        public Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ApplyOutcome(kind, kind.ToString()));
        }
    }
}
