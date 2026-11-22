using System.Text.Json;

namespace MonitorApp.Update.Apply;

public enum LinuxPackageKind
{
    Deb,
    Rpm,
}

public sealed record ServiceCommand(string FileName, IReadOnlyList<string> Arguments);

public sealed record LinuxPackageOptions(
    LinuxPackageKind Kind,
    string RetentionDirectory,
    ServiceCommand? RestartCommand = null,
    IReadOnlyList<string>? ExtraInstallArguments = null,
    TimeSpan? CommandTimeout = null);

/// <summary>Keeps the last-known-good package file. Only replaced after a new version proves healthy.</summary>
public sealed class RetainedPackageStore
{
    private sealed record Pointer(string Version, string File);

    private readonly string _directory;

    public RetainedPackageStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PointerPath => Path.Combine(_directory, "last-known-good.json");

    public (string Path, ReleaseVersion Version)? Get()
    {
        if (!File.Exists(PointerPath))
        {
            return null;
        }

        try
        {
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(PointerPath));
            var path = pointer is null ? null : Path.Combine(_directory, Path.GetFileName(pointer.File));
            return pointer is not null && path is not null && File.Exists(path) && ReleaseVersion.TryParse(pointer.Version, out var version)
                ? (path, version)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Copy <paramref name="packagePath"/> in as the new last-known-good, then prune older copies.</summary>
    public void Promote(string packagePath, ReleaseVersion version)
    {
        var name = $"lkg-{version}.pkg";
        var final = Path.Combine(_directory, name);
        var temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(packagePath, temp, overwrite: true);
        File.Move(temp, final, overwrite: true);

        var pointerTemp = PointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(pointerTemp, JsonSerializer.SerializeToUtf8Bytes(new Pointer(version.ToString(), name)));
        File.Move(pointerTemp, PointerPath, overwrite: true);

        foreach (var other in Directory.EnumerateFiles(_directory, "lkg-*.pkg"))
        {
            if (!string.Equals(Path.GetFileName(other), name, StringComparison.Ordinal))
            {
                File.Delete(other);
            }
        }
    }
}

/// <summary>
/// dpkg/rpm strategy. Install the staged package, restart, wait for the health marker; on any failure
/// reinstall the retained previous package and require it to be healthy too.
/// </summary>
public sealed class LinuxPackageApplier : IUpdateApplier
{
    private readonly IProcessRunner _runner;
    private readonly IHealthProbe _health;
    private readonly LinuxPackageOptions _options;
    private readonly TimeProvider _time;

    public LinuxPackageApplier(
        IProcessRunner runner,
        IHealthProbe health,
        LinuxPackageOptions options,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(options);
        _runner = runner;
        _health = health;
        _options = options;
        _time = time ?? TimeProvider.System;
        Retained = new RetainedPackageStore(options.RetentionDirectory);
    }

    public RetainedPackageStore Retained { get; }

    public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lkg = Retained.Get();
        var usableLkg = lkg is { } known && known.Version == request.PreviousVersion ? lkg : null;

        var failure = await InstallAndVerifyAsync(request.StagedArtifactPath, request.TargetVersion, false, request.HealthTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (failure is null)
        {
            try
            {
                Retained.Promote(request.StagedArtifactPath, request.TargetVersion);
                return new ApplyOutcome(ApplyOutcomeKind.Applied, $"Installed {request.TargetVersion} and it reported healthy.");
            }
            catch (IOException ex)
            {
                return new ApplyOutcome(ApplyOutcomeKind.Applied, $"Installed {request.TargetVersion} (healthy) but could not retain the package for rollback: {ex.Message}");
            }
        }

        if (usableLkg is not { } good)
        {
            return new ApplyOutcome(ApplyOutcomeKind.Failed, $"{failure} No last-known-good package for {request.PreviousVersion} is retained; manual recovery required.");
        }

        var rollbackFailure = await InstallAndVerifyAsync(good.Path, good.Version, true, request.HealthTimeout, cancellationToken)
            .ConfigureAwait(false);
        return rollbackFailure is null
            ? new ApplyOutcome(ApplyOutcomeKind.RolledBack, $"{failure} Restored {good.Version} and it reported healthy.")
            : new ApplyOutcome(ApplyOutcomeKind.Failed, $"{failure} Rollback also failed: {rollbackFailure}");
    }

    private async Task<string?> InstallAndVerifyAsync(
        string package,
        ReleaseVersion expected,
        bool allowDowngrade,
        TimeSpan healthTimeout,
        CancellationToken cancellationToken)
    {
        var timeout = _options.CommandTimeout ?? TimeSpan.FromMinutes(5);
        var started = _time.GetUtcNow();
        var (file, args) = BuildInstall(package, allowDowngrade);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(file, args, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"Package install for {expected} could not run: {ex.Message}";
        }

        if (result.ExitCode != 0)
        {
            return $"Package install for {expected} exited {result.ExitCode}: {Trim(result.StandardError)}";
        }

        if (_options.RestartCommand is { } restart)
        {
            var restarted = await _runner.RunAsync(restart.FileName, restart.Arguments, timeout, cancellationToken).ConfigureAwait(false);
            if (restarted.ExitCode != 0)
            {
                return $"Service restart after installing {expected} exited {restarted.ExitCode}: {Trim(restarted.StandardError)}";
            }
        }

        return await _health.WaitForHealthyAsync(expected, started, healthTimeout, cancellationToken).ConfigureAwait(false)
            ? null
            : $"Version {expected} did not report healthy within {healthTimeout}.";
    }

    private (string File, IReadOnlyList<string> Args) BuildInstall(string package, bool allowDowngrade)
    {
        var args = new List<string>();
        string file;
        if (_options.Kind == LinuxPackageKind.Deb)
        {
            file = "dpkg";
            args.AddRange(_options.ExtraInstallArguments ?? []);
            args.Add("-i");
        }
        else
        {
            file = "rpm";
            args.Add("-U");
            if (allowDowngrade)
            {
                args.Add("--oldpackage");
            }

            args.AddRange(_options.ExtraInstallArguments ?? []);
        }

        args.Add(package);
        return (file, args);
    }

    private static string Trim(string value) => value.Length <= 500 ? value.Trim() : value[..500].Trim();
}
