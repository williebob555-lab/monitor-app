using System.Text.Json;
using MonitorApp.Update;
using MonitorApp.Update.Apply;

namespace MonitorApp.Peer.Node.Update;

public sealed record ApplyHelperOutcome(string TargetVersion, ApplyOutcomeKind Kind, string Detail);

/// <summary>
/// The unprivileged service cannot run dpkg/rpm (non-root, ProtectSystem=strict) and would be killed by its own
/// restart. It therefore writes a request file and a root helper (the same binary started with
/// <c>--apply-update</c> by a systemd path unit) performs the install. The helper trusts only the signed manifest
/// and the root-owned key file, never the request contents: it re-verifies the manifest, the artifact digest on a
/// private copy, and the version before installing, then health-checks and rolls back through
/// <see cref="LinuxPackageApplier"/>.
/// </summary>
public static class ApplyHelper
{
    public const string HelperArgument = "--apply-update";

    private sealed record Request(string TargetVersion, string StagedPath);

    public static ApplyHelperOutcome? TryReadResult(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ApplyHelperOutcome>(File.ReadAllBytes(path), SerializerOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Unprivileged side: write the request and wait (bounded) for the helper's result.</summary>
    /// <param name="options">Paths for the request/result handshake.</param>
    /// <param name="launchHelper">
    /// Starts the privileged helper when nothing else does (Windows: the on-demand scheduled task). Null on Linux, where
    /// the systemd path unit fires on the request file. A launch failure is reported as a failed apply, never swallowed.
    /// </param>
    public sealed class HandoffApplier(NodeUpdateOptions options, Func<CancellationToken, Task>? launchHelper = null) : IUpdateApplier
    {
        public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            File.Delete(options.ResultPath);
            var body = JsonSerializer.SerializeToUtf8Bytes(new Request(request.TargetVersion.ToString(), request.StagedArtifactPath));
            var temp = options.RequestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, body, cancellationToken).ConfigureAwait(false);
            File.Move(temp, options.RequestPath, overwrite: true);

            if (launchHelper is not null)
            {
                try
                {
                    await launchHelper(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    File.Delete(options.RequestPath);
                    return new ApplyOutcome(ApplyOutcomeKind.Failed, "Could not start the update helper: " + exception.Message);
                }
            }

            var deadline = DateTimeOffset.UtcNow + (request.HealthTimeout * 3) + TimeSpan.FromMinutes(6);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (TryReadResult(options.ResultPath) is { } result && result.TargetVersion == request.TargetVersion.ToString())
                {
                    return new ApplyOutcome(result.Kind, result.Detail);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            File.Delete(options.RequestPath);
            return new ApplyOutcome(ApplyOutcomeKind.Failed, "The privileged update helper did not report a result (Linux: is monitor-app-update.path enabled? Windows: is the MonitorAppUpdate task registered?).");
        }
    }

    /// <summary>Root side (Linux). <paramref name="updateDirectory"/> is the service's (untrusted) update directory.</summary>
    public static Task<int> RunHelperAsync(
        string updateDirectory,
        string keyFile,
        string workDirectory,
        string packageKind,
        string channel,
        IProcessRunner? runner = null,
        TimeSpan? healthTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var rpm = packageKind == "rpm";
        return RunVerifiedAsync(
            updateDirectory,
            keyFile,
            workDirectory,
            rpm ? "fedora" : "ubuntu",
            "pending-" + packageKind + "." + (rpm ? "rpm" : "deb"),
            channel,
            restrictStagedPathToUpdateDirectory: false,
            marker => new LinuxPackageApplier(
                runner ?? new SystemProcessRunner(),
                new FileHealthProbe(marker),
                new LinuxPackageOptions(
                    rpm ? LinuxPackageKind.Rpm : LinuxPackageKind.Deb,
                    Path.Combine(workDirectory, "retained"),
                    new ServiceCommand("systemctl", ["restart", "monitor-app"]))),
            healthTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Shared privileged flow: read and consume the request, then trust only the signed manifest and key file. The
    /// artifact is copied to a private work directory and its digest verified on the copy before the platform applier runs.
    /// </summary>
    internal static async Task<int> RunVerifiedAsync(
        string updateDirectory,
        string keyFile,
        string workDirectory,
        string platform,
        string pendingFileName,
        string channel,
        bool restrictStagedPathToUpdateDirectory,
        Func<string, IUpdateApplier> createApplier,
        TimeSpan? healthTimeout,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(updateDirectory, "apply-request.json");
        var resultPath = Path.Combine(updateDirectory, "apply-result.json");
        var manifestPath = Path.Combine(updateDirectory, "manifest.json");
        var marker = Path.Combine(updateDirectory, "health.json");
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllBytes(requestPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine($"apply-update: unreadable request: {exception.Message}");
            return 2;
        }
        finally
        {
            File.Delete(requestPath);
        }

        if (request is null || !ReleaseVersion.TryParse(request.TargetVersion, out var target))
        {
            Console.Error.WriteLine("apply-update: invalid request.");
            return 2;
        }

        async Task<int> FinishAsync(ApplyOutcomeKind kind, string detail)
        {
            Console.WriteLine($"apply-update {target}: {kind}: {detail}");
            File.Delete(resultPath);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ApplyHelperOutcome(target.ToString(), kind, detail), SerializerOptions);
            // CreateNew never follows a symlink the unprivileged service might have planted. The helper runs as root, so
            // the result is made readable (not writable) by the service that owns the 0700 update directory; without
            // that the node can never fold a rollback outcome into its state.
            await using var stream = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (!OperatingSystem.IsWindows())
            {
                // On the handle (no path race) and explicit, so the helper unit's UMask=0077 does not strip it.
                File.SetUnixFileMode(
                    stream.SafeFileHandle,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return kind == ApplyOutcomeKind.Applied ? 0 : 1;
        }

        var trust = NodeUpdateTrust.TryLoad(keyFile);
        if (trust is null)
        {
            return await FinishAsync(ApplyOutcomeKind.Failed, "No trusted update key is installed.").ConfigureAwait(false);
        }

        try
        {
            if (restrictStagedPathToUpdateDirectory && !IsUnder(request.StagedPath, updateDirectory))
            {
                return await FinishAsync(ApplyOutcomeKind.Failed, "The staged package is not inside the update directory.").ConfigureAwait(false);
            }

            var installed = NodeUpdateOptions.InstalledVersionFromAssembly();
            var manifest = UpdateManifestCodec.VerifyAndRead(
                File.ReadAllBytes(manifestPath),
                new TrustedUpdateKey(trust.KeyId, trust.PublicKeySpki));
            if (manifest.Version != target)
            {
                return await FinishAsync(ApplyOutcomeKind.Failed, "Signed manifest version differs from the approved version.").ConfigureAwait(false);
            }

            var artifact = UpdatePolicy.SelectArtifact(manifest, installed, channel, "node", platform, "x64");

            Directory.CreateDirectory(workDirectory);
            var copy = Path.Combine(workDirectory, pendingFileName);
            File.Copy(request.StagedPath, copy, overwrite: true);
            await ArtifactIntegrityVerifier.VerifyFileAsync(copy, artifact, cancellationToken).ConfigureAwait(false);

            var outcome = await createApplier(marker).ApplyAsync(
                new ApplyRequest(copy, target, installed, healthTimeout ?? TimeSpan.FromSeconds(90)),
                cancellationToken).ConfigureAwait(false);
            File.Delete(copy);
            return await FinishAsync(outcome.Kind, outcome.Detail).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UpdateVerificationException or IOException or FormatException or UnauthorizedAccessException)
        {
            return await FinishAsync(ApplyOutcomeKind.Failed, $"Refused to install: {exception.Message}").ConfigureAwait(false);
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
