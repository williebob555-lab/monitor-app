using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonitorApp.Peer.Collection.Lan;

public static class SubnetMath
{
    /// <summary>Returns the network in CIDR form for an IPv4 unicast address, or null for loopback, link-local, unspecified, or IPv6.</summary>
    public static string? Ipv4Network(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 1 or > 32) return null;
        var b = address.GetAddressBytes();
        if (b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254)) return null;
        var value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var mask = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
        var net = value & mask;
        return string.Create(CultureInfo.InvariantCulture, $"{net >> 24}.{net >> 16 & 255}.{net >> 8 & 255}.{net & 255}/{prefixLength}");
    }

    /// <summary>Carrier-grade NAT space (100.64.0.0/10) is where Tailscale assigns overlay addresses.</summary>
    public static bool IsOverlayRange(string cidr) =>
        cidr.StartsWith("100.", StringComparison.Ordinal) && int.TryParse(cidr.Split('.')[1], NumberStyles.None, CultureInfo.InvariantCulture, out var o) && o is >= 64 and <= 127;
}

/// <summary>Reads the host's own subnets from the OS. IPv4 only; the peer's own addresses, never neighbors.</summary>
public static class OwnSubnetReader
{
    public static IReadOnlyList<string> Read(string interfaceName)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == interfaceName);
        return nic is null ? [] : FromNic(nic);
    }

    public static IReadOnlyList<string> FromNic(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses
            .Select(a => SubnetMath.Ipv4Network(a.Address, a.PrefixLength))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>Linux: sysfs and procfs file parsing; no external tools and no neighbor/ARP tables.</summary>
public sealed class LinuxSelfInterfaceCollector(
    string sysClassNet = "/sys/class/net",
    string procRoute = "/proc/net/route",
    string procVlanConfig = "/proc/net/vlan/config",
    Func<string, IReadOnlyList<string>>? subnetReader = null) : ISelfInterfaceCollector
{
    private readonly Func<string, IReadOnlyList<string>> subnets = subnetReader ?? OwnSubnetReader.Read;

    public SelfInterfaceSnapshot Collect(DateTimeOffset now)
    {
        if (!Directory.Exists(sysClassNet)) return SelfInterfaceSnapshot.Unsupported($"{sysClassNet} is not available.", now);
        try
        {
            // /proc/net/vlan/config is root-only (0600) on many hosts: unreadable means "VLAN membership unknown", not a failed collection.
            var vlanText = ReadFile(procVlanConfig);
            var vlans = vlanText is null ? new Dictionary<string, (int, string)>() : SelfInterfaceParsers.ParseVlanConfig(vlanText);
            var list = new List<SelfInterface>();
            foreach (var dir in Directory.EnumerateDirectories(sysClassNet).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var uevent = Read(dir, "uevent") ?? string.Empty;
                var devType = SelfInterfaceParsers.ParseUeventValue(uevent, "DEVTYPE");
                var medium = SelfInterfaceParsers.ClassifyByName(name, devType);
                int? vlanId = null; string? parent = null;
                if (vlans.TryGetValue(name, out var v)) { medium = InterfaceMedium.Vlan; vlanId = v.Id; parent = v.Parent; }
                if (medium == InterfaceMedium.Other)
                {
                    var backed = Directory.Exists(Path.Combine(dir, "device")) || File.Exists(Path.Combine(dir, "device"));
                    if (devType == "wlan" || Directory.Exists(Path.Combine(dir, "wireless")) || Directory.Exists(Path.Combine(dir, "phy80211"))) medium = InterfaceMedium.WiFi;
                    else medium = backed ? InterfaceMedium.Ethernet : InterfaceMedium.Virtual;
                }

                var oper = Read(dir, "operstate")?.Trim();
                var speed = long.TryParse(Read(dir, "speed")?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : (long?)null;
                var mtu = int.TryParse(Read(dir, "mtu")?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
                var duplex = Read(dir, "duplex")?.Trim();
                list.Add(new SelfInterface(
                    name, medium, oper == "up", speed,
                    duplex is "full" or "half" ? duplex : null, mtu,
                    medium == InterfaceMedium.Loopback ? null : SelfInterfaceParsers.NormalizeMac(Read(dir, "address")),
                    medium == InterfaceMedium.Overlay ? [] : subnets(name), vlanId, parent));
            }

            var routeText = ReadFile(procRoute);
            var gateways = routeText is null ? [] : SelfInterfaceParsers.ParseDefaultRoutes(routeText);
            return new SelfInterfaceSnapshot(SelfCollectionState.Collected, null, list, gateways, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SelfInterfaceSnapshot.Failed($"Reading {sysClassNet} failed: {ex.GetType().Name}.", now);
        }
    }

    private static string? Read(string dir, string file) => ReadFile(Path.Combine(dir, file));

    private static string? ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } // e.g. speed on a down link
    }
}

/// <summary>Windows: System.Net.NetworkInformation. Duplex and VLAN membership are not exposed and stay unknown.</summary>
public sealed class WindowsSelfInterfaceCollector : ISelfInterfaceCollector
{
    public SelfInterfaceSnapshot Collect(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) return SelfInterfaceSnapshot.Unsupported("Windows interface collection runs only on Windows.", now);
        try
        {
            var list = new List<SelfInterface>();
            var gateways = new List<SelfGateway>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().OrderBy(n => n.Name, StringComparer.Ordinal))
            {
                if (IsWindowsPlumbing(nic.NetworkInterfaceType, nic.Name, nic.Description))
                {
                    continue;
                }

                var medium = ClassifyWindows(nic.NetworkInterfaceType, nic.Name, nic.Description);
                var up = nic.OperationalStatus == OperationalStatus.Up;
                var props = nic.GetIPProperties();
                int? mtu = null;
                try { mtu = props.GetIPv4Properties()?.Mtu; } catch (NetworkInformationException) { }
                list.Add(new SelfInterface(nic.Name, medium, up, up && nic.Speed > 0 ? nic.Speed / 1_000_000 : null, null, mtu,
                    medium is InterfaceMedium.Loopback ? null : SelfInterfaceParsers.NormalizeMac(nic.GetPhysicalAddress().ToString()),
                    medium == InterfaceMedium.Overlay ? [] : OwnSubnetReader.FromNic(nic), null, null));
                if (up && medium != InterfaceMedium.Overlay)
                {
                    gateways.AddRange(props.GatewayAddresses
                        .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        .Select(g => new SelfGateway(g.Address.ToString(), nic.Name)));
                }
            }

            return new SelfInterfaceSnapshot(SelfCollectionState.Collected, null, list, gateways, now);
        }
        catch (NetworkInformationException ex)
        {
            return SelfInterfaceSnapshot.Failed($"Network interface query failed: {ex.ErrorCode}.", now);
        }
    }

    /// <summary>
    /// Windows lists driver plumbing alongside real adapters: a copy of each adapter per filter driver bound to it
    /// ("Wi-Fi-QoS Packet Scheduler-0000", "...-WFP Native MAC Layer LightWeight Filter-0000"), the hidden Wi-Fi Direct
    /// adapters ("Local Area Connection* 6"), IPv6 transition tunnels (6to4, Teredo, ISATAP) and the kernel debugger.
    /// None of them is a link a person has or can act on, so they are not collected at all.
    /// </summary>
    public static bool IsWindowsPlumbing(NetworkInterfaceType type, string name, string description)
    {
        var text = $"{name} {description}";
        string[] markers =
        [
            "QoS Packet Scheduler", "LightWeight Filter", "WFP ", "Native WiFi Filter", "Virtual WiFi Filter", "Npcap Packet Driver",
            "Local Area Connection*", "6to4", "Teredo", "isatap", "IP-HTTPS", "Kernel Debug", "WAN Miniport",
        ];
        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))
            || (type == NetworkInterfaceType.Tunnel && ClassifyWindows(type, name, description) != InterfaceMedium.Overlay);
    }

    public static InterfaceMedium ClassifyWindows(NetworkInterfaceType type, string name, string description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        if (type == NetworkInterfaceType.Loopback) return InterfaceMedium.Loopback;
        if (text.Contains("tailscale", StringComparison.Ordinal) || text.Contains("wireguard", StringComparison.Ordinal) || text.Contains("wintun", StringComparison.Ordinal)) return InterfaceMedium.Overlay;
        if (text.Contains("hyper-v", StringComparison.Ordinal) || text.Contains("vethernet", StringComparison.Ordinal) || text.Contains("virtualbox", StringComparison.Ordinal)
            || text.Contains("vmware", StringComparison.Ordinal) || text.Contains("docker", StringComparison.Ordinal) || text.Contains("wsl", StringComparison.Ordinal)) return InterfaceMedium.Virtual;
        return type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => InterfaceMedium.Ethernet,
            NetworkInterfaceType.Wireless80211 => InterfaceMedium.WiFi,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => InterfaceMedium.Virtual,
            _ => InterfaceMedium.Other,
        };
    }
}

public static class SelfInterfaceCollectorFactory
{
    public static ISelfInterfaceCollector Create() =>
        OperatingSystem.IsWindows() ? new WindowsSelfInterfaceCollector()
        : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? new LinuxSelfInterfaceCollector()
        : new UnsupportedSelfInterfaceCollector();
}

/// <summary>Truthful fallback for platforms without a collector (Android reports only coarse transport via Android.Core; macOS is not a target).</summary>
public sealed class UnsupportedSelfInterfaceCollector : ISelfInterfaceCollector
{
    public SelfInterfaceSnapshot Collect(DateTimeOffset now) =>
        SelfInterfaceSnapshot.Unsupported("No self-interface collector exists for this platform.", now);
}
