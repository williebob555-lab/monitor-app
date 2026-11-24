using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Services;

/// <summary>Where Syncthing's local API answers and the key it wants, read from its own config file.</summary>
public sealed record SyncthingEndpoint(Uri BaseAddress, string ApiKey);

/// <summary>
/// Syncthing's own status (D3) from its local REST API: how many folders are up to date, syncing or in error, each
/// folder's state, and how many of its devices are connected. Read-only (GET requests only). The API key is read from
/// Syncthing's config file on this computer each time, used only for requests to 127.0.0.1, and never stored or sent on.
/// </summary>
public sealed class SyncthingStatusReader(Func<SyncthingEndpoint?>? locate = null, HttpMessageHandler? handler = null) : IServiceDetailReader
{
    public string ServiceId => "syncthing";

    public const string Folders = "service.syncthing.folders";
    public const string FoldersUpToDate = "service.syncthing.folders_up_to_date";
    public const string FoldersWithErrors = "service.syncthing.folders_with_errors";
    public const string DevicesConnected = "service.syncthing.devices_connected";
    public const string Devices = "service.syncthing.devices";

    public static string FolderState(string folderId) => $"service.syncthing.folder.{folderId}.state";

    public async Task<IReadOnlyList<CollectedMetric>> ReadAsync(CancellationToken cancellationToken)
    {
        if ((locate ?? Locate)() is not { } endpoint)
        {
            return [];
        }

        using var client = handler is null ? new HttpClient(LoopbackHandler(), disposeHandler: true) : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = endpoint.BaseAddress;
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-API-Key", endpoint.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var metrics = new List<CollectedMetric>();
        using (var folders = await GetAsync(client, "rest/config/folders", cancellationToken).ConfigureAwait(false))
        {
            var list = folders.RootElement.EnumerateArray()
                .Where(f => !(f.TryGetProperty("paused", out var paused) && paused.ValueKind == JsonValueKind.True))
                .Select(f => f.GetProperty("id").GetString())
                .OfType<string>()
                .ToList();
            int upToDate = 0, errors = 0;
            foreach (var id in list)
            {
                using var status = await GetAsync(client, "rest/db/status?folder=" + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
                var state = status.RootElement.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
                var need = status.RootElement.TryGetProperty("needTotalItems", out var n) && n.TryGetInt64(out var items) ? items : 0;
                var failed = status.RootElement.TryGetProperty("errors", out var e) && e.TryGetInt64(out var count) ? count : 0;
                var shown = state == "error" || failed > 0 ? "error" : state == "idle" && need == 0 ? "up to date" : state == "idle" ? "out of sync" : state;
                if (shown == "up to date") upToDate++;
                if (shown == "error") errors++;
                if (IsSafeId(id))
                {
                    metrics.Add(Text(FolderState(id), shown));
                }
            }

            metrics.Add(Count(Folders, list.Count));
            metrics.Add(Count(FoldersUpToDate, upToDate));
            metrics.Add(Count(FoldersWithErrors, errors));
        }

        using (var connections = await GetAsync(client, "rest/system/connections", cancellationToken).ConfigureAwait(false))
        {
            if (connections.RootElement.TryGetProperty("connections", out var all) && all.ValueKind == JsonValueKind.Object)
            {
                var devices = all.EnumerateObject().ToList();
                metrics.Add(Count(Devices, devices.Count));
                metrics.Add(Count(DevicesConnected, devices.Count(d => d.Value.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True)));
            }
        }

        return metrics;
    }

    /// <summary>Folder ids go into signal ids, so only plain ones are used ("abcd-1234", "photos").</summary>
    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Syncthing refused the API key from its config file.");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Syncthing's GUI uses its own certificate when HTTPS is on; only 127.0.0.1 is ever contacted.</summary>
    private static HttpClientHandler LoopbackHandler() => new()
    {
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { } uri && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip),
    };

    /// <summary>
    /// Finds Syncthing's config for any user on this computer (the node runs as a service, Syncthing as the person) and
    /// reads the GUI port, whether it uses HTTPS, and the API key. Only a loopback or "all addresses" GUI is used.
    /// </summary>
    public static SyncthingEndpoint? Locate()
    {
        foreach (var path in ConfigCandidates().Where(File.Exists))
        {
            if (ParseConfig(File.ReadAllText(path)) is { } endpoint)
            {
                return endpoint;
            }
        }

        return null;
    }

    public static SyncthingEndpoint? ParseConfig(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var gui = document.Root?.Element("gui");
        var key = gui?.Element("apikey")?.Value.Trim();
        var address = gui?.Element("address")?.Value.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(address))
        {
            return null;
        }

        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address.AsSpan(colon + 1), out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        var host = address[..colon].Trim('[', ']');
        if (host is not ("127.0.0.1" or "localhost" or "0.0.0.0" or "::1" or "::" or ""))
        {
            return null;
        }

        var tls = string.Equals(gui!.Attribute("tls")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return new SyncthingEndpoint(new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{port}/"), key);
    }

    private static IEnumerable<string> ConfigCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users");
            foreach (var home in SafeDirectories(users))
            {
                yield return Path.Combine(home, "AppData", "Local", "Syncthing", "config.xml");
            }

            yield break;
        }

        foreach (var home in SafeDirectories("/home").Append("/root"))
        {
            yield return Path.Combine(home, ".local", "state", "syncthing", "config.xml");
            yield return Path.Combine(home, ".config", "syncthing", "config.xml");
        }

        yield return "/var/lib/syncthing/.local/state/syncthing/config.xml";
        yield return "/var/lib/syncthing/.config/syncthing/config.xml";
    }

    private static string[] SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CollectedMetric Text(string id, string value) => new(id, MetricUnit.Unspecified, ObservationQuality.Valid, new MetricValue { StringValue = value });

    private static CollectedMetric Count(string id, int value) => new(id, MetricUnit.Count, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = (ulong)value });
}
