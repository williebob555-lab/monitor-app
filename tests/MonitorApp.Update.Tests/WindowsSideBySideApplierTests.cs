using System.IO.Compression;
using MonitorApp.Update.Apply;

namespace MonitorApp.Update.Tests;

public sealed class WindowsSideBySideApplierTests
{
    private static readonly ReleaseVersion Old = ReleaseVersion.Parse("1.2.0");
    private static readonly ReleaseVersion New = ReleaseVersion.Parse("1.3.0");

    private sealed class RecordingService : IServiceController
    {
        public List<string> Events { get; } = [];

        public Func<string?> CurrentVersionAtStart { get; set; } = () => null;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Events.Add("start:" + CurrentVersionAtStart());
            return Task.CompletedTask;
        }
    }

    private static string MakeZip(string path, string content, string entry = "peer.txt")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var e = archive.CreateEntry(entry);
        using var writer = new StreamWriter(e.Open());
        writer.Write(content);
        return path;
    }

    private static (VersionedInstallLayout Layout, RecordingService Service) Install(TempDir dir)
    {
        var layout = new VersionedInstallLayout(dir.Combine("install"));
        Directory.CreateDirectory(layout.VersionDirectory(Old));
        File.WriteAllText(Path.Combine(layout.VersionDirectory(Old), "peer.txt"), "old");
        layout.SetCurrent(Old);
        var service = new RecordingService();
        service.CurrentVersionAtStart = () => layout.GetCurrentVersion()?.ToString();
        return (layout, service);
    }

    private static ApplyRequest Request(string zip) => new(zip, New, Old, TimeSpan.FromSeconds(1));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HealthySwapPointsCurrentAtNewVersionAndPrunesOlderOnesAsync()
    {
        using var dir = new TempDir();
        var (layout, service) = Install(dir);
        Directory.CreateDirectory(layout.VersionDirectory(ReleaseVersion.Parse("1.0.0")));
        var applier = new WindowsSideBySideApplier(layout, service, new FakeHealth(v => v == New));

        var outcome = await applier.ApplyAsync(Request(MakeZip(dir.Combine("n.zip"), "new")), default);

        Assert.Equal(ApplyOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(New, layout.GetCurrentVersion());
        Assert.Equal("new", File.ReadAllText(Path.Combine(layout.CurrentLink, "peer.txt")));
        Assert.Equal(["stop", "start:1.3.0"], service.Events);
        Assert.True(Directory.Exists(layout.VersionDirectory(Old)));
        Assert.False(Directory.Exists(layout.VersionDirectory(ReleaseVersion.Parse("1.0.0"))));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnhealthyNewVersionSwapsBackAndKeepsPreviousDirectoryAsync()
    {
        using var dir = new TempDir();
        var (layout, service) = Install(dir);
        var applier = new WindowsSideBySideApplier(layout, service, new FakeHealth(v => v == Old));

        var outcome = await applier.ApplyAsync(Request(MakeZip(dir.Combine("n.zip"), "new")), default);

        Assert.Equal(ApplyOutcomeKind.RolledBack, outcome.Kind);
        Assert.Equal(Old, layout.GetCurrentVersion());
        Assert.Equal("old", File.ReadAllText(Path.Combine(layout.CurrentLink, "peer.txt")));
        Assert.Equal(["stop", "start:1.3.0", "stop", "start:1.2.0"], service.Events);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BothVersionsUnhealthyIsExplicitFailedAsync()
    {
        using var dir = new TempDir();
        var (layout, service) = Install(dir);
        var outcome = await new WindowsSideBySideApplier(layout, service, new FakeHealth(_ => false))
            .ApplyAsync(Request(MakeZip(dir.Combine("n.zip"), "new")), default);
        Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("Rollback also failed", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ZipSlipEntryIsRejectedWithoutTouchingTheRunningInstallAsync()
    {
        using var dir = new TempDir();
        var (layout, service) = Install(dir);
        var zip = MakeZip(dir.Combine("evil.zip"), "pwned", "../../escaped.txt");

        var outcome = await new WindowsSideBySideApplier(layout, service, new FakeHealth(_ => true)).ApplyAsync(Request(zip), default);

        Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
        Assert.Empty(service.Events);
        Assert.Equal(Old, layout.GetCurrentVersion());
        Assert.False(File.Exists(Path.Combine(dir.Path, "install", "escaped.txt")));
        Assert.False(Directory.Exists(layout.VersionDirectory(New)));
        Assert.Empty(Directory.EnumerateDirectories(layout.VersionsDirectory, ".staging-*"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScExeControllerIssuesStopAndStartAndWaitsForStateAsync()
    {
        var runner = new ScriptedRunner((_, args) => new ProcessResult(0, args[0] == "query" ? "STATE : 4  RUNNING STOPPED" : "", ""));
        var controller = new ScExeServiceController(runner, "MonitorAppPeer", TimeSpan.FromSeconds(5));
        await controller.StopAsync(default);
        await controller.StartAsync(default);
        Assert.Equal(["stop", "query", "start", "query"], runner.Calls.Select(c => c.Args[0]));
        Assert.All(runner.Calls, c => Assert.Equal("sc.exe", c.File));
    }
}
