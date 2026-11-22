using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MonitorApp.Update;

namespace MonitorApp.Peer.Node.Update;

public enum NodeUpdatePolicy
{
    /// <summary>Download, verify and stage automatically; install only after explicit approval (user-confirmed default).</summary>
    Approve,

    /// <summary>Install automatically after staging; the applier still health-checks and rolls back.</summary>
    Auto,
}

/// <summary>The non-secret trust anchor and repository shipped inside the package (update-key.json).</summary>
public sealed record NodeUpdateTrust(string KeyId, byte[] PublicKeySpki, string Repository)
{
    public static NodeUpdateTrust? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var keyId = root.GetProperty("keyId").GetString();
            var key = root.GetProperty("publicKeySpkiBase64").GetString();
            var repository = root.GetProperty("repository").GetString();
            return string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(repository)
                ? null
                : new NodeUpdateTrust(keyId, Convert.FromBase64String(key), repository);
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Update-agent configuration. Environment variables (the packaged unit loads /etc/monitor-app/node.conf):
/// MONITOR_APP_UPDATE_POLICY=approve|auto (default approve), MONITOR_APP_UPDATE_REPO=owner/repo,
/// MONITOR_APP_UPDATE_KEY_FILE, MONITOR_APP_UPDATE_API_URL (HTTPS), MONITOR_APP_UPDATE_INTERVAL_HOURS (default 6),
/// MONITOR_APP_UPDATE_CHANNEL (default preview), MONITOR_APP_UPDATE_PACKAGE=auto|deb|rpm.
/// </summary>
public sealed record NodeUpdateOptions(
    NodeUpdatePolicy Policy,
    string Repository,
    Uri ApiBase,
    string Channel,
    TimeSpan Interval,
    string UpdateDirectory,
    string KeyFilePath,
    string PackageKind,
    ReleaseVersion InstalledVersion,
    TimeSpan HealthTimeout)
{
    public const string PolicyVariable = "MONITOR_APP_UPDATE_POLICY";

    public string HealthMarkerPath => Path.Combine(UpdateDirectory, "health.json");

    public string StagingDirectory => Path.Combine(UpdateDirectory, "staging");

    public string RequestPath => Path.Combine(UpdateDirectory, "apply-request.json");

    public string ResultPath => Path.Combine(UpdateDirectory, "apply-result.json");

    public string ManifestCopyPath => Path.Combine(UpdateDirectory, "manifest.json");

    /// <summary>Release platform derived from the package kind only, so the choice is explicit and testable on any host.</summary>
    public string Platform => PackageKind switch { "rpm" => "fedora", "deb" => "ubuntu", _ => "windows" };

    public static string DefaultKeyFile() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(AppContext.BaseDirectory, "update-key.json")
            : "/etc/monitor-app/update-key.json";

    public static NodeUpdateOptions FromEnvironment(NodeOptions node, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        environment ??= Environment.GetEnvironmentVariable;
        string? Get(string name) => environment(name) is { Length: > 0 } value ? value.Trim() : null;

        var policyText = Get(PolicyVariable) ?? "approve";
        if (!Enum.TryParse<NodeUpdatePolicy>(policyText, ignoreCase: true, out var policy))
        {
            throw new ArgumentException($"{PolicyVariable} must be 'approve' or 'auto'.");
        }

        var keyFile = Get("MONITOR_APP_UPDATE_KEY_FILE") ?? DefaultKeyFile();
        var repository = Get("MONITOR_APP_UPDATE_REPO") ?? NodeUpdateTrust.TryLoad(keyFile)?.Repository ?? string.Empty;
        var api = new Uri(Get("MONITOR_APP_UPDATE_API_URL") ?? "https://api.github.com/", UriKind.Absolute);
        if (api.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("MONITOR_APP_UPDATE_API_URL must use HTTPS.");
        }

        if (!double.TryParse(Get("MONITOR_APP_UPDATE_INTERVAL_HOURS") ?? "6", NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            || hours is < 0.01 or > 24 * 30)
        {
            throw new ArgumentException("MONITOR_APP_UPDATE_INTERVAL_HOURS is out of range.");
        }

        var kind = (Get("MONITOR_APP_UPDATE_PACKAGE") ?? "auto").ToLowerInvariant();
        if (kind == "auto")
        {
            kind = DetectPackageKind();
        }

        if (kind is not ("deb" or "rpm" or "zip") || (OperatingSystem.IsWindows() != (kind == "zip")))
        {
            throw new ArgumentException("MONITOR_APP_UPDATE_PACKAGE must be 'zip' on Windows and 'deb' or 'rpm' on Linux.");
        }

        return new NodeUpdateOptions(
            policy,
            repository,
            api,
            Get("MONITOR_APP_UPDATE_CHANNEL") ?? "preview",
            TimeSpan.FromHours(hours),
            Path.Combine(node.DataDirectory, "update"),
            keyFile,
            kind,
            InstalledVersionFromAssembly(),
            TimeSpan.FromSeconds(90));
    }

    public static ReleaseVersion InstalledVersionFromAssembly()
    {
        var text = typeof(NodeUpdateOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        var core = text.Split('+', '-')[0];
        return ReleaseVersion.TryParse(core, out var version) ? version : new ReleaseVersion(0, 0, 0);
    }

    private static string DetectPackageKind()
    {
        if (OperatingSystem.IsWindows())
        {
            return "zip";
        }

        try
        {
            var release = File.ReadAllText("/etc/os-release").ToLowerInvariant();
            return release.Contains("fedora", StringComparison.Ordinal) || release.Contains("rhel", StringComparison.Ordinal) ? "rpm" : "deb";
        }
        catch (IOException)
        {
            return "deb";
        }
    }
}
