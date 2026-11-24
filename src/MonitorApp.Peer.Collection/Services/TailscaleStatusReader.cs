using System.Diagnostics;
using System.Text.Json;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Services;

/// <summary>Another device on the tailnet, as Tailscale on this computer reports it.</summary>
public sealed record TailscaleDevice(string Id, string Name, string? HostName, string? Os, string? Address, bool Online, bool IsExitNode, DateTimeOffset? LastSeen);

/// <summary>
/// Tailscale's own status (D2), read with "tailscale status --json": connected or not, this computer's Tailscale address,
/// how many of your devices are online, and whether traffic goes through an exit node. Read-only: it never changes
/// Tailscale's settings.
/// </summary>
public sealed class TailscaleStatusReader(Func<CancellationToken, Task<string?>>? runStatus = null) : IServiceDetailReader
{
    public string ServiceId => "tailscale";

    public const string Connected = "service.tailscale.connected";
    public const string State = "service.tailscale.state";
    public const string Address = "service.tailscale.address";
    public const string DevicesOnline = "service.tailscale.devices_online";
    public const string Devices = "service.tailscale.devices";
    public const string ExitNode = "service.tailscale.exit_node";

    /// <summary>The other devices on the tailnet from the most recent read; empty until one succeeded.</summary>
    public IReadOnlyList<TailscaleDevice> LastDevices { get; private set; } = [];

    public async Task<IReadOnlyList<CollectedMetric>> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await (runStatus ?? RunCliAsync)(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            LastDevices = [];
            return [];
        }

        LastDevices = ParseDevices(json);
        return Parse(json);
    }

    /// <summary>Every other device on the tailnet: its name, operating system, Tailscale address and whether it is online.</summary>
    public static IReadOnlyList<TailscaleDevice> ParseDevices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<TailscaleDevice>();
        if (!document.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var peer in peers.EnumerateObject().Select(p => p.Value).Take(512))
        {
            string? Str(string name) => peer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Str("ID");
            var host = Str("HostName");
            var dns = Str("DNSName")?.Split('.')[0];
            if (string.IsNullOrWhiteSpace(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || id.Length > 64)
            {
                continue;
            }

            var ipv4 = peer.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Select(ip => ip.GetString()).FirstOrDefault(ip => ip is not null && !ip.Contains(':', StringComparison.Ordinal))
                : null;
            DateTimeOffset? lastSeen = DateTimeOffset.TryParse(Str("LastSeen"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var seen) && seen.Year > 2000 ? seen.ToUniversalTime() : null;
            result.Add(new TailscaleDevice(
                id,
                Bounded(string.IsNullOrWhiteSpace(dns) ? host : dns) ?? id,
                Bounded(host),
                Bounded(Str("OS")),
                ipv4,
                peer.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True,
                peer.TryGetProperty("ExitNode", out var exit) && exit.ValueKind == JsonValueKind.True,
                lastSeen));
        }

        return result;
    }

    private static string? Bounded(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return value.Length > 64 ? value[..64] : value;
    }

    /// <summary>Turns the CLI's JSON into readings. Fields that are missing are left out, never guessed.</summary>
    public static IReadOnlyList<CollectedMetric> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var metrics = new List<CollectedMetric>();
        if (root.TryGetProperty("BackendState", out var backend) && backend.ValueKind == JsonValueKind.String)
        {
            var state = backend.GetString() ?? string.Empty;
            metrics.Add(Flag(Connected, state == "Running"));
            metrics.Add(Text(State, state));
        }

        if (root.TryGetProperty("Self", out var self) && self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
        {
            var ipv4 = ips.EnumerateArray().Select(ip => ip.GetString()).FirstOrDefault(ip => ip is not null && !ip.Contains(':', StringComparison.Ordinal));
            if (ipv4 is not null)
            {
                metrics.Add(Text(Address, ipv4));
            }
        }

        if (root.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
        {
            var all = peers.EnumerateObject().Select(p => p.Value).ToList();
            metrics.Add(Count(Devices, all.Count));
            metrics.Add(Count(DevicesOnline, all.Count(p => p.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True)));
            var exit = all.FirstOrDefault(p => p.TryGetProperty("ExitNode", out var isExit) && isExit.ValueKind == JsonValueKind.True);
            metrics.Add(Text(ExitNode, exit.ValueKind == JsonValueKind.Object && exit.TryGetProperty("HostName", out var host) ? host.GetString() ?? "yes" : "none"));
        }

        return metrics;
    }

    private static CollectedMetric Flag(string id, bool value) => new(id, MetricUnit.Boolean, ObservationQuality.Valid, new MetricValue { BooleanValue = value });

    private static CollectedMetric Text(string id, string value) => new(id, MetricUnit.Unspecified, ObservationQuality.Valid, new MetricValue { StringValue = value });

    private static CollectedMetric Count(string id, int value) => new(id, MetricUnit.Count, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = (ulong)value });

    private static async Task<string?> RunCliAsync(CancellationToken cancellationToken)
    {
        var cli = Candidates().FirstOrDefault(File.Exists);
        if (cli is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("status");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return output.Length == 0 ? null : output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("tailscale status did not answer within 10 seconds.");
        }
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            yield break;
        }

        yield return "/usr/bin/tailscale";
        yield return "/usr/sbin/tailscale";
        yield return "/usr/local/bin/tailscale";
        yield return "/opt/homebrew/bin/tailscale";
    }
}
