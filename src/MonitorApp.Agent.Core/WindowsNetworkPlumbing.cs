using System.Net.NetworkInformation;

namespace MonitorApp.Agent.Core;

/// <summary>
/// Windows lists driver plumbing alongside real adapters: a copy of each adapter per filter driver bound to it
/// ("Wi-Fi-QoS Packet Scheduler-0000", "...-WFP Native MAC Layer LightWeight Filter-0000"), the hidden Wi-Fi Direct
/// adapters ("Local Area Connection* 6"), IPv6 transition tunnels (6to4, Teredo, ISATAP) and the kernel debugger.
/// None of them is a link a person has or can act on, and the filter copies repeat the traffic of the adapter they sit
/// on, so counting them would inflate traffic totals. Collectors skip them.
/// </summary>
public static class WindowsNetworkPlumbing
{
    private static readonly string[] Markers =
    [
        "QoS Packet Scheduler", "LightWeight Filter", "WFP ", "Native WiFi Filter", "Virtual WiFi Filter", "Npcap Packet Driver",
        "Local Area Connection*", "6to4", "Teredo", "isatap", "IP-HTTPS", "Kernel Debug", "WAN Miniport",
    ];

    private static readonly string[] Overlays = ["tailscale", "wireguard", "wintun"];

    public static bool IsPlumbing(NetworkInterfaceType type, string name, string description)
    {
        var text = $"{name} {description}";
        if (Overlays.Any(o => text.Contains(o, StringComparison.OrdinalIgnoreCase)))
        {
            return false; // a VPN is a real link even though Windows calls it a tunnel.
        }

        return type == NetworkInterfaceType.Tunnel || Markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
