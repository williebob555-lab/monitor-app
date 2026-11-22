using MonitorApp.Update.Apply;

namespace MonitorApp.Update.Tests;

internal sealed class ScriptedRunner(Func<string, IReadOnlyList<string>, ProcessResult> script) : IProcessRunner
{
    public List<(string File, string[] Args)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, [.. arguments]));
        return Task.FromResult(script(fileName, arguments));
    }
}

internal sealed class FakeHealth(Func<ReleaseVersion, bool> healthy) : IHealthProbe
{
    public List<ReleaseVersion> Asked { get; } = [];

    public Task<bool> WaitForHealthyAsync(ReleaseVersion expectedVersion, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Asked.Add(expectedVersion);
        return Task.FromResult(healthy(expectedVersion));
    }
}

public sealed class LinuxPackageApplierTests
{
    private static readonly ReleaseVersion Old = ReleaseVersion.Parse("1.2.0");
    private static readonly ReleaseVersion New = ReleaseVersion.Parse("1.3.0");
    private static readonly ProcessResult Ok = new(0, "", "");

    private static (LinuxPackageApplier Applier, string Staged, string Lkg, TempDir Dir) Setup(
        IProcessRunner runner, IHealthProbe health, LinuxPackageKind kind = LinuxPackageKind.Deb, bool seedLkg = true, ServiceCommand? restart = null)
    {
        var dir = new TempDir();
        var applier = new LinuxPackageApplier(runner, health, new LinuxPackageOptions(kind, dir.Combine("retained"), restart));
        var lkg = dir.Combine("old.pkg");
        File.WriteAllText(lkg, "old");
        if (seedLkg)
        {
            applier.Retained.Promote(lkg, Old);
        }

        var staged = dir.Combine("new.pkg");
        File.WriteAllText(staged, "new");
        return (applier, staged, lkg, dir);
    }

    private static ApplyRequest Request(string staged) => new(staged, New, Old, TimeSpan.FromSeconds(1));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HealthyInstallPromotesNewPackageAndPrunesOldOneAsync()
    {
        var runner = new ScriptedRunner((_, _) => Ok);
        var (applier, staged, _, dir) = Setup(runner, new FakeHealth(_ => true), restart: new ServiceCommand("systemctl", ["restart", "monitorapp"]));
        using (dir)
        {
            var outcome = await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.Applied, outcome.Kind);
            Assert.Equal(["dpkg", "systemctl"], runner.Calls.Select(c => c.File));
            Assert.Equal(["-i", staged], runner.Calls[0].Args);
            var retained = applier.Retained.Get()!.Value;
            Assert.Equal(New, retained.Version);
            Assert.Equal("new", File.ReadAllText(retained.Path));
            Assert.Single(Directory.EnumerateFiles(dir.Combine("retained"), "lkg-*.pkg"));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnhealthyVersionIsRolledBackToRetainedPackageWhichStaysUntilHealthyAsync()
    {
        string? lkgPathAtRollback = null;
        LinuxPackageApplier? applierRef = null;
        var runner = new ScriptedRunner((_, args) =>
        {
            if (args[^1].EndsWith("lkg-1.2.0.pkg", StringComparison.Ordinal))
            {
                lkgPathAtRollback = args[^1];
                Assert.True(File.Exists(args[^1]));
            }

            return Ok;
        });
        var health = new FakeHealth(v => v == Old);
        var (applier, staged, _, dir) = Setup(runner, health);
        applierRef = applier;
        using (dir)
        {
            var outcome = await applierRef.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.RolledBack, outcome.Kind);
            Assert.Equal([New, Old], health.Asked);
            Assert.NotNull(lkgPathAtRollback);
            Assert.Equal(Old, applier.Retained.Get()!.Value.Version);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedInstallCommandTriggersRollbackAsync()
    {
        var installs = 0;
        var runner = new ScriptedRunner((_, _) => ++installs == 1 ? new ProcessResult(1, "", "dependency problems") : Ok);
        var (applier, staged, _, dir) = Setup(runner, new FakeHealth(_ => true));
        using (dir)
        {
            var outcome = await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.RolledBack, outcome.Kind);
            Assert.Contains("dependency problems", outcome.Detail, StringComparison.Ordinal);
            Assert.Equal(2, runner.Calls.Count);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoRetainedPackageMeansExplicitFailedAsync()
    {
        var (applier, staged, _, dir) = Setup(new ScriptedRunner((_, _) => Ok), new FakeHealth(_ => false), seedLkg: false);
        using (dir)
        {
            var outcome = await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
            Assert.Contains("manual recovery", outcome.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RetainedPackageForADifferentVersionIsNotUsedForRollbackAsync()
    {
        var runner = new ScriptedRunner((_, _) => Ok);
        var (applier, staged, lkg, dir) = Setup(runner, new FakeHealth(_ => false), seedLkg: false);
        using (dir)
        {
            applier.Retained.Promote(lkg, ReleaseVersion.Parse("1.1.0"));
            var outcome = await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
            Assert.Single(runner.Calls);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FailedRollbackIsReportedAsFailedAndKeepsRetainedPackageAsync()
    {
        var (applier, staged, _, dir) = Setup(new ScriptedRunner((_, _) => Ok), new FakeHealth(_ => false));
        using (dir)
        {
            var outcome = await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(ApplyOutcomeKind.Failed, outcome.Kind);
            Assert.Contains("Rollback also failed", outcome.Detail, StringComparison.Ordinal);
            Assert.True(File.Exists(applier.Retained.Get()!.Value.Path));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RpmUsesUpgradeAndOldPackageForRollbackAsync()
    {
        var runner = new ScriptedRunner((_, _) => Ok);
        var (applier, staged, _, dir) = Setup(runner, new FakeHealth(v => v == Old), LinuxPackageKind.Rpm);
        using (dir)
        {
            await applier.ApplyAsync(Request(staged), default);
            Assert.Equal(["-U", staged], runner.Calls[0].Args);
            Assert.Equal("--oldpackage", runner.Calls[1].Args[1]);
            Assert.All(runner.Calls, c => Assert.Equal("rpm", c.File));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HealthMarkerMustBeFreshAndMatchVersionAsync()
    {
        using var dir = new TempDir();
        var marker = dir.Combine("healthy.json");
        var probe = new FileHealthProbe(marker, TimeSpan.FromMilliseconds(20));
        var before = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(marker, "{\"version\":\"1.3.0\"}");

        Assert.True(await probe.WaitForHealthyAsync(New, before.AddSeconds(-1), TimeSpan.FromSeconds(1), default));
        Assert.False(await probe.WaitForHealthyAsync(Old, before.AddSeconds(-1), TimeSpan.FromMilliseconds(100), default));
        // A marker older than the restart must not count.
        Assert.False(await probe.WaitForHealthyAsync(New, DateTimeOffset.UtcNow.AddMinutes(1), TimeSpan.FromMilliseconds(100), default));
        await File.WriteAllTextAsync(marker, "garbage");
        Assert.False(await probe.WaitForHealthyAsync(New, before.AddSeconds(-1), TimeSpan.FromMilliseconds(100), default));
    }
}
