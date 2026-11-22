using MonitorApp.Update.Apply;

namespace MonitorApp.Peer.Node.Update;

/// <summary>
/// Windows in-place apply. The node service (virtual account NT SERVICE\MonitorApp, read/execute on its binaries) cannot
/// replace itself while running and must not be able to rewrite its own binaries. It therefore writes the same
/// apply-request.json as on Linux and starts the pre-registered on-demand scheduled task <see cref="TaskName"/>,
/// which runs this binary as SYSTEM with <c>--apply-update &lt;update dir&gt; --install-root &lt;root&gt;</c>. The task definition
/// (admin-only writable, so the arguments cannot be changed by the service) is the only privileged entry point.
/// The helper trusts only the signed manifest and the admin-owned key file; the request merely names the approved version
/// and a staged zip, which must lie inside the update directory and match the signed digest. It then runs
/// <see cref="WindowsSideBySideApplier"/>: extract to versions\&lt;v&gt;, stop service, repoint <c>current</c>, start, wait
/// for the health marker, and swap back on failure.
/// </summary>
public static class WindowsApplyHelper
{
    public const string TaskName = "MonitorAppUpdate";
    public const string ServiceName = "MonitorApp";
    public const string InstallRootArgument = "--install-root";

    /// <summary>Written by install-service.ps1 (admin) into the install root; its presence is how the node knows a helper exists.</summary>
    public const string MarkerFileName = "update-helper.json";

    public static bool IsInstalled(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(installRoot, MarkerFileName))
                   && Directory.Exists(Path.Combine(installRoot, "versions"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Unprivileged side: ask Task Scheduler to run the registered helper task.</summary>
    public static async Task LaunchAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var result = await runner.RunAsync("schtasks.exe", ["/Run", "/TN", TaskName], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"schtasks /Run {TaskName} exited {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    /// <summary>Privileged side. <paramref name="service"/> is replaceable so the flow can be tested without a real service.</summary>
    public static Task<int> RunAsync(
        string updateDirectory,
        string installRoot,
        string keyFile,
        string channel,
        IServiceController? service = null,
        TimeSpan? healthTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(installRoot);
        return ApplyHelper.RunVerifiedAsync(
            updateDirectory,
            keyFile,
            Path.Combine(root, ".update-work"),
            "windows",
            "pending-windows.zip",
            channel,
            restrictStagedPathToUpdateDirectory: true,
            marker =>
            {
                var layout = new VersionedInstallLayout(root);
                if (layout.GetCurrentVersion() is null)
                {
                    throw new IOException("The install root has no 'current' link; it is not a versioned install. Re-run Install-MonitorApp.ps1.");
                }

                return new WindowsSideBySideApplier(
                    layout,
                    service ?? new ScExeServiceController(new SystemProcessRunner(), ServiceName),
                    new FileHealthProbe(marker));
            },
            healthTimeout,
            cancellationToken);
    }
}
