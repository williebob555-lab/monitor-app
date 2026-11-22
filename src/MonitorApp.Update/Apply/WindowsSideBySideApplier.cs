using System.IO.Compression;

namespace MonitorApp.Update.Apply;

public interface IServiceController
{
    Task StopAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Windows Service control through sc.exe. UNVERIFIED on Windows: only the command shape is unit-tested
/// with a fake runner. A real Windows service must be exercised before release.
/// </summary>
public sealed class ScExeServiceController : IServiceController
{
    private readonly IProcessRunner _runner;
    private readonly string _serviceName;
    private readonly TimeSpan _timeout;

    public ScExeServiceController(IProcessRunner runner, string serviceName, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _runner = runner;
        _serviceName = serviceName;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    public Task StopAsync(CancellationToken cancellationToken) => RunAsync("stop", "STOPPED", cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync("start", "RUNNING", cancellationToken);

    private async Task RunAsync(string verb, string finalState, CancellationToken cancellationToken)
    {
        var command = await _runner.RunAsync("sc.exe", [verb, _serviceName], _timeout, cancellationToken).ConfigureAwait(false);
        // 1062 = service not started (stop), 1056 = already running (start): both already in the desired state.
        if (command.ExitCode is not (0 or 1062 or 1056))
        {
            throw new InvalidOperationException($"sc.exe {verb} {_serviceName} exited {command.ExitCode}.");
        }

        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var query = await _runner.RunAsync("sc.exe", ["query", _serviceName], _timeout, cancellationToken).ConfigureAwait(false);
            if (query.StandardOutput.Contains(finalState, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Service {_serviceName} did not reach {finalState}.");
    }
}

/// <summary>
/// root/versions/&lt;version&gt;/ holds side-by-side installs; root/current is a directory symlink to the
/// active one. The service always launches from root/current. Swapping happens only while it is stopped.
/// </summary>
public sealed class VersionedInstallLayout
{
    public VersionedInstallLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(VersionsDirectory);
    }

    public string Root { get; }

    public string VersionsDirectory => Path.Combine(Root, "versions");

    public string CurrentLink => Path.Combine(Root, "current");

    public string VersionDirectory(ReleaseVersion version) => Path.Combine(VersionsDirectory, version.ToString());

    public ReleaseVersion? GetCurrentVersion()
    {
        var target = new DirectoryInfo(CurrentLink).LinkTarget;
        return target is not null && ReleaseVersion.TryParse(Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)), out var version)
            ? version
            : null;
    }

    /// <summary>Extract a zip into a staging directory then rename it into place. ZipFile rejects entries escaping the destination.</summary>
    public void InstallFromZip(string zipPath, ReleaseVersion version)
    {
        var final = VersionDirectory(version);
        if (GetCurrentVersion() == version)
        {
            throw new InvalidOperationException("Refusing to overwrite the active version directory.");
        }

        var staging = Path.Combine(VersionsDirectory, $".staging-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            if (Directory.Exists(final))
            {
                Directory.Delete(final, recursive: true);
            }

            Directory.Move(staging, final);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    public void SetCurrent(ReleaseVersion version)
    {
        var target = VersionDirectory(version);
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Version directory for {version} does not exist.");
        }

        var temp = CurrentLink + ".new-" + Guid.NewGuid().ToString("N");
        Directory.CreateSymbolicLink(temp, target);
        if (new DirectoryInfo(CurrentLink).LinkTarget is not null)
        {
            Directory.Delete(CurrentLink);
        }

        Directory.Move(temp, CurrentLink);
    }

    /// <summary>Delete every version directory except those in <paramref name="keep"/>. Call only after health success.</summary>
    public void Prune(params ReleaseVersion[] keep)
    {
        var keepNames = keep.Select(v => v.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(VersionsDirectory))
        {
            if (!keepNames.Contains(Path.GetFileName(dir)))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

/// <summary>Stop service, swap pointer, start, health-check; swap back on failure. Windows execution UNVERIFIED.</summary>
public sealed class WindowsSideBySideApplier : IUpdateApplier
{
    private readonly VersionedInstallLayout _layout;
    private readonly IServiceController _service;
    private readonly IHealthProbe _health;
    private readonly TimeProvider _time;

    public WindowsSideBySideApplier(
        VersionedInstallLayout layout,
        IServiceController service,
        IHealthProbe health,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(health);
        _layout = layout;
        _service = service;
        _health = health;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previous = _layout.GetCurrentVersion() ?? request.PreviousVersion;
        var previousAvailable = Directory.Exists(_layout.VersionDirectory(previous));

        try
        {
            _layout.InstallFromZip(request.StagedArtifactPath, request.TargetVersion);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing was swapped; the running version is untouched.
            return new ApplyOutcome(ApplyOutcomeKind.Failed, $"Could not unpack {request.TargetVersion}; the current install was not touched: {ex.Message}");
        }

        var failure = await SwitchAndVerifyAsync(request.TargetVersion, request.HealthTimeout, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            try
            {
                _layout.Prune(request.TargetVersion, previous);
            }
            catch (IOException)
            {
                // Pruning is best-effort; leftover directories are harmless.
            }

            return new ApplyOutcome(ApplyOutcomeKind.Applied, $"Switched to {request.TargetVersion} and it reported healthy.");
        }

        if (!previousAvailable)
        {
            return new ApplyOutcome(ApplyOutcomeKind.Failed, $"{failure} Previous version {previous} is not retained; manual recovery required.");
        }

        var rollbackFailure = await SwitchAndVerifyAsync(previous, request.HealthTimeout, cancellationToken).ConfigureAwait(false);
        return rollbackFailure is null
            ? new ApplyOutcome(ApplyOutcomeKind.RolledBack, $"{failure} Restored {previous} and it reported healthy.")
            : new ApplyOutcome(ApplyOutcomeKind.Failed, $"{failure} Rollback also failed: {rollbackFailure}");
    }

    private async Task<string?> SwitchAndVerifyAsync(ReleaseVersion version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _service.StopAsync(cancellationToken).ConfigureAwait(false);
            _layout.SetCurrent(version);
            var started = _time.GetUtcNow();
            await _service.StartAsync(cancellationToken).ConfigureAwait(false);
            return await _health.WaitForHealthyAsync(version, started, timeout, cancellationToken).ConfigureAwait(false)
                ? null
                : $"Version {version} did not report healthy within {timeout}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return $"Switching to {version} failed: {ex.Message}";
        }
    }
}
