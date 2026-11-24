using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection.Lan;

namespace MonitorApp.Peer.Collection.Discovery;

/// <summary>One device found on this computer's network, merged from every source that mentioned it.</summary>
public sealed record DiscoveredDevice(
    string Mac,
    IPAddress Address,
    string Network,
    string Name,
    string? Maker,
    string? Model,
    string Kind,
    IReadOnlyList<string> Offers,
    bool Answered,
    bool IsGateway,
    string Sources);

/// <summary>What one discovery pass found, and what it did, in words the screen can show.</summary>
public sealed record DiscoveryPass(DateTimeOffset At, IReadOnlyList<DiscoveredDevice> Devices, IReadOnlyList<string> Networks, bool Swept, string? Problem);

/// <summary>
/// Network discovery (D1), run only while the computer's "Find devices on this network" switch is on. It looks at this
/// computer's own private subnets only (at most a /22 each): the neighbor table, a slow ping sweep (every 30 minutes,
/// 16 at a time), mDNS and SSDP on the local link, each device's own UPnP description, and the local DNS name. It never
/// logs in to anything, never scans ports, and never trusts a found device: found devices are only described.
/// </summary>
public sealed class NetworkDiscovery(
    ISelfInterfaceCollector interfaces,
    INeighborTable neighbors,
    ILocalNetworkProbes probes,
    TimeProvider? time = null,
    Func<IReadOnlyList<IPAddress>>? ownAddresses = null)
{
    public const string CollectorId = "monitor.discovery";
    public const string SubjectPrefix = "net:";
    public const int MaximumPrefix = 22;
    public const int MaximumDevices = 512;
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NameLifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Dictionary<IPAddress, (string? Name, DateTimeOffset At)> names = [];
    private DateTimeOffset lastSweep = DateTimeOffset.MinValue;

    public DiscoveryPass? Last { get; private set; }

    public static string SubjectFor(string mac) => SubjectPrefix + mac.Replace(":", string.Empty, StringComparison.Ordinal);

    public async Task<DiscoveryPass> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var snapshot = interfaces.Collect(now);
        var networks = snapshot.Interfaces
            .Where(i => i.IsPhysical && i.IsUp)
            .SelectMany(i => i.Subnets)
            .Where(c => !SubnetMath.IsOverlayRange(c))
            .Select(Cidr.TryParse)
            .OfType<Cidr>()
            .Where(c => c.Prefix >= MaximumPrefix && c.Prefix <= 30 && c.IsPrivate)
            .Distinct()
            .ToList();
        if (networks.Count == 0)
        {
            return Last = new DiscoveryPass(now, [], [], false, "No private home network is connected on this computer.");
        }

        bool IsLocal(IPAddress ip) => networks.Any(n => n.Contains(ip));
        var mine = (ownAddresses ?? OwnAddresses)().ToHashSet();
        var gateways = snapshot.Gateways.Select(g => IPAddress.TryParse(g.Address, out var ip) ? ip : null).OfType<IPAddress>().ToHashSet();

        var answered = new HashSet<IPAddress>();
        var swept = false;
        if (now - lastSweep >= SweepInterval)
        {
            var targets = networks.SelectMany(n => n.Hosts()).Where(ip => !mine.Contains(ip)).Distinct().ToList();
            answered.UnionWith(await probes.PingAsync(targets, cancellationToken).ConfigureAwait(false));
            lastSweep = now;
            swept = true;
        }

        var descriptions = new List<SelfDescription>();
        descriptions.AddRange(await Safe(() => probes.MdnsAsync(cancellationToken)).ConfigureAwait(false));
        descriptions.AddRange(await Safe(() => probes.SsdpAsync(IsLocal, cancellationToken)).ConfigureAwait(false));
        descriptions.RemoveAll(d => !IsLocal(d.Address) || mine.Contains(d.Address));
        answered.UnionWith(descriptions.Select(d => d.Address));

        var table = neighbors.Read()
            .Where(n => IsLocal(n.Address) && !mine.Contains(n.Address))
            .GroupBy(n => n.Address)
            .Select(g => g.First())
            .Take(MaximumDevices)
            .ToList();

        var devices = new List<DiscoveredDevice>();
        foreach (var neighbor in table)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var about = descriptions.Where(d => d.Address.Equals(neighbor.Address)).ToList();
            var dns = await NameAsync(neighbor.Address, now, cancellationToken).ConfigureAwait(false);
            devices.Add(Merge(neighbor, networks.First(n => n.Contains(neighbor.Address)).ToString(), about, dns,
                answered.Contains(neighbor.Address), gateways.Contains(neighbor.Address)));
        }

        // Two addresses with one MAC (a router with two IPs) are one device: keep the gateway or the first.
        devices = [.. devices.GroupBy(d => d.Mac).Select(g => g.OrderByDescending(d => d.IsGateway).First())];
        return Last = new DiscoveryPass(now, devices, [.. networks.Select(n => n.ToString())], swept, null);
    }

    /// <summary>The readings for a pass: one subject per device, plus a summary on this computer.</summary>
    public static IReadOnlyList<SubjectReadings> Readings(string selfSubject, DiscoveryPass pass)
    {
        var result = new List<SubjectReadings>();
        foreach (var device in pass.Devices)
        {
            var metrics = new List<CollectedMetric>
            {
                Text(DiscoverySignals.Name, device.Name),
                Text(DiscoverySignals.Address, device.Address.ToString()),
                Text(DiscoverySignals.Mac, device.Mac),
                Text(DiscoverySignals.Network, device.Network),
                Text(DiscoverySignals.Kind, device.Kind, ObservationQuality.Estimated),
                Text(DiscoverySignals.Found, device.Sources),
                Flag(DiscoverySignals.Answered, device.Answered),
                Flag(DiscoverySignals.Gateway, device.IsGateway),
            };
            if (device.Maker is not null) metrics.Add(Text(DiscoverySignals.Maker, device.Maker));
            if (device.Model is not null) metrics.Add(Text(DiscoverySignals.Model, device.Model));
            if (device.Offers.Count > 0) metrics.Add(Text(DiscoverySignals.Offers, string.Join(", ", device.Offers)));
            result.Add(new SubjectReadings(SubjectFor(device.Mac), metrics));
        }

        var summary = new List<CollectedMetric>
        {
            new(DiscoverySignals.DevicesFound, MetricUnit.Count, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = (ulong)pass.Devices.Count }),
            new(DiscoverySignals.DevicesAnswered, MetricUnit.Count, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = (ulong)pass.Devices.Count(d => d.Answered) }),
            Text(DiscoverySignals.Networks, pass.Networks.Count == 0 ? pass.Problem ?? "none" : string.Join(", ", pass.Networks)),
        };
        result.Add(new SubjectReadings(selfSubject, summary));
        return result;
    }

    private static DiscoveredDevice Merge(Neighbor neighbor, string network, List<SelfDescription> about, string? dns, bool answered, bool gateway)
    {
        var vendor = MacVendors.Lookup(neighbor.Mac);
        var maker = about.Select(a => a.Maker).FirstOrDefault(m => m is not null) ?? vendor;
        var model = about.Select(a => a.Model).FirstOrDefault(m => m is not null);
        var offers = about.SelectMany(a => a.Services ?? []).Where(s => s != "UPnP" || about.Count == 1).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var host = ShortHost(dns) ?? about.Select(a => ShortHost(a.HostName)).FirstOrDefault(h => h is not null);
        var friendly = about.Select(a => a.FriendlyName).FirstOrDefault(f => f is not null);
        var kind = GuessKind(gateway, offers, about.Select(a => a.DeviceType).OfType<string>(), vendor, maker);
        var name = friendly ?? host ?? (gateway ? $"Router ({Maker(vendor)})" : $"{Maker(vendor)} {KindWord(kind)}".Trim());
        var sources = new List<string> { "neighbor table" };
        if (answered) sources.Add("ping");
        if (about.Any(a => a.HostName is not null || a.Services is { Count: > 0 } && a.DeviceType is null)) sources.Add("mDNS");
        if (about.Any(a => a.DeviceType is not null || a.Services?.Contains("UPnP") == true)) sources.Add("UPnP");
        if (dns is not null) sources.Add("DNS");
        return new DiscoveredDevice(neighbor.Mac, neighbor.Address, network, name, maker, model, kind, offers, answered, gateway, string.Join(", ", sources));
    }

    private static string Maker(string? vendor) => vendor switch
    {
        null => "Unknown",
        MacVendors.PrivateAddress => "Private",
        _ => vendor,
    };

    /// <summary>A model kind id from what a device offers and who made it. Always an estimate, and recorded as one.</summary>
    public static string GuessKind(bool gateway, IReadOnlyCollection<string> offers, IEnumerable<string> deviceTypes, string? vendor, string? maker)
    {
        bool Offers(params string[] words) => words.Any(offers.Contains);
        bool Made(params string[] names) => names.Any(n => (vendor ?? string.Empty).Contains(n, StringComparison.OrdinalIgnoreCase) || (maker ?? string.Empty).Contains(n, StringComparison.OrdinalIgnoreCase));
        if (gateway || Offers("router") || deviceTypes.Any(t => t.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase))) return "device.network.router";
        if (Offers("printing", "scanning")) return "device.printer";
        if (Offers("Chromecast", "Fire TV", "Android TV", "media player") || Made("Roku", "Vizio", "TCL", "Hisense", "LG Electronics")) return "device.media";
        if (Offers("Sonos", "Spotify Connect", "AirPlay audio") || Made("Sonos", "Bose")) return "device.audio.speaker";
        if (Offers("HomeKit", "Matter", "ESPHome", "Home Assistant") || Made("Espressif", "Tuya", "Shelly", "Signify", "Philips Lighting", "Ecobee", "Nest")) return "device.smart-home";
        if (Made("Cisco", "Ubiquiti", "TP-Link", "Netgear", "Aruba", "MikroTik", "Juniper", "D-Link", "Zyxel")) return "device.network";
        if (Offers("computer", "file sharing", "SSH", "game streaming", "Syncthing") || Made("Raspberry Pi", "Intel", "Dell", "Lenovo", "Hewlett", "ASUSTek", "Micro-Star", "Gigabyte", "ASRock", "Realtek")) return "device.computer";
        if (vendor == MacVendors.PrivateAddress) return "device.phone";
        return "device";
    }

    private static string KindWord(string kind) => kind switch
    {
        "device.network.router" => "router",
        "device.printer" => "printer",
        "device.media" => "TV or media player",
        "device.audio.speaker" => "speaker",
        "device.smart-home" => "smart home device",
        "device.network" => "network device",
        "device.computer" => "computer",
        "device.phone" => "phone, tablet or laptop",
        _ => "device",
    };

    private static string? ShortHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var first = host.Split('.')[0].Trim();
        return first.Length is 0 or > 63 ? null : first;
    }

    private async Task<string?> NameAsync(IPAddress address, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (names.TryGetValue(address, out var cached) && now - cached.At < NameLifetime) return cached.Name;
        var name = await probes.ReverseNameAsync(address, cancellationToken).ConfigureAwait(false);
        names[address] = (name, now);
        return name;
    }

    private static async Task<IReadOnlyList<SelfDescription>> Safe(Func<Task<IReadOnlyList<SelfDescription>>> probe)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException or HttpRequestException)
        {
            return []; // a blocked multicast or a firewall only means less detail
        }
    }

    private static IReadOnlyList<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static CollectedMetric Text(string id, string value, ObservationQuality quality = ObservationQuality.Valid) =>
        new(id, MetricUnit.Unspecified, quality, new MetricValue { StringValue = value });

    private static CollectedMetric Flag(string id, bool value) =>
        new(id, MetricUnit.Boolean, ObservationQuality.Valid, new MetricValue { BooleanValue = value });
}

/// <summary>Signal ids discovery writes. Device readings use the device's own subject (net:{mac}).</summary>
public static class DiscoverySignals
{
    public const string Name = "net.device.name";
    public const string Address = "net.device.address";
    public const string Mac = "net.device.mac";
    public const string Maker = "net.device.maker";
    public const string Model = "net.device.model";
    public const string Kind = "net.device.kind";
    public const string Offers = "net.device.offers";
    public const string Network = "net.device.network";
    public const string Answered = "net.device.answered";
    public const string Gateway = "net.device.gateway";
    public const string Found = "net.device.found_by";
    public const string Os = "net.device.os";
    public const string DevicesFound = "net.discovery.devices_found";
    public const string DevicesAnswered = "net.discovery.devices_answered";
    public const string Networks = "net.discovery.networks";
}

/// <summary>An IPv4 network in CIDR form.</summary>
public sealed record Cidr(uint Network, int Prefix)
{
    public static Cidr? TryParse(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !IPAddress.TryParse(text[..slash], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(text.AsSpan(slash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix) || prefix is < 0 or > 32)
            return null;
        return new Cidr(ToUInt(ip) & Mask(prefix), prefix);
    }

    public bool IsPrivate =>
        (Network & 0xFF000000) == 0x0A000000 || (Network & 0xFFF00000) == 0xAC100000 || (Network & 0xFFFF0000) == 0xC0A80000;

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt(address) & Mask(Prefix)) == Network;

    /// <summary>Every usable host address (no network or broadcast address).</summary>
    public IEnumerable<IPAddress> Hosts()
    {
        var size = 1u << (32 - Prefix);
        for (var i = 1u; i < size - 1; i++) yield return FromUInt(Network + i);
    }

    public override string ToString() => $"{FromUInt(Network)}/{Prefix}";

    private static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
