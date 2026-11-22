using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MonitorApp.Peer.Node.Update;
using MonitorApp.Update;
using MonitorApp.Update.Apply;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

/// <summary>
/// The Windows apply helper and its request/result handshake. Everything except the service control and Task Scheduler
/// launch runs the real production code on real directories, signed manifests, zips and directory symlinks, so it is
/// portable; the sc.exe/schtasks command shapes are exercised by the Windows-category tests below.
/// </summary>
public sealed class WindowsApplyHelperTests : IDisposable
{
    private static readonly ReleaseVersion Old = ReleaseVersion.Parse("0.0.1");
    private static readonly ReleaseVersion New = ReleaseVersion.Parse("1.0.5");
    private readonly string root = Path.Combine(Path.GetTempPath(), "monitor-win-helper-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string InstallRoot => Path.Combine(root, "install");

    private string UpdateDirectory => Path.Combine(root, "data", "update");

    private string KeyFile => Path.Combine(root, "update-key.json");

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

    [Fact]
    public async Task HealthyUpdateSwitchesCurrentAndRecordsAppliedResult()
    {
        var service = Prepare();

        var exit = await Run(service);

        Assert.Equal(0, exit);
        Assert.Equal(["stop", "start:1.0.5"], service.Events);
        Assert.Equal("new", File.ReadAllText(Path.Combine(InstallRoot, "current", "node", "app.txt")));
        Assert.Equal(ApplyOutcomeKind.Applied, ApplyHelper.TryReadResult(Path.Combine(UpdateDirectory, "apply-result.json"))!.Kind);
        Assert.False(File.Exists(Path.Combine(UpdateDirectory, "apply-request.json")));
        Assert.True(Directory.Exists(Path.Combine(InstallRoot, "versions", Old.ToString())));
    }

    [Fact]
    public async Task UnhealthyNewVersionRollsBackToThePreviousVersion()
    {
        var service = Prepare(healthyVersion: Old);

        var exit = await Run(service);

        Assert.Equal(1, exit);
        Assert.Equal(["stop", "start:1.0.5", "stop", "start:0.0.1"], service.Events);
        Assert.Equal("old", File.ReadAllText(Path.Combine(InstallRoot, "current", "node", "app.txt")));
        Assert.Equal(ApplyOutcomeKind.RolledBack, ApplyHelper.TryReadResult(Path.Combine(UpdateDirectory, "apply-result.json"))!.Kind);
    }

    [Fact]
    public async Task TamperedZipIsRefusedBeforeTheServiceIsTouched()
    {
        var service = Prepare(stagedContent: "tampered");

        Assert.Equal(1, await Run(service));
        Assert.Empty(service.Events);
        Assert.Equal("old", File.ReadAllText(Path.Combine(InstallRoot, "current", "node", "app.txt")));
    }

    [Fact]
    public async Task StagedPackageOutsideTheUpdateDirectoryIsRefused()
    {
        var service = Prepare();
        var outside = Path.Combine(root, "elsewhere.zip");
        File.Copy(Path.Combine(UpdateDirectory, "staged.zip"), outside);
        WriteRequest(New.ToString(), outside);

        Assert.Equal(1, await Run(service));
        Assert.Empty(service.Events);
        Assert.Contains("not inside the update directory", ApplyHelper.TryReadResult(Path.Combine(UpdateDirectory, "apply-result.json"))!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRootWithoutACurrentLinkFailsClosed()
    {
        var service = Prepare();
        Directory.Delete(Path.Combine(InstallRoot, "current"));

        Assert.Equal(1, await Run(service));
        Assert.Empty(service.Events);
        Assert.Contains("not a versioned install", ApplyHelper.TryReadResult(Path.Combine(UpdateDirectory, "apply-result.json"))!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandoffApplierAndHelperCompleteTheRequestResultHandshake()
    {
        var service = Prepare();
        var options = Options(UpdateDirectory);
        Task<int>? helper = null;
        var applier = new ApplyHelper.HandoffApplier(options, _ =>
        {
            helper = Task.Run(() => Run(service));
            return Task.CompletedTask;
        });

        var outcome = await applier.ApplyAsync(new ApplyRequest(Path.Combine(UpdateDirectory, "staged.zip"), New, Old, TimeSpan.FromSeconds(2)), default);

        Assert.Equal(ApplyOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(0, await helper!);
        Assert.Equal(New, new VersionedInstallLayout(InstallRoot).GetCurrentVersion());
    }

    [Fact]
    public async Task HandoffApplierReportsAFailedLaunchAndWithdrawsTheRequest()
    {
        Prepare();
        var options = Options(UpdateDirectory);
        var applier = new ApplyHelper.HandoffApplier(options, _ => throw new InvalidOperationException("task not found"));

        var outcome = await applier.ApplyAsync(new ApplyRequest(Path.Combine(UpdateDirectory, "staged.zip"), New, Old, TimeSpan.FromSeconds(2)), default);

        Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("task not found", outcome.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(options.RequestPath));
    }

    [Fact]
    public async Task LaunchRunsTheRegisteredTaskAndSurfacesFailure()
    {
        var ok = new CapturingRunner(0);
        await WindowsApplyHelper.LaunchAsync(ok, default);
        Assert.Equal("schtasks.exe", ok.FileName);
        Assert.Equal(["/Run", "/TN", "MonitorAppUpdate"], ok.Arguments);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsApplyHelper.LaunchAsync(new CapturingRunner(1), default));
    }

    [Fact]
    public void HelperIsDetectedOnlyWhenTheAdminMarkerAndVersionsDirectoryExist()
    {
        Assert.False(WindowsApplyHelper.IsInstalled(null));
        Assert.False(WindowsApplyHelper.IsInstalled(InstallRoot));
        Directory.CreateDirectory(Path.Combine(InstallRoot, "versions"));
        Assert.False(WindowsApplyHelper.IsInstalled(InstallRoot));
        File.WriteAllText(Path.Combine(InstallRoot, WindowsApplyHelper.MarkerFileName), "{}");
        Assert.True(WindowsApplyHelper.IsInstalled(InstallRoot));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task ScExeReportsAMissingServiceInsteadOfPretendingItStopped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controller = new ScExeServiceController(new SystemProcessRunner(), "MonitorAppDoesNotExist-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromSeconds(10));

        // sc.exe exit 1060 (service does not exist) must not be mistaken for "already stopped".
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync(default));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task SchtasksReportsAMissingTaskAsALaunchFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The helper task is not registered on a CI runner; the real schtasks.exe must fail and LaunchAsync must surface it.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsApplyHelper.LaunchAsync(new SystemProcessRunner(), default));
        Assert.Contains("schtasks", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task RealWindowsDirectorySymlinkSwapWorksThroughTheHelperFlow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var service = Prepare();
        Assert.Equal(0, await Run(service));
        Assert.Equal(New, new VersionedInstallLayout(InstallRoot).GetCurrentVersion());
        Assert.Equal("new", File.ReadAllText(Path.Combine(InstallRoot, "current", "node", "app.txt")));
    }

    private NodeUpdateOptions Options(string updateDirectory) =>
        new(
            NodeUpdatePolicy.Approve,
            "o/r",
            new Uri("https://api.github.com/"),
            "preview",
            TimeSpan.FromHours(1),
            updateDirectory,
            KeyFile,
            "zip",
            Old,
            TimeSpan.FromSeconds(2),
            InstallRoot);

    private Task<int> Run(FakeService service) =>
        WindowsApplyHelper.RunAsync(UpdateDirectory, InstallRoot, KeyFile, "preview", service, TimeSpan.FromSeconds(2));

    private void WriteRequest(string version, string stagedPath) =>
        File.WriteAllText(
            Path.Combine(UpdateDirectory, "apply-request.json"),
            JsonSerializer.Serialize(new { TargetVersion = version, StagedPath = stagedPath }));

    /// <summary>Lays out an installed 0.0.1, a signed manifest for 1.0.5 and a staged zip.</summary>
    private FakeService Prepare(ReleaseVersion? healthyVersion = null, string? stagedContent = null)
    {
        Directory.CreateDirectory(UpdateDirectory);
        var layout = new VersionedInstallLayout(InstallRoot);
        var oldNode = Path.Combine(layout.VersionDirectory(Old), "node");
        Directory.CreateDirectory(oldNode);
        File.WriteAllText(Path.Combine(oldNode, "app.txt"), "old");
        layout.SetCurrent(Old);

        var zip = Path.Combine(UpdateDirectory, "staged.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("node/app.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("new");
        }

        var signedBytes = File.ReadAllBytes(zip);
        if (stagedContent is not null)
        {
            File.WriteAllText(zip, stagedContent);
        }

        File.WriteAllText(
            KeyFile,
            JsonSerializer.Serialize(new { keyId = "k", publicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), repository = "o/r" }));
        File.WriteAllBytes(
            Path.Combine(UpdateDirectory, "manifest.json"),
            UpdateManifestCodec.CreateSignedEnvelope(
                new UpdateManifest(
                    UpdateManifestCodec.CurrentSchemaVersion,
                    "preview",
                    New,
                    ReleaseVersion.Parse("0.0.1"),
                    new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
                    [
                        new UpdateArtifact(
                            "node",
                            "windows",
                            "x64",
                            new Uri("https://github.com/o/r/releases/download/x/monitorapp-node-windows-x64-1.0.5.zip"),
                            signedBytes.Length,
                            Convert.ToHexStringLower(SHA256.HashData(signedBytes))),
                    ]),
                "k",
                key));
        WriteRequest(New.ToString(), zip);
        return new FakeService(layout, UpdateDirectory, healthyVersion ?? New);
    }

    private sealed class CapturingRunner(int exitCode) : IProcessRunner
    {
        public string? FileName { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, "failed"));
        }
    }

    /// <summary>Stands in for SCM and the restarted node: on start it writes the health marker for the version <c>current</c> points at, if that is the healthy one.</summary>
    private sealed class FakeService(VersionedInstallLayout layout, string updateDirectory, ReleaseVersion healthyVersion) : IServiceController
    {
        public List<string> Events { get; } = [];

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var version = layout.GetCurrentVersion();
            Events.Add("start:" + version);
            if (version == healthyVersion)
            {
                // A real node needs seconds to boot; the delay also keeps the marker's mtime past the coarse filesystem clock.
                await Task.Delay(100, cancellationToken);
                File.WriteAllText(Path.Combine(updateDirectory, "health.json"), JsonSerializer.Serialize(new { version = version.ToString() }));
            }
        }
    }
}
