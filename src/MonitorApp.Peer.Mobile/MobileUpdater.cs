using System.Security.Cryptography;
using System.Text.Json;
using MonitorApp.Update;

namespace MonitorApp.Peer.Mobile;

/// <summary>The non-secret trust anchor packaged into the APK by the release build (update-key.json).</summary>
public sealed record MobileUpdateTrust(string KeyId, byte[] PublicKeySpki, string Repository)
{
    /// <summary>Null when the bytes are not a complete update-key.json (development builds ship none: updates stay off).</summary>
    public static MobileUpdateTrust? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            var keyId = root.GetProperty("keyId").GetString();
            var key = root.GetProperty("publicKeySpkiBase64").GetString();
            var repository = root.GetProperty("repository").GetString();
            return string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(repository)
                ? null
                : new MobileUpdateTrust(keyId, Convert.FromBase64String(key), repository);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Hands a verified APK to the system installer. Android shows its own confirmation; nothing installs silently.</summary>
public interface IApkInstaller
{
    /// <summary>Starts the system install of <paramref name="apkPath"/>. Returns null once handed over, or a plain reason it could not start.</summary>
    Task<string?> InstallAsync(string apkPath, CancellationToken cancellationToken);
}

/// <summary>The phone's own update state, in the same shape a computer reports.</summary>
public sealed record MobileUpdateStatus(
    string InstalledVersion,
    string Stage,
    string? TargetVersion,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    string? LastError,
    bool ApprovalPending,
    string? TargetDigest);

/// <summary>
/// The phone's updater: look for a newer signed preview (hourly and on request), download the APK, verify the manifest signature
/// against the packaged key and the APK's size and SHA-256, then wait. Installing happens only when the person presses Install and
/// confirms Android's own prompt; Android additionally refuses an APK not signed with the installed app's key.
/// </summary>
public sealed class MobileUpdater : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan ManualCheckSpacing = TimeSpan.FromSeconds(30);

    private readonly ReleaseVersion _installed;
    private readonly UpdateService? _service;
    private readonly IApkInstaller _installer;
    private readonly TimeProvider _time;
    private readonly string _disabledReason;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly object _statusLock = new();
    private DateTimeOffset? _lastChecked;
    private string? _lastError;

    public MobileUpdater(
        string updateDirectory,
        ReleaseVersion installed,
        MobileUpdateTrust? trust,
        IApkInstaller installer,
        IManifestFetcher? fetcher = null,
        IArtifactDownloader? downloader = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateDirectory);
        ArgumentNullException.ThrowIfNull(installer);
        _installed = installed;
        _installer = installer;
        _time = time ?? TimeProvider.System;
        if (trust is null)
        {
            _disabledReason = "this build was not made by the release pipeline, so it has no update key.";
            return;
        }

        _disabledReason = string.Empty;
        HttpClient? http = null;
        if (fetcher is null || downloader is null)
        {
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorApp-Android-Updater");
        }

        _service = new UpdateService(
            new UpdateTarget("preview", "mobile", "android", "universal"),
            new TrustedUpdateKey(trust.KeyId, trust.PublicKeySpki),
            fetcher ?? new GitHubPreviewManifestFetcher(http!, new Uri("https://api.github.com/"), trust.Repository),
            downloader ?? new HttpArtifactDownloader(http!),
            new UpdateStateStore(updateDirectory),
            Path.Combine(updateDirectory, "staging"),
            _time);
    }

    public void Dispose()
    {
        _service?.Dispose();
        _cycle.Dispose();
    }

    public MobileUpdateStatus GetStatus()
    {
        UpdateState? state = null;
        string? corrupt = null;
        try
        {
            state = _service?.Current;
        }
        catch (UpdateStateCorruptException)
        {
            corrupt = "Update state is corrupt; refusing to reset anti-rollback state.";
        }

        lock (_statusLock)
        {
            return new MobileUpdateStatus(
                _installed.ToString(),
                _service is null ? "Disabled" : state?.Stage.ToString() ?? "Idle",
                state?.TargetVersion,
                _service is null ? _disabledReason : state?.Detail,
                _lastChecked,
                corrupt ?? _lastError,
                state is { Stage: UpdateStage.Staged, StagedPath: not null },
                state?.ArtifactSha256);
        }
    }

    /// <summary>Checks at start, then every <paramref name="interval"/> (default hourly) until cancelled.</summary>
    public async Task RunAsync(TimeSpan? interval, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            await _service.ReconcileAsync(_installed, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(interval ?? DefaultInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The person pressed "Check for updates". Spaced so repeated presses cannot exhaust the release API's rate limit.</summary>
    public async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            if (_lastChecked is { } last && _time.GetUtcNow() - last < ManualCheckSpacing)
            {
                return;
            }
        }

        await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The person pressed Install for <paramref name="version"/>: re-verify the staged APK and hand it to Android's installer.
    /// Returns null once Android shows its prompt, or a plain reason it did not.
    /// </summary>
    public async Task<string?> InstallAsync(string version, CancellationToken cancellationToken)
    {
        if (_service?.GetAndroidOffer() is not { } offer || offer.Version.ToString() != version)
        {
            return "No verified update for that version is waiting.";
        }

        await using (var stream = File.OpenRead(offer.ApkPath))
        {
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(digest, offer.Sha256, StringComparison.OrdinalIgnoreCase) || stream.Length != offer.Size)
            {
                return "The downloaded update no longer matches its signed checksum, so it was not installed. Check for updates again.";
            }
        }

        return await _installer.InstallAsync(offer.ApkPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return;
        }

        await _cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _service.ReconcileAsync(_installed, cancellationToken).ConfigureAwait(false);
            // A downloaded build waits for the person to press Install, but a newer release replaces it.
            var available = await _service.CheckAsync(_installed, downgrade: null, cancellationToken).ConfigureAwait(false);
            if (available is { Stage: UpdateStage.Available })
            {
                await _service.StageAsync(cancellationToken).ConfigureAwait(false);
            }

            MarkChecked(null);
        }
        catch (Exception exception) when (exception is UpdateVerificationException or UpdateStateException or UpdateStateCorruptException or HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkChecked($"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _cycle.Release();
        }
    }

    private void MarkChecked(string? error)
    {
        lock (_statusLock)
        {
            _lastChecked = _time.GetUtcNow();
            _lastError = error;
        }
    }
}
