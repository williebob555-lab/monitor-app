using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using MonitorApp.Peer.LocalApi;

namespace MonitorApp.Peer.Node;

/// <summary>
/// The saved "let my phone reach this computer" choice (network.json in the data directory) and Tailscale address lookup.
/// Only a Tailscale address (100.64.0.0/10) that exists on this computer is ever used; anything else keeps the listener on
/// 127.0.0.1. Arguments and MONITOR_APP_PEER_BIND still win over the saved choice.
/// </summary>
public static class PeerReachSetting
{
    public const string FileName = "network.json";

    /// <summary>Windows with the update helper installed: the SYSTEM task adds the Tailscale-only firewall rule on request.</summary>
    public static Func<Task>? WindowsFirewallHelper()
    {
        if (!OperatingSystem.IsWindows() ||
            !Update.WindowsApplyHelper.IsInstalled(Environment.GetEnvironmentVariable(Update.NodeUpdateOptions.InstallRootVariable)))
        {
            return null;
        }

        return () => Update.WindowsApplyHelper.LaunchAsync(new MonitorApp.Update.Apply.SystemProcessRunner(), CancellationToken.None);
    }

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static bool IsTailscale(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }

    /// <summary>This computer's Tailscale IPv4 address, or null when Tailscale is not up.</summary>
    public static IPAddress? FindTailscaleAddress(Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        var candidates = localAddresses?.Invoke() ?? NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address);
        return candidates.FirstOrDefault(IsTailscale);
    }

    public static bool ReadWantsReach(string dataDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(PathIn(dataDirectory)));
            return document.RootElement.TryGetProperty("reachableByPhone", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Write(string dataDirectory, bool reachableByPhone)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathIn(dataDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { reachableByPhone }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The saved choice resolved to a bind address. When Tailscale is wanted but not up yet (it may start after Monitor App at
    /// boot), waits up to <paramref name="wait"/> for it, then falls back to 127.0.0.1 so the node always starts.
    /// </summary>
    public static IPAddress ResolveSaved(string dataDirectory, TimeSpan wait, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        if (!ReadWantsReach(dataDirectory))
        {
            return IPAddress.Loopback;
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            if (FindTailscaleAddress(localAddresses) is { } address)
            {
                return address;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return IPAddress.Loopback;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }
}

/// <summary>
/// The node's side of the reach switch: reports where the peer listener is, saves a change, and restarts the node so the
/// listener moves. The service manager restarts it (Windows failure actions, systemd Restart=on-failure).
/// </summary>
public sealed class PeerReachController(NodeOptions options, Action<int>? exit = null, Func<Task>? ensureFirewall = null) : IPeerReachController
{
    /// <summary>Exit code the node uses to ask its service manager for a restart.</summary>
    public const int RestartExitCode = 75;

    private readonly Action<int> exit = exit ?? Environment.Exit;
    private volatile bool restartPending;

    public PeerReachStatus GetStatus()
    {
        var tailscale = PeerReachSetting.FindTailscaleAddress();
        var listening = options.PeerBindAddress;
        var reachable = !IPAddress.IsLoopback(listening);
        var canChange = options.PeerBindSource is "setting" or "default";
        string explanation;
        if (!canChange)
        {
            explanation = $"Set by this computer's configuration ({options.PeerBindSource}), so it can't be changed here.";
        }
        else if (reachable)
        {
            explanation = $"Your paired phone can reach this computer at {listening} over Tailscale. Nothing outside your Tailscale network can.";
        }
        else if (PeerReachSetting.ReadWantsReach(options.DataDirectory))
        {
            explanation = "Turned on, but Tailscale wasn't running when Monitor App started. Start Tailscale, then turn this off and on again.";
        }
        else if (tailscale is null)
        {
            explanation = "Only this computer can connect. To let your phone in, install and sign in to Tailscale on this computer and the phone first.";
        }
        else
        {
            explanation = "Only this computer can connect. Turn this on to let your phone connect over Tailscale.";
        }

        return new PeerReachStatus(reachable, listening.ToString(), tailscale?.ToString(), options.PeerBindSource, canChange, restartPending, explanation);
    }

    public PeerReachChange Change(bool reachableByPhone)
    {
        if (options.PeerBindSource is not ("setting" or "default"))
        {
            return new PeerReachChange(false, "This computer's configuration sets the address; change it there.", GetStatus());
        }

        if (reachableByPhone && PeerReachSetting.FindTailscaleAddress() is null)
        {
            return new PeerReachChange(false, "Tailscale isn't running on this computer. Install it and sign in, then try again.", GetStatus());
        }

        PeerReachSetting.Write(options.DataDirectory, reachableByPhone);
        restartPending = true;
        // Answer first, then restart so the listener moves.
        _ = Task.Run(async () =>
        {
            if (reachableByPhone && ensureFirewall is not null)
            {
                try
                {
                    // Windows: the SYSTEM helper task adds the Tailscale-only inbound rule (the service account cannot).
                    await ensureFirewall().ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    // The listener still moves; the status page then says the phone could not connect.
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            exit(RestartExitCode);
        });
        return new PeerReachChange(true, null, GetStatus() with
        {
            Explanation = "Saved. Monitor App is restarting to apply it; this takes a few seconds.",
        });
    }
}
