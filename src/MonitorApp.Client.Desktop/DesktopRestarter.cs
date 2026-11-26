using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using MonitorApp.Client.Core;

namespace MonitorApp.Client.Desktop;

/// <summary>
/// Restarts this window on the installed build. An update unpacks the new build beside the old one and points
/// <c>current</c> at it, so the new window is started through <c>current</c>, never from this window's own folder.
/// </summary>
internal sealed class DesktopRestarter(IClassicDesktopStyleApplicationLifetime lifetime) : IAppRestarter
{
    public string? RunningVersion { get; } = ReadVersion();

    public bool Restart()
    {
        var path = InstalledPath(Environment.ProcessPath);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            if (started is null)
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }

        lifetime.Shutdown();
        return true;
    }

    /// <summary>…\versions\0.1.30\desktop\App.exe becomes …\current\desktop\App.exe; any other path is used as it is.</summary>
    internal static string? InstalledPath(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var separator = Path.DirectorySeparatorChar;
        var marker = separator + "versions" + separator;
        var at = processPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return processPath;
        }

        var afterVersion = processPath.IndexOf(separator, at + marker.Length);
        return afterVersion < 0
            ? processPath
            : processPath[..at] + separator + "current" + processPath[afterVersion..];
    }

    private static string? ReadVersion()
    {
        var text = typeof(DesktopRestarter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = text?.Split('+')[0];

        // Developer builds are stamped 1.0.0; they never ask for a restart.
        return version is null or "1.0.0" ? null : version;
    }
}
