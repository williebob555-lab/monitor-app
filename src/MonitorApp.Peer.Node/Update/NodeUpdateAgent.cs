using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Update;
using MonitorApp.Update.Apply;

namespace MonitorApp.Peer.Node.Update;

/// <summary>
/// Periodic update agent: fetch the latest preview manifest, verify its signature against the packaged public
/// key, stage the matching artifact, and then apply according to <see cref="NodeUpdatePolicy"/>. Verification
/// failures are recorded and never staged or applied. The agent also writes the health marker that the
/// installer-side health check waits for after a restart.
/// </summary>
public sealed partial class NodeUpdateAgent : BackgroundService, ILocalUpdateController, ILocalUpdateHistory
{
    private readonly NodeUpdateOptions _options;
    private readonly UpdateService? _service;
    private readonly RecordingFetcher? _fetcher;
    private readonly IUpdateApplier _applier;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _disabledReason;
    private readonly object _statusLock = new();
    private readonly object _approveLock = new();
    private DateTimeOffset? _lastChecked;
    private string? _lastError;
    private Task _applyTask = Task.CompletedTask;

    public NodeUpdateAgent(
        NodeUpdateOptions options,
        TrustedUpdateKey? key,
        IManifestFetcher? fetcher,
        IArtifactDownloader? downloader,
        IUpdateApplier applier,
        ILogger<NodeUpdateAgent> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _applier = applier;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _disabledReason = key is null || fetcher is null || downloader is null
            ? "Updates are not configured (no packaged update key/repository)."
            : string.Empty;
        if (_disabledReason.Length == 0)
        {
            _fetcher = new RecordingFetcher(fetcher!);
            _service = new UpdateService(
                new UpdateTarget(options.Channel, "node", options.Platform, "x64"),
                key!,
                _fetcher,
                downloader!,
                new UpdateStateStore(options.UpdateDirectory),
                options.StagingDirectory,
                _time);
        }
    }

    public override void Dispose()
    {
        _service?.Dispose();
        _stateGate.Dispose();
        base.Dispose();
    }

    public LocalUpdateStatus GetStatus()
    {
        UpdateState? state = null;
        if (_service is not null)
        {
            try
            {
                state = _service.Current;
            }
            catch (UpdateStateCorruptException)
            {
                lock (_statusLock)
                {
                    _lastError = "Update state is corrupt; refusing to reset anti-rollback state.";
                }
            }
        }

        lock (_statusLock)
        {
            return new LocalUpdateStatus(
                _options.Policy.ToString().ToLowerInvariant(),
                _options.InstalledVersion.ToString(),
                _service is null ? "Disabled" : state?.Stage.ToString() ?? "Idle",
                state?.TargetVersion,
                _service is null ? _disabledReason : state?.Detail,
                _lastChecked,
                _lastError,
                state is { Stage: UpdateStage.Staged } && CanApplyHere,
                state?.ArtifactSha256);
        }
    }

    /// <summary>Applied/RolledBack/Failed outcomes from the append-only audit log, oldest first.</summary>
    public IReadOnlyList<LocalUpdateEvent> GetUpdateHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var result = new List<LocalUpdateEvent>();
        IReadOnlyList<string> lines;
        try
        {
            lines = new UpdateStateStore(_options.UpdateDirectory).ReadAudit();
        }
        catch (IOException)
        {
            return result;
        }

        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var at = root.GetProperty("at").GetDateTimeOffset();
                var name = root.GetProperty("event").GetString() ?? string.Empty;
                var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (name == "reconciled" && detail is not null)
                {
                    // "<Stage>: <detail>" written when a restart settles an interrupted apply.
                    var split = detail.IndexOf(':', StringComparison.Ordinal);
                    name = split > 0 ? detail[..split].ToLowerInvariant() : name;
                    detail = split > 0 ? detail[(split + 1)..].Trim() : detail;
                }

                var outcome = name switch
                {
                    "applied" => "Applied",
                    "rolledback" => "RolledBack",
                    "failed" => "Failed",
                    _ => null,
                };
                if (outcome is not null && at >= fromUtc && at <= toUtc)
                {
                    result.Add(new LocalUpdateEvent(version ?? "unknown", outcome, at, detail));
                }
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A damaged audit line is skipped; the log stays append-only and is never rewritten here.
            }
        }

        return result.OrderBy(item => item.At).ToArray();
    }

    public Task<bool> ApproveAsync(CancellationToken cancellationToken) => ApproveCoreAsync(null, null, "local-api");

    /// <summary>Minimum spacing between checks the person asks for, so repeated presses cannot exhaust the release API's rate limit.</summary>
    public static readonly TimeSpan ManualCheckSpacing = TimeSpan.FromSeconds(30);

    public async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            if (_lastChecked is { } last && _time.GetUtcNow() - last < ManualCheckSpacing)
            {
                return;
            }
        }

        await RunCycleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approval bound to the exact staged version and artifact digest; the check and the start of the apply are atomic.</summary>
    public Task<bool> ApproveAsync(string version, string digestHex, CancellationToken cancellationToken) =>
        ApproveCoreAsync(version, digestHex, "remote-control");

    private async Task<bool> ApproveCoreAsync(string? version, string? digestHex, string approvedBy)
    {
        lock (_approveLock)
        {
            if (_service is null || _service.Current is not { Stage: UpdateStage.Staged } staged || !CanApplyHere ||
                (version is not null &&
                    (!string.Equals(staged.TargetVersion, version, StringComparison.Ordinal) ||
                     !string.Equals(staged.ArtifactSha256, digestHex, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            _applyTask = ApplyStagedAsync(approvedBy, cancellationToken: default);
        }

        await Task.Yield();
        return true;
    }

    /// <summary>Completes when no background apply is running (used by tests and shutdown).</summary>
    public Task WhenIdleAsync() => _applyTask;

    /// <summary>One check/stage/(auto-apply) pass. Never throws for expected network or verification failures.</summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return;
        }

        // A cycle briefly replaces the state with a transient check record; the helper-result watcher must not fold in between.
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunCycleCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_service!.Current is { Stage: UpdateStage.Applying })
            {
                return;
            }

            var priorFailure = _service.Current is { Stage: UpdateStage.Failed or UpdateStage.RolledBack } failed ? failed : null;
            var previousFailure = priorFailure?.TargetVersion;
            var available = await _service.CheckAsync(_options.InstalledVersion, downgrade: null, cancellationToken).ConfigureAwait(false);
            lock (_statusLock)
            {
                _lastChecked = _time.GetUtcNow();
                _lastError = null;
            }

            MonitorApp.Peer.Application.DiagnosticLog.Write("update", available is null
                ? $"checked: {_options.InstalledVersion} is the newest"
                : $"checked: {available.TargetVersion} is {available.Stage} (installed {_options.InstalledVersion})");
            if (available is null or { Stage: UpdateStage.Staged })
            {
                // Up to date, or the build already waiting is still the newest.
                return;
            }

            if (available.TargetVersion == previousFailure)
            {
                // Keep the failure visible instead of the transient Available record the check just wrote.
                var store = new UpdateStateStore(_options.UpdateDirectory);
                store.Write(store.Read() with { Active = priorFailure });
                LogSkippingFailed(available.TargetVersion);
                return;
            }

            var staged = await _service.StageAsync(cancellationToken).ConfigureAwait(false);
            if (_fetcher?.LastEnvelope is { } envelope)
            {
                Directory.CreateDirectory(_options.UpdateDirectory);
                await File.WriteAllBytesAsync(_options.ManifestCopyPath, envelope, cancellationToken).ConfigureAwait(false);
            }

            LogStaged(staged.TargetVersion, _options.Policy);
            if (_options.Policy == NodeUpdatePolicy.Auto && CanApplyHere)
            {
                _applyTask = ApplyStagedAsync("policy:auto", cancellationToken);
                await _applyTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is UpdateVerificationException or UpdateStateException or UpdateStateCorruptException or HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_statusLock)
            {
                _lastChecked = _time.GetUtcNow();
                _lastError = $"{exception.GetType().Name}: {exception.Message}";
            }

            LogCycleFailed(exception.GetType().Name, exception.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        WriteHealthMarker();
        if (_service is null)
        {
            LogDisabled(_disabledReason);
            return;
        }

        try
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                // Up to 10% jitter so a fleet does not poll in lockstep.
                var jitter = TimeSpan.FromSeconds(_options.Interval.TotalSeconds * 0.1 * RandomNumberGenerator.GetInt32(0, 1000) / 1000.0);
                await Task.Delay(_options.Interval + jitter, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Package kind is validated against the host OS in NodeUpdateOptions.FromEnvironment; Windows ("zip") applies only when the detached helper is installed.
    private bool CanApplyHere => _options.CanApplyHere;

    private async Task ApplyStagedAsync(string approvedBy, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service!.ApplyAsync(new UpdateApproval(approvedBy, _time.GetUtcNow()), _applier, _options.HealthTimeout, cancellationToken)
                .ConfigureAwait(false);
            LogApplied(result.TargetVersion, result.Stage);
        }
        catch (Exception exception) when (exception is UpdateVerificationException or UpdateStateException or IOException)
        {
            lock (_statusLock)
            {
                _lastError = $"{exception.GetType().Name}: {exception.Message}";
            }

            LogCycleFailed(exception.GetType().Name, exception.Message);
        }
    }

    /// <summary>After a restart: settle Applying/Staged records and fold in the privileged helper's recorded outcome.</summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await _service!.ReconcileAsync(_options.InstalledVersion, cancellationToken).ConfigureAwait(false);
            if (!TryFoldHelperResult() && current is { Stage: UpdateStage.Failed } failed && failed.TargetVersion != _options.InstalledVersion.ToString())
            {
                // A rollback restarts this (restored) version before the helper has confirmed it healthy and written its
                // result, so the outcome usually arrives after startup: keep watching for it for a bounded time.
                _resultWatch = WatchHelperResultAsync(cancellationToken);
            }
            else
            {
                File.Delete(_options.ResultPath);
            }
        }
        catch (Exception exception) when (exception is UpdateStateCorruptException or IOException or UpdateStateException)
        {
            lock (_statusLock)
            {
                _lastError = $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private Task? _resultWatch;

    /// <summary>Folds a recorded RolledBack/Failed helper outcome for the current target into the state; true once consumed.</summary>
    private bool TryFoldHelperResult()
    {
        var current = _service!.Current;
        if (current is not { Stage: UpdateStage.Failed or UpdateStage.Applied } ||
            ApplyHelper.TryReadResult(_options.ResultPath) is not { } outcome ||
            outcome.TargetVersion != current.TargetVersion)
        {
            return false;
        }

        var store = new UpdateStateStore(_options.UpdateDirectory);
        var snapshot = store.Read();
        if (snapshot.Active is { } active && outcome.Kind != ApplyOutcomeKind.Applied)
        {
            var stage = outcome.Kind == ApplyOutcomeKind.RolledBack ? UpdateStage.RolledBack : UpdateStage.Failed;
            store.Write(snapshot with { Active = active with { Stage = stage, Detail = outcome.Detail, UpdatedAtUtc = _time.GetUtcNow() } });
            store.Audit(_time.GetUtcNow(), stage.ToString().ToLowerInvariant(), outcome.Detail, active.TargetVersion);
        }

        File.Delete(_options.ResultPath);
        return true;
    }

    private async Task WatchHelperResultAsync(CancellationToken cancellationToken)
    {
        // The helper confirms the restored version within its health timeout; allow that twice over plus a margin.
        var deadline = _time.GetUtcNow() + (_options.HealthTimeout * 2) + TimeSpan.FromMinutes(1);
        try
        {
            while (_time.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken).ConfigureAwait(false);
                if (!await _stateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    if (TryFoldHelperResult())
                    {
                        return;
                    }
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is UpdateStateCorruptException or IOException or UpdateStateException)
        {
            lock (_statusLock)
            {
                _lastError = $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private void WriteHealthMarker()
    {
        try
        {
            Directory.CreateDirectory(_options.UpdateDirectory);
            var temp = _options.HealthMarkerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { version = _options.InstalledVersion.ToString() }));
            File.Move(temp, _options.HealthMarkerPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCycleFailed(exception.GetType().Name, "Could not write the health marker: " + exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update agent disabled: {Reason}")]
    private partial void LogDisabled(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} downloaded, verified and staged (policy {Policy}).")]
    private partial void LogStaged(string version, NodeUpdatePolicy policy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} apply finished: {Stage}")]
    private partial void LogApplied(string version, UpdateStage stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check did not complete ({Kind}): {Reason}")]
    private partial void LogCycleFailed(string kind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not retrying previously failed update {Version}; waiting for a newer release.")]
    private partial void LogSkippingFailed(string version);

    private sealed class RecordingFetcher(IManifestFetcher inner) : IManifestFetcher
    {
        public byte[]? LastEnvelope { get; private set; }

        public async Task<byte[]> FetchAsync(CancellationToken cancellationToken)
        {
            LastEnvelope = await inner.FetchAsync(cancellationToken).ConfigureAwait(false);
            return LastEnvelope;
        }
    }
}
