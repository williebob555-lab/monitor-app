using System.Security.Cryptography;
using MonitorApp.Update.Apply;

namespace MonitorApp.Update;

public sealed record UpdateTarget(
    string Channel,
    string Component,
    string Platform,
    string Architecture);

public sealed class UpdateStateException : Exception
{
    public UpdateStateException(string message)
        : base(message)
    {
    }
}

/// <summary>Android portable result: a verified APK on disk for the user to install through the system installer.</summary>
public sealed record AndroidUpdateOffer(ReleaseVersion Version, string ApkPath, string Sha256, long Size);

/// <summary>
/// Check, verify, stage, apply (on explicit approval), reconcile. All transitions persist to the state
/// store. An artifact whose verification failed is never staged and never applied. Default consent
/// policy (working decision): download/verify/stage automatically, apply only on <see cref="UpdateApproval"/>.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly UpdateTarget _target;
    private readonly TrustedUpdateKey _key;
    private readonly IManifestFetcher _fetcher;
    private readonly IArtifactDownloader _downloader;
    private readonly UpdateStateStore _store;
    private readonly string _stagingDirectory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService(
        UpdateTarget target,
        TrustedUpdateKey trustedKey,
        IManifestFetcher fetcher,
        IArtifactDownloader downloader,
        UpdateStateStore store,
        string stagingDirectory,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(trustedKey);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        _target = target;
        _key = trustedKey;
        _fetcher = fetcher;
        _downloader = downloader;
        _store = store;
        _stagingDirectory = stagingDirectory;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(stagingDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => _gate.Dispose();

    public UpdateState? Current => _store.Read().Active;

    /// <summary>Fetch and verify the manifest. Returns null when already up to date. Verification failures are audited and rethrown.</summary>
    public async Task<UpdateState?> CheckAsync(
        ReleaseVersion installedVersion,
        DowngradeAuthorization? downgrade,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Read();
            if (snapshot.Active is { Stage: UpdateStage.Applying })
            {
                throw new UpdateStateException("An update is being applied; reconcile before checking again.");
            }

            try
            {
                var envelope = await _fetcher.FetchAsync(cancellationToken).ConfigureAwait(false);
                var manifest = UpdateManifestCodec.VerifyAndRead(envelope, _key);
                if (string.Equals(manifest.Channel, _target.Channel, StringComparison.Ordinal) && manifest.Version == installedVersion)
                {
                    return null;
                }

                var authorizedDowngrade = downgrade is not null && downgrade.TargetVersion == manifest.Version ? downgrade : null;
                if (snapshot.HighestSeenVersion is { } seen
                    && ReleaseVersion.TryParse(seen, out var highest)
                    && manifest.Version < highest
                    && authorizedDowngrade is null)
                {
                    throw new UpdateVerificationException(
                        UpdateVerificationError.DowngradeOrReinstall,
                        "Manifest is older than a version already seen (possible replay of an old release).");
                }

                var artifact = UpdatePolicy.SelectArtifact(
                    manifest, installedVersion, _target.Channel, _target.Component, _target.Platform, _target.Architecture, authorizedDowngrade);

                if (authorizedDowngrade is not null)
                {
                    _store.Audit(_time.GetUtcNow(), "downgrade-authorized",
                        $"{installedVersion} -> {manifest.Version} by {authorizedDowngrade.ApprovedBy}: {authorizedDowngrade.Reason}");
                }

                var state = new UpdateState(
                    UpdateStage.Available, _target.Component, _target.Platform, _target.Architecture,
                    manifest.Version.ToString(), installedVersion.ToString(), artifact.Url, artifact.Size, artifact.Sha256,
                    null, authorizedDowngrade is null ? null : "audited downgrade", _time.GetUtcNow());
                var highestToStore = snapshot.HighestSeenVersion is { } h && ReleaseVersion.TryParse(h, out var hv) && hv > manifest.Version
                    ? h
                    : manifest.Version.ToString();
                _store.Write(new UpdateStateSnapshot(highestToStore, state));
                _store.Audit(_time.GetUtcNow(), "available", $"{installedVersion} -> {manifest.Version}");
                return state;
            }
            catch (UpdateVerificationException ex)
            {
                _store.Audit(_time.GetUtcNow(), "check-rejected", $"{ex.Error}: {ex.Message}");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Download into the protected staging directory, verify size and SHA-256, then atomically publish as Staged.</summary>
    public async Task<UpdateState> StageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Read();
            var active = snapshot.Active;
            if (active is not { Stage: UpdateStage.Available })
            {
                throw new UpdateStateException("No available update to stage.");
            }

            var partial = Path.Combine(_stagingDirectory, $"{Guid.NewGuid():N}.partial");
            var final = Path.Combine(_stagingDirectory, $"{active.ArtifactSha256[..16]}-{SafeFileName(active.ArtifactUrl)}");
            try
            {
                await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await _downloader.DownloadAsync(active.ArtifactUrl, stream, active.ArtifactSize, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                await VerifyAsync(partial, active, cancellationToken).ConfigureAwait(false);
                File.Move(partial, final, overwrite: true);
            }
            catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException)
            {
                File.Delete(partial);
                var failed = active with { Stage = UpdateStage.Failed, Detail = $"Staging failed: {ex.Message}", UpdatedAtUtc = _time.GetUtcNow() };
                _store.Write(snapshot with { Active = failed });
                _store.Audit(_time.GetUtcNow(), "stage-failed", ex.Message);
                throw;
            }

            var staged = active with { Stage = UpdateStage.Staged, StagedPath = final, Detail = null, UpdatedAtUtc = _time.GetUtcNow() };
            _store.Write(snapshot with { Active = staged });
            _store.Audit(_time.GetUtcNow(), "staged", staged.TargetVersion);
            return staged;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Apply a staged update. Requires explicit approval and re-verifies the staged bytes immediately before applying.</summary>
    public async Task<UpdateState> ApplyAsync(
        UpdateApproval approval,
        IUpdateApplier applier,
        TimeSpan healthTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.ApprovedBy);
        ArgumentNullException.ThrowIfNull(applier);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Read();
            if (snapshot.Active is not { Stage: UpdateStage.Staged, StagedPath: { } stagedPath } staged)
            {
                throw new UpdateStateException("No staged update to apply.");
            }

            try
            {
                await VerifyAsync(stagedPath, staged, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UpdateVerificationException or IOException)
            {
                File.Delete(stagedPath);
                var rejected = staged with { Stage = UpdateStage.Failed, StagedPath = null, Detail = $"Staged artifact failed re-verification: {ex.Message}", UpdatedAtUtc = _time.GetUtcNow() };
                _store.Write(snapshot with { Active = rejected });
                _store.Audit(_time.GetUtcNow(), "apply-rejected", ex.Message);
                throw;
            }

            var applying = staged with { Stage = UpdateStage.Applying, Detail = $"approved by {approval.ApprovedBy}", UpdatedAtUtc = _time.GetUtcNow() };
            _store.Write(snapshot with { Active = applying });
            _store.Audit(_time.GetUtcNow(), "applying", $"{staged.PreviousVersion} -> {staged.TargetVersion} approved by {approval.ApprovedBy}");

            ApplyOutcome outcome;
            try
            {
                outcome = await applier.ApplyAsync(
                    new ApplyRequest(stagedPath, ReleaseVersion.Parse(staged.TargetVersion), ReleaseVersion.Parse(staged.PreviousVersion), healthTimeout),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new ApplyOutcome(ApplyOutcomeKind.Failed, $"Applier threw {ex.GetType().Name}: {ex.Message}");
            }

            var stage = outcome.Kind switch
            {
                ApplyOutcomeKind.Applied => UpdateStage.Applied,
                ApplyOutcomeKind.RolledBack => UpdateStage.RolledBack,
                _ => UpdateStage.Failed,
            };
            var finished = applying with { Stage = stage, Detail = outcome.Detail, UpdatedAtUtc = _time.GetUtcNow() };
            _store.Write(snapshot with { Active = finished });
            _store.Audit(_time.GetUtcNow(), stage.ToString().ToLowerInvariant(), outcome.Detail, finished.TargetVersion);
            return finished;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run at startup (and on Android launch). A Staged update whose version is now installed becomes Applied
    /// (system installer finished). An Applying record found after a restart means the apply was interrupted
    /// and becomes an explicit Failed state unless the target version is what is now running.
    /// </summary>
    public async Task<UpdateState?> ReconcileAsync(ReleaseVersion installedVersion, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Read();
            if (snapshot.Active is not { Stage: UpdateStage.Staged or UpdateStage.Applying } active)
            {
                return snapshot.Active;
            }

            var target = ReleaseVersion.Parse(active.TargetVersion);
            UpdateState next;
            if (installedVersion == target)
            {
                next = active with { Stage = UpdateStage.Applied, Detail = "Target version is the running version.", UpdatedAtUtc = _time.GetUtcNow() };
            }
            else if (active.Stage == UpdateStage.Applying)
            {
                next = active with { Stage = UpdateStage.Failed, Detail = $"Apply was interrupted; running {installedVersion}, target {target}. Verify the installation.", UpdatedAtUtc = _time.GetUtcNow() };
            }
            else
            {
                return active;
            }

            _store.Write(snapshot with { Active = next });
            _store.Audit(_time.GetUtcNow(), "reconciled", $"{next.Stage}: {next.Detail}", next.TargetVersion);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Android: the staged, verified APK for the user to install via the system installer. Null unless Staged.</summary>
    public AndroidUpdateOffer? GetAndroidOffer()
    {
        return Current is { Stage: UpdateStage.Staged, StagedPath: { } path } state && state.Platform == "android"
            ? new AndroidUpdateOffer(ReleaseVersion.Parse(state.TargetVersion), path, state.ArtifactSha256, state.ArtifactSize)
            : null;
    }

    private static async Task VerifyAsync(string path, UpdateState state, CancellationToken cancellationToken) =>
        await ArtifactIntegrityVerifier.VerifyFileAsync(
            path,
            new UpdateArtifact(state.Component, state.Platform, state.Architecture, state.ArtifactUrl, state.ArtifactSize, state.ArtifactSha256),
            cancellationToken).ConfigureAwait(false);

    private static string SafeFileName(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        var clean = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return clean.Length is > 0 and <= 100 && clean.Trim('.').Length > 0 ? clean : "artifact.bin";
    }
}
