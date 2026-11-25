using System.Diagnostics;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Desktop;

/// <summary>
/// Starts the Monitor App Windows service from the window when it is not running. Windows gives up on a service that
/// is slow to start during boot and never retries it, so without this the computer records and shares nothing until
/// someone finds the service in Windows Services. Starting a service needs administrator rights: Windows shows its
/// own "allow this app" prompt for <c>sc.exe</c>, and nothing else runs elevated.
/// </summary>
internal sealed class WindowsNodeStarter : INodeServiceStarter
{
    private const string ServiceName = "MonitorApp";
    private static readonly TimeSpan RunningWithin = TimeSpan.FromSeconds(60);

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            using var elevated = Process.Start(new ProcessStartInfo(SystemTool("sc.exe"), $"start {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (elevated is null)
            {
                return false;
            }

            await elevated.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Declined at the Windows prompt.
            return false;
        }

        var deadline = DateTime.UtcNow + RunningWithin;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo(SystemTool("sc.exe"), $"query {ServiceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (query is null)
            {
                return false;
            }

            var output = await query.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await query.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Always the system's own sc.exe, never one found on PATH or in the working directory.
    private static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);
}
