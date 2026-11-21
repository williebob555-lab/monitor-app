using System.Net;

namespace MonitorApp.Peer.Node;

public sealed record NodeOptions(string DataDirectory, IPAddress PeerBindAddress, int PeerPort, int AdminPort)
{
    public static NodeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use --name value pairs.", nameof(args));
            }

            values[args[index][2..]] = args[index + 1];
        }

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorApp",
            "peer-preview");
        var dataDirectory = Path.GetFullPath(values.GetValueOrDefault("data-dir", defaultRoot));
        var bindText = values.GetValueOrDefault("peer-bind", IPAddress.Loopback.ToString());
        if (!IPAddress.TryParse(bindText, out var bindAddress))
        {
            throw new ArgumentException("--peer-bind must be an IP address.", nameof(args));
        }

        var peerPort = ParsePort(values.GetValueOrDefault("peer-port", "55431"), "peer-port", allowZero: false);
        var adminPort = ParsePort(values.GetValueOrDefault("admin-port", "55432"), "admin-port", allowZero: true);
        return new NodeOptions(dataDirectory, bindAddress, peerPort, adminPort);
    }

    private static int ParsePort(string value, string name, bool allowZero)
    {
        if (!int.TryParse(value, out var port) || port < (allowZero ? 0 : 1) || port > 65_535)
        {
            throw new ArgumentOutOfRangeException(name, $"--{name} must be between {(allowZero ? 0 : 1)} and 65535.");
        }

        return port;
    }
}
