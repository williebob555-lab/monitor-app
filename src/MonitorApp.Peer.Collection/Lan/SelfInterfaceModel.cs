using System.Globalization;
using System.Net;

namespace MonitorApp.Peer.Collection.Lan;

public enum InterfaceMedium
{
    Ethernet = 1,
    WiFi = 2,
    Loopback = 3,
    Overlay = 4,
    Bridge = 5,
    Virtual = 6,
    Vlan = 7,
    Other = 8,
}

/// <summary>An interface of the local peer's own host. Never describes other hosts.</summary>
public sealed record SelfInterface(
    string Name,
    InterfaceMedium Medium,
    bool IsUp,
    long? SpeedMbps,
    string? Duplex,
    int? Mtu,
    string? Mac,
    IReadOnlyList<string> Subnets,
    int? VlanId,
    string? VlanParent)
{
    /// <summary>True only for hardware-backed Ethernet or Wi-Fi; overlay, loopback, bridge, virtual and VLAN sub-interfaces are not physical.</summary>
    public bool IsPhysical => Medium is InterfaceMedium.Ethernet or InterfaceMedium.WiFi;

    public bool IsOverlay => Medium == InterfaceMedium.Overlay;
}

/// <summary>Default gateway address as seen in the peer's own routing table. The IP is a weak claim only.</summary>
public sealed record SelfGateway(string Address, string InterfaceName);

public enum SelfCollectionState
{
    Collected = 1,
    Unsupported = 2,
    Failed = 3,
}

public sealed record SelfInterfaceSnapshot(
    SelfCollectionState State,
    string? Reason,
    IReadOnlyList<SelfInterface> Interfaces,
    IReadOnlyList<SelfGateway> Gateways,
    DateTimeOffset CollectedAt)
{
    public static SelfInterfaceSnapshot Unsupported(string reason, DateTimeOffset at) =>
        new(SelfCollectionState.Unsupported, reason, [], [], at);

    public static SelfInterfaceSnapshot Failed(string reason, DateTimeOffset at) =>
        new(SelfCollectionState.Failed, reason, [], [], at);
}

public interface ISelfInterfaceCollector
{
    SelfInterfaceSnapshot Collect(DateTimeOffset now);
}

public static class SelfInterfaceParsers
{
    /// <summary>Parses /proc/net/route, returning default routes (destination 0.0.0.0, gateway non-zero, RTF_UP|RTF_GATEWAY).</summary>
    public static IReadOnlyList<SelfGateway> ParseDefaultRoutes(string text)
    {
        var result = new List<SelfGateway>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 8 || f[1] != "00000000" || f[7] != "00000000") continue;
            if (!uint.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gw) || gw == 0) continue;
            if (!uint.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) || (flags & 0x3) != 0x3) continue;
            var b = BitConverter.GetBytes(gw); // /proc/net/route is host-endian hex; little-endian hosts store the first octet lowest.
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            result.Add(new SelfGateway(new IPAddress(b).ToString(), f[0]));
        }

        return result;
    }

    /// <summary>Parses /proc/net/vlan/config into interface name to (id, parent).</summary>
    public static IReadOnlyDictionary<string, (int Id, string Parent)> ParseVlanConfig(string text)
    {
        var result = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|', StringSplitOptions.TrimEntries);
            if (f.Length == 3 && int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 1 and <= 4094)
            {
                result[f[0]] = (id, f[2]);
            }
        }

        return result;
    }

    public static string? ParseUeventValue(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal)) return line[(key.Length + 1)..].Trim();
        }

        return null;
    }

    /// <summary>Normalizes to lowercase colon form; rejects all-zero and malformed values.</summary>
    public static string? NormalizeMac(string? raw)
    {
        if (raw is null) return null;
        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12 || hex.All(c => c == '0')) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public static InterfaceMedium ClassifyByName(string name, string? devType)
    {
        var n = name.ToLowerInvariant();
        if (n == "lo" || n.StartsWith("loopback", StringComparison.Ordinal)) return InterfaceMedium.Loopback;
        if (n.StartsWith("tailscale", StringComparison.Ordinal) || n.StartsWith("wg", StringComparison.Ordinal)
            || n.StartsWith("utun", StringComparison.Ordinal) || n.Contains("wireguard", StringComparison.Ordinal)
            || devType == "wireguard") return InterfaceMedium.Overlay;
        if (devType == "vlan") return InterfaceMedium.Vlan;
        if (devType == "bridge" || n.StartsWith("br-", StringComparison.Ordinal) || n.StartsWith("br", StringComparison.Ordinal) && n.Length > 2 && char.IsDigit(n[2])
            || n.StartsWith("docker", StringComparison.Ordinal) || n.StartsWith("virbr", StringComparison.Ordinal)) return InterfaceMedium.Bridge;
        if (n.StartsWith("veth", StringComparison.Ordinal) || n.StartsWith("tun", StringComparison.Ordinal)
            || n.StartsWith("tap", StringComparison.Ordinal) || n.StartsWith("vmnet", StringComparison.Ordinal)) return InterfaceMedium.Virtual;
        return InterfaceMedium.Other;
    }
}
