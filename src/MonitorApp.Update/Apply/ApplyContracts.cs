using System.Diagnostics;
using System.Text.Json;

namespace MonitorApp.Update.Apply;

public sealed record ApplyRequest(
    string StagedArtifactPath,
    ReleaseVersion TargetVersion,
    ReleaseVersion PreviousVersion,
    TimeSpan HealthTimeout);

public enum ApplyOutcomeKind
{
    /// <summary>New version installed and reported healthy.</summary>
    Applied,

    /// <summary>New version was unhealthy or failed to install; previous version restored and healthy.</summary>
    RolledBack,

    /// <summary>Neither the new nor the previous version is known healthy. Operator action required.</summary>
    Failed,
}

public sealed record ApplyOutcome(ApplyOutcomeKind Kind, string Detail);

/// <summary>Platform strategy. Implementations must do health check and rollback themselves and never delete last-known-good before health success.</summary>
public interface IUpdateApplier
{
    Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Runs a process without a shell using an argument list; kills it on timeout.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Process did not start.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, string.Empty, "Process timed out and was killed.");
        }
    }
}

/// <summary>
/// The installed service proves it is running the expected version by (re)writing a marker file.
/// A marker only counts if it was written at or after <paramref name="since"/> so a stale marker
/// from before the restart cannot mask a dead service.
/// </summary>
public interface IHealthProbe
{
    Task<bool> WaitForHealthyAsync(
        ReleaseVersion expectedVersion,
        DateTimeOffset since,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Marker file contents: {"version":"1.2.3"}.</summary>
public sealed class FileHealthProbe : IHealthProbe
{
    private readonly string _markerPath;
    private readonly TimeSpan _pollInterval;

    public FileHealthProbe(string markerPath, TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        _markerPath = markerPath;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task<bool> WaitForHealthyAsync(
        ReleaseVersion expectedVersion,
        DateTimeOffset since,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (IsHealthy(expectedVersion, since))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsHealthy(ReleaseVersion expected, DateTimeOffset since)
    {
        try
        {
            if (!File.Exists(_markerPath) || File.GetLastWriteTimeUtc(_markerPath) < since.UtcDateTime)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(_markerPath));
            return document.RootElement.TryGetProperty("version", out var value)
                   && ReleaseVersion.TryParse(value.GetString(), out var reported)
                   && reported == expected;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
