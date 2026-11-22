using MonitorApp.Update.Apply;

namespace MonitorApp.Update.Tests;

/// <summary>Real dpkg-deb and dpkg against a sandbox root (--root/--admindir). Skips (passes vacuously with a note) when dpkg is unusable.</summary>
public sealed class RealDpkgTests
{
    private sealed class SandboxHealth(string versionFile) : IHealthProbe
    {
        public Task<bool> WaitForHealthyAsync(ReleaseVersion expectedVersion, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == expectedVersion.ToString());
    }

    private static async Task<string> BuildDebAsync(SystemProcessRunner runner, string dir, string version, string installedContent)
    {
        var root = Path.Combine(dir, $"build-{version}-{installedContent}");
        Directory.CreateDirectory(Path.Combine(root, "DEBIAN"));
        Directory.CreateDirectory(Path.Combine(root, "opt", "monitorapp"));
        File.WriteAllText(
            Path.Combine(root, "DEBIAN", "control"),
            $"Package: monitorapp-test\nVersion: {version}\nArchitecture: all\nMaintainer: test <test@example.invalid>\nDescription: update test package\n");
        File.WriteAllText(Path.Combine(root, "opt", "monitorapp", "VERSION"), installedContent + "\n");
        var deb = Path.Combine(dir, $"monitorapp-test_{version}_{installedContent}.deb");
        var result = await runner.RunAsync("dpkg-deb", ["--root-owner-group", "--build", root, deb], TimeSpan.FromSeconds(30), default);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return deb;
    }

    private static bool DpkgAvailable() =>
        OperatingSystem.IsLinux() && File.Exists("/usr/bin/dpkg") && File.Exists("/usr/bin/dpkg-deb");

    private static string[] Sandbox(string root) =>
        [$"--root={root}", $"--admindir={Path.Combine(root, "var", "lib", "dpkg")}", "--force-not-root", "--force-bad-path", "--no-triggers"];

    private static void InitAdminDir(string root)
    {
        var admin = Path.Combine(root, "var", "lib", "dpkg");
        Directory.CreateDirectory(Path.Combine(admin, "info"));
        Directory.CreateDirectory(Path.Combine(admin, "updates"));
        File.WriteAllText(Path.Combine(admin, "status"), string.Empty);
        File.WriteAllText(Path.Combine(admin, "available"), string.Empty);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealDebUpgradeSucceedsAndUnhealthyUpgradeIsRolledBackAsync()
    {
        if (!DpkgAvailable())
        {
            return; // Reported as unverified in docs when dpkg is not usable on the runner.
        }

        using var dir = new TempDir();
        var runner = new SystemProcessRunner();
        var sandboxRoot = dir.Combine("root");
        InitAdminDir(sandboxRoot);
        var versionFile = Path.Combine(sandboxRoot, "opt", "monitorapp", "VERSION");

        var v120 = await BuildDebAsync(runner, dir.Path, "1.2.0", "1.2.0");
        var v130Good = await BuildDebAsync(runner, dir.Path, "1.3.0", "1.3.0");
        var v140Broken = await BuildDebAsync(runner, dir.Path, "1.4.0", "BROKEN");
        var options = new LinuxPackageOptions(LinuxPackageKind.Deb, dir.Combine("retained"), ExtraInstallArguments: Sandbox(sandboxRoot));
        var applier = new LinuxPackageApplier(runner, new SandboxHealth(versionFile), options);

        var install = await runner.RunAsync("dpkg", [.. Sandbox(sandboxRoot), "-i", v120], TimeSpan.FromSeconds(60), default);
        Assert.True(install.ExitCode == 0, install.StandardError);
        applier.Retained.Promote(v120, ReleaseVersion.Parse("1.2.0"));
        Assert.Equal("1.2.0", (await File.ReadAllTextAsync(versionFile)).Trim());

        var upgrade = await applier.ApplyAsync(
            new ApplyRequest(v130Good, ReleaseVersion.Parse("1.3.0"), ReleaseVersion.Parse("1.2.0"), TimeSpan.FromSeconds(1)), default);
        Assert.True(upgrade.Kind == ApplyOutcomeKind.Applied, upgrade.Detail);
        Assert.Equal("1.3.0", (await File.ReadAllTextAsync(versionFile)).Trim());
        Assert.Equal(ReleaseVersion.Parse("1.3.0"), applier.Retained.Get()!.Value.Version);

        var broken = await applier.ApplyAsync(
            new ApplyRequest(v140Broken, ReleaseVersion.Parse("1.4.0"), ReleaseVersion.Parse("1.3.0"), TimeSpan.FromSeconds(1)), default);
        Assert.True(broken.Kind == ApplyOutcomeKind.RolledBack, broken.Detail);
        Assert.Equal("1.3.0", (await File.ReadAllTextAsync(versionFile)).Trim());
        var status = await runner.RunAsync("dpkg-query", [$"--admindir={Path.Combine(sandboxRoot, "var", "lib", "dpkg")}", "-W", "-f=${Version}", "monitorapp-test"], TimeSpan.FromSeconds(30), default);
        Assert.Equal("1.3.0", status.StandardOutput);
        Assert.Equal(ReleaseVersion.Parse("1.3.0"), applier.Retained.Get()!.Value.Version);
    }
}
