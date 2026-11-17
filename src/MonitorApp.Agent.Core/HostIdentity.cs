using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public sealed record HostIdentity(
    string MachineName,
    string OperatingSystemDescription,
    string Architecture,
    Platform Platform,
    string BootId);

public static class HostIdentityProvider
{
    public static HostIdentity ReadCurrent()
    {
        var platform = PlatformDetector.DetectCurrent();
        return new HostIdentity(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            platform,
            ReadBootId(platform));
    }

    private static string ReadBootId(Platform platform)
    {
        if (platform is Platform.Fedora or Platform.Ubuntu)
        {
            const string bootIdPath = "/proc/sys/kernel/random/boot_id";
            if (File.Exists(bootIdPath))
            {
                var bootId = File.ReadAllText(bootIdPath).Trim();
                if (Guid.TryParse(bootId, out var parsed))
                {
                    return parsed.ToString("D");
                }
            }
        }

        var bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var material = Encoding.UTF8.GetBytes(
            $"{Environment.MachineName}|{bootTime.UtcTicks / TimeSpan.TicksPerSecond}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }
}
