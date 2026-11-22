using System.Security.Cryptography;
using System.Text.Json;
using MonitorApp.Peer.Node.Update;
using MonitorApp.Update;
using MonitorApp.Update.Apply;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class ApplyHelperTests : IDisposable
{
    private static readonly byte[] Package = "helper-package"u8.ToArray();
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-apply-helper-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task VerifiedRequestInstallsHealthChecksAndRecordsResult()
    {
        var runner = new HealthyRunner(root);
        var exit = await Run(Setup("1.0.5", Package, Package), runner);

        Assert.Equal(0, exit);
        Assert.Equal(["dpkg", "systemctl"], runner.Commands);
        var result = ApplyHelper.TryReadResult(Path.Combine(root, "update", "apply-result.json"));
        Assert.Equal(ApplyOutcomeKind.Applied, result!.Kind);
        Assert.False(File.Exists(Path.Combine(root, "update", "apply-request.json")));
    }

    [Fact]
    public async Task TamperedStagedPackageIsRefusedBeforeAnythingIsInstalled()
    {
        var runner = new HealthyRunner(root);
        var exit = await Run(Setup("1.0.5", Package, "tampered"u8.ToArray()), runner);

        Assert.Equal(1, exit);
        Assert.Empty(runner.Commands);
        Assert.Equal(ApplyOutcomeKind.Failed, ApplyHelper.TryReadResult(Path.Combine(root, "update", "apply-result.json"))!.Kind);
    }

    [Fact]
    public async Task ApprovedVersionThatDiffersFromTheSignedManifestIsRefused()
    {
        var runner = new HealthyRunner(root);
        var exit = await Run(Setup("1.0.5", Package, Package, requestVersion: "1.0.9"), runner);

        Assert.Equal(1, exit);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task MissingTrustAnchorFailsClosed()
    {
        var runner = new HealthyRunner(root);
        var request = Setup("1.0.5", Package, Package);
        File.Delete(Path.Combine(root, "update-key.json"));

        Assert.Equal(1, await Run(request, runner));
        Assert.Empty(runner.Commands);
    }

    public void Dispose()
    {
        key.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Setup(string version, byte[] signedPackage, byte[] stagedPackage, string? requestVersion = null)
    {
        var update = Path.Combine(root, "update");
        Directory.CreateDirectory(update);
        File.WriteAllText(
            Path.Combine(root, "update-key.json"),
            JsonSerializer.Serialize(new { keyId = "k", publicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), repository = "o/r" }));
        File.WriteAllBytes(
            Path.Combine(update, "manifest.json"),
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
                            new Uri("https://github.com/o/r/releases/download/x/monitorapp-node-ubuntu-x64-" + version + ".deb"),
                            signedPackage.Length,
                            Convert.ToHexStringLower(SHA256.HashData(signedPackage))),
                    ]),
                "k",
                key));
        var staged = Path.Combine(update, "staged.deb");
        File.WriteAllBytes(staged, stagedPackage);
        File.WriteAllText(
            Path.Combine(update, "apply-request.json"),
            JsonSerializer.Serialize(new { TargetVersion = requestVersion ?? version, StagedPath = staged }));
        return update;
    }

    private Task<int> Run(string updateDirectory, IProcessRunner runner) =>
        ApplyHelper.RunHelperAsync(
            updateDirectory,
            Path.Combine(root, "update-key.json"),
            Path.Combine(root, "work"),
            "deb",
            "preview",
            runner,
            TimeSpan.FromSeconds(5));

    private sealed class HealthyRunner(string root) : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(fileName);
            if (fileName == "systemctl")
            {
                // The restarted service proves health by rewriting the marker with the new version.
                await Task.Delay(100, cancellationToken);
                File.WriteAllText(Path.Combine(root, "update", "health.json"), "{\"version\":\"1.0.5\"}");
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
