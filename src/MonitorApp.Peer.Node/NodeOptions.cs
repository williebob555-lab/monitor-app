using System.Net;
using MonitorApp.Peer.LocalApi;

namespace MonitorApp.Peer.Node;

public sealed record NodeOptions(
    string DataDirectory,
    IPAddress PeerBindAddress,
    int PeerPort,
    int AdminPort,
    LocalPresencePolicy LocalPresence = LocalPresencePolicy.RequiredWhenEnrolled,
    string PeerBindSource = "default")
{
    public const string LocalPresenceVariable = "MONITOR_APP_LOCAL_PRESENCE";
    public const string DataDirectoryVariable = "MONITOR_APP_DATA_DIR";
    public const string PeerBindVariable = "MONITOR_APP_PEER_BIND";
    public const string PeerPortVariable = "MONITOR_APP_PEER_PORT";
    public const string AdminPortVariable = "MONITOR_APP_ADMIN_PORT";

    /// <summary>
    /// Parses <c>--name value</c> arguments. Precedence: arguments, then MONITOR_APP_* environment
    /// variables (the packaged systemd unit loads /etc/monitor-app/node.conf into them), then defaults.
    /// The admin listener is always loopback; the peer listener defaults to loopback and is never
    /// widened without an explicit bind address.
    /// </summary>
    public static NodeOptions Parse(string[] args, bool serviceMode = false, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use --name value pairs.", nameof(args));
            }

            values[args[index][2..]] = args[index + 1];
        }

        string Pick(string name, string variable, string fallback)
        {
            if (values.TryGetValue(name, out var argument))
            {
                return argument;
            }

            var fromEnvironment = environment(variable);
            return string.IsNullOrWhiteSpace(fromEnvironment) ? fallback : fromEnvironment.Trim();
        }

        var dataDirectory = Path.GetFullPath(Pick(
            "data-dir",
            DataDirectoryVariable,
            serviceMode ? ServiceDataDirectory(environment) : DefaultDataDirectory()));
        // Precedence for the peer listener: --peer-bind, then MONITOR_APP_PEER_BIND, then the in-app "let my phone reach this
        // computer" choice (a Tailscale address only), then 127.0.0.1.
        string bindSource;
        IPAddress bindAddress;
        if (values.ContainsKey("peer-bind") || !string.IsNullOrWhiteSpace(environment(PeerBindVariable)))
        {
            bindSource = values.ContainsKey("peer-bind") ? "argument" : "environment";
            if (!IPAddress.TryParse(Pick("peer-bind", PeerBindVariable, string.Empty), out bindAddress!))
            {
                throw new ArgumentException("--peer-bind must be an IP address.", nameof(args));
            }
        }
        else if (PeerReachSetting.ReadWantsReach(dataDirectory))
        {
            bindSource = "setting";
            bindAddress = PeerReachSetting.ResolveSaved(dataDirectory, serviceMode ? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
        }
        else
        {
            bindSource = "default";
            bindAddress = IPAddress.Loopback;
        }

        var peerPort = ParsePort(Pick("peer-port", PeerPortVariable, "55431"), "peer-port", allowZero: false);
        var adminPort = ParsePort(Pick("admin-port", AdminPortVariable, "55432"), "admin-port", allowZero: true);
        var presence = Pick("local-presence", LocalPresenceVariable, "when-enrolled").ToLowerInvariant() switch
        {
            "off" => LocalPresencePolicy.Off,
            "when-enrolled" => LocalPresencePolicy.RequiredWhenEnrolled,
            "required" => LocalPresencePolicy.Required,
            _ => throw new ArgumentException("--local-presence must be off, when-enrolled, or required.", nameof(args)),
        };
        return new NodeOptions(dataDirectory, bindAddress, peerPort, adminPort, presence, bindSource);
    }

    /// <summary>
    /// Always-on service state location: %ProgramData%\MonitorApp on Windows; the systemd
    /// StateDirectory (STATE_DIRECTORY) or /var/lib/monitor-app on Linux.
    /// </summary>
    public static string ServiceDataDirectory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonitorApp");
        }

        var state = environment("STATE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(state))
        {
            // systemd may pass several colon-separated directories; the first is ours.
            var first = state.Split(':', StringSplitOptions.RemoveEmptyEntries)[0];
            if (Path.IsPathRooted(first))
            {
                return first;
            }
        }

        return "/var/lib/monitor-app";
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MonitorApp",
                "peer-preview");
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(dataHome, "monitor-app");
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
