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
    public async Task StartupWritesTheHealthMarkerWithTheInstalledVersion()
    {
        using var agent = Create(NodeUpdatePolicy.Approve, Envelope("0.1.2", Package), Package, new RecordingApplier(), interval: TimeSpan.FromHours(1));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await agent.StartAsync(default);
        await Task.Delay(300);
        await agent.StopAsync(default);

        var marker = Path.Combine(directory, "update", "health.json");
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
        await Task.Delay(300);
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

    private NodeUpdateOptions Options(NodeUpdatePolicy policy, TimeSpan interval) =>
        new(
            policy,
            "owner/repo",
            new Uri("https://api.github.com/"),
            "preview",
            interval,
            Path.Combine(directory, "update"),
            Path.Combine(directory, "update-key.json"),
            "deb",
            ReleaseVersion.Parse("0.1.1"),
            TimeSpan.FromSeconds(1));

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

    private byte[] Envelope(string version, byte[] artifact, ECDsa? signer = null) =>
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
                        "ubuntu",
                        "x64",
                        new Uri($"https://github.com/o/r/releases/download/preview-{version}/monitorapp-node-ubuntu-x64-{version}.deb"),
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
