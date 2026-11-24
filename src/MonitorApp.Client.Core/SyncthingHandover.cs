using System.Net;
using System.Xml.Linq;

namespace MonitorApp.Client.Core;

/// <summary>
/// Hands this person's Syncthing address and API key to the Monitor App node on the same computer, so the node (a service
/// account that cannot read the person's files) can show folder and device status. Only ever sent to a loopback node:
/// a node on another computer never receives it.
/// </summary>
public static class SyncthingHandover
{
    public sealed record LocalSyncthing(int Port, bool Tls, string ApiKey);

    /// <summary>Reads the current user's Syncthing config, or null when there is none or its GUI is not on this computer.</summary>
    public static LocalSyncthing? ReadForCurrentUser()
    {
        foreach (var path in Candidates().Where(File.Exists))
        {
            try
            {
                if (Parse(File.ReadAllText(path)) is { } found) return found;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // Try the next place.
            }
        }

        return null;
    }

    public static LocalSyncthing? Parse(string xml)
    {
        var gui = XDocument.Parse(xml).Root?.Element("gui");
        var key = gui?.Element("apikey")?.Value.Trim();
        var address = gui?.Element("address")?.Value.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(address)) return null;
        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address.AsSpan(colon + 1), out var port) || port is < 1 or > 65535) return null;
        var host = address[..colon].Trim('[', ']');
        if (host is not ("127.0.0.1" or "localhost" or "0.0.0.0" or "::1" or "::" or "")) return null;
        return new LocalSyncthing(port, string.Equals(gui!.Attribute("tls")?.Value, "true", StringComparison.OrdinalIgnoreCase), key);
    }

    /// <summary>Sends it to the node when the node is on this computer. Returns true when the node took it.</summary>
    public static async Task<bool> HandOverAsync(IPeerDataSource source, Uri nodeAddress, CancellationToken ct = default)
    {
        if (source is not LocalApiClient client || !IsLoopback(nodeAddress) || ReadForCurrentUser() is not { } found) return false;
        try
        {
            await client.HandOverSyncthingAsync(found.Port, found.Tls, found.ApiKey, ct).ConfigureAwait(false);
            return true;
        }
        catch (LocalApiException)
        {
            return false; // an older node without the endpoint
        }
    }

    public static bool IsLoopback(Uri address) =>
        address.IsLoopback || (IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncthing", "config.xml");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        yield return Path.Combine(string.IsNullOrEmpty(state) ? Path.Combine(home, ".local", "state") : state, "syncthing", "config.xml");
        yield return Path.Combine(string.IsNullOrEmpty(config) ? Path.Combine(home, ".config") : config, "syncthing", "config.xml");
    }
}
