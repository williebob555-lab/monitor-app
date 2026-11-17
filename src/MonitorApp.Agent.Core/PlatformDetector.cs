using System.Runtime.InteropServices;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public static class PlatformDetector
{
    public static Platform DetectCurrent()
    {
        if (OperatingSystem.IsWindows())
        {
            return Platform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            const string osReleasePath = "/etc/os-release";
            if (!File.Exists(osReleasePath))
            {
                throw new PlatformNotSupportedException("Linux distribution identity is unavailable.");
            }

            return ParseLinuxDistribution(File.ReadAllText(osReleasePath));
        }

        throw new PlatformNotSupportedException(
            $"Monitor App does not have an agent platform mapping for {RuntimeInformation.OSDescription}.");
    }

    public static Platform ParseLinuxDistribution(string osRelease)
    {
        ArgumentNullException.ThrowIfNull(osRelease);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in osRelease.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            values[line[..separator].Trim()] = value;
        }

        if (!values.TryGetValue("ID", out var id))
        {
            throw new PlatformNotSupportedException("Linux distribution ID is unavailable.");
        }

        return id.ToLowerInvariant() switch
        {
            "fedora" => Platform.Fedora,
            "ubuntu" => Platform.Ubuntu,
            _ => throw new PlatformNotSupportedException(
                $"Linux distribution '{id}' has not passed Monitor App platform verification."),
        };
    }
}
