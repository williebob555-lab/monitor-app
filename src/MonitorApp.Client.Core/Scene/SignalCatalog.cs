using System.Globalization;
using System.Text.RegularExpressions;
using MonitorApp.Client.Core.ViewModels;

namespace MonitorApp.Client.Core.Scene;

/// <summary>Signal ids the peers publish today (see the platform collectors and the Android adapter).</summary>
public static class SignalIds
{
    public const string CpuUtilization = "system.cpu.utilization";
    public const string MemoryUsed = "system.memory.used";
    public const string MemoryTotal = "system.memory.total";
    public const string StorageFree = "system.storage.free";
    public const string StorageTotal = "system.storage.total";
    public const string NetworkBytesSent = "system.network.bytes_sent";
    public const string NetworkBytesReceived = "system.network.bytes_received";
    public const string BatteryLevel = "android.battery.level";
    public const string BatteryCharging = "android.battery.charging";
    public const string NetworkConnected = "android.network.connected";
    public const string NetworkVpn = "android.network.vpn";
}

/// <summary>What the catalog knows about one signal id.</summary>
/// <param name="IsKnown">False for ids no definition matches; they are shown under <see cref="SceneLayerKind.Other"/> with a readable label made from the id.</param>
public sealed record SignalDescriptor(string SignalId, SceneLayerKind Kind, string Label, bool IsKnown)
{
    /// <summary>Words for a true/false value ("Charging" / "On battery"); null uses "Yes" / "No".</summary>
    public (string True, string False)? Flag { get; init; }

    /// <summary>Words for text values the collector sends as codes ("Available" becomes "Working").</summary>
    public IReadOnlyDictionary<string, string>? Words { get; init; }

    /// <summary>When set, numbers are shown exactly with this word ("1500 bytes") instead of the unit's usual scaling.</summary>
    public string? Suffix { get; init; }
}

/// <summary>
/// One catalog entry. <see cref="Pattern"/> is a signal id where <c>{name}</c> stands for any part of the id (it may contain dots,
/// so a VLAN interface "eth0.10" still matches). <see cref="Label"/> may use the same placeholders; their values are shown through
/// <see cref="SignalCatalog.Names"/> ("wifi" becomes "Wi-Fi"). New collectors add entries with <see cref="SignalCatalog.Register"/>
/// instead of editing code that formats values.
/// </summary>
public sealed record SignalDefinition(string Pattern, SceneLayerKind Kind, string Label)
{
    public (string True, string False)? Flag { get; init; }

    public IReadOnlyDictionary<string, string>? Words { get; init; }

    public string? Suffix { get; init; }
}

/// <summary>
/// Maps signal ids to the layer they belong to, a plain-English label, and exact value formatting. The catalog only describes how to
/// show what a peer reported; it never supplies a value. It is data, not a switch: exact ids and patterns are looked up in the order
/// registered, later registrations win, and ids nothing matches get a readable label made from the id itself.
/// </summary>
public static class SignalCatalog
{
    private static readonly IReadOnlyDictionary<string, string> CollectorWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Available"] = "Working",
        ["Degraded"] = "Partly working",
        ["Unsupported"] = "Not supported on this device",
        ["PermissionDenied"] = "No permission",
        ["Error"] = "Not working",
    };

    private static readonly IReadOnlyDictionary<string, string> TailscaleWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Running"] = "Connected",
        ["Stopped"] = "Turned off",
        ["NeedsLogin"] = "Needs sign-in",
        ["NeedsMachineAuth"] = "Waiting for approval in the admin console",
        ["Starting"] = "Starting",
        ["NoState"] = "Not started",
    };

    private static readonly IReadOnlyDictionary<string, string> SyncthingWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["up to date"] = "Up to date",
        ["out of sync"] = "Out of sync",
        ["error"] = "Error",
        ["syncing"] = "Syncing",
        ["scanning"] = "Scanning",
        ["sync-preparing"] = "Preparing to sync",
        ["scan-waiting"] = "Waiting to scan",
        ["sync-waiting"] = "Waiting to sync",
    };

    /// <summary>Plain words for the kind a found device looks like (always an estimate).</summary>
    public static IReadOnlyDictionary<string, string> KindWords { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["device.network.router"] = "Router",
        ["device.printer"] = "Printer",
        ["device.media"] = "TV or media player",
        ["device.audio.speaker"] = "Speaker",
        ["device.smart-home"] = "Smart home device",
        ["device.network"] = "Network device",
        ["device.computer"] = "Computer",
        ["device.phone"] = "Phone, tablet or laptop",
        ["device"] = "Device",
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SignalDescriptor> Cache = new(StringComparer.Ordinal);
    private static readonly List<(SignalDefinition Definition, Regex Matcher, string[] Slots)> Definitions = [];

    /// <summary>
    /// Plain names for the parts of ids that placeholders capture (interface and collector names). Unlisted parts are shown as
    /// sent, except that underscores become spaces.
    /// </summary>
    public static IDictionary<string, string> Names { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["wifi"] = "Wi-Fi",
        ["cellular"] = "Mobile data",
        ["vpn"] = "VPN",
        ["ethernet"] = "Ethernet",
        ["bluetooth"] = "Bluetooth",
        ["battery"] = "Battery",
        ["network"] = "Network",
        ["lan"] = "Network link",
        ["hardware"] = "Hardware",
        ["discovery"] = "Network discovery",
        ["cpu"] = "Processor",
        ["memory"] = "Memory",
        ["storage"] = "Storage",
        ["system"] = "System",

        // Service ids (KnownServices on the peer) read as their product names.
        ["tailscale"] = "Tailscale",
        ["syncthing"] = "Syncthing",
        ["ssh"] = "SSH server",
        ["docker"] = "Docker",
        ["podman"] = "Podman",
        ["samba"] = "File sharing (Samba)",
        ["nginx"] = "nginx",
        ["caddy"] = "Caddy",
        ["apache"] = "Apache",
        ["jellyfin"] = "Jellyfin",
        ["plex"] = "Plex",
        ["home-assistant"] = "Home Assistant",
        ["postgres"] = "PostgreSQL",
        ["mysql"] = "MySQL / MariaDB",
        ["ollama"] = "Ollama",
        ["nextcloud"] = "Nextcloud",
        ["onedrive"] = "OneDrive",
        ["dropbox"] = "Dropbox",
        ["steam"] = "Steam",
        ["discord"] = "Discord",
    };

    static SignalCatalog()
    {
        Register(new(SignalIds.CpuUtilization, SceneLayerKind.Compute, "CPU utilization"));
        Register(new(SignalIds.MemoryUsed, SceneLayerKind.Memory, "Memory used"));
        Register(new(SignalIds.MemoryTotal, SceneLayerKind.Memory, "Memory total"));
        Register(new(SignalIds.StorageFree, SceneLayerKind.Storage, "Storage free"));
        Register(new(SignalIds.StorageTotal, SceneLayerKind.Storage, "Storage total"));
        Register(new(SignalIds.NetworkBytesSent, SceneLayerKind.Network, "Data sent (total)"));
        Register(new(SignalIds.NetworkBytesReceived, SceneLayerKind.Network, "Data received (total)"));
        Register(new(SignalIds.BatteryLevel, SceneLayerKind.Power, "Battery level"));
        Register(new(SignalIds.BatteryCharging, SceneLayerKind.Power, "Charging") { Flag = ("Charging", "On battery") });
        Register(new(SignalIds.NetworkConnected, SceneLayerKind.Connectivity, "Internet") { Flag = ("Connected", "Not connected") });
        Register(new(SignalIds.NetworkVpn, SceneLayerKind.Connectivity, "VPN") { Flag = ("VPN on", "VPN off") });

        // The device's own network links (Peer.Collection LanLinkObservations, phone extras in Peer.Mobile).
        Register(new("lan.link.{interface}.up", SceneLayerKind.Connectivity, "{interface} link") { Flag = ("Up", "Down") });
        Register(new("lan.link.{interface}.speed_mbps", SceneLayerKind.Connectivity, "{interface} link speed"));
        Register(new("lan.link.{interface}.full_duplex", SceneLayerKind.Connectivity, "{interface} duplex") { Flag = ("Full", "Half") });
        Register(new("lan.link.{interface}.mtu", SceneLayerKind.Connectivity, "{interface} max packet size") { Suffix = "bytes" });
        Register(new("lan.link.{interface}.metered", SceneLayerKind.Connectivity, "{interface} data plan") { Flag = ("Metered", "Unmetered") });
        Register(new("lan.link.{interface}.bandwidth_down_mbps", SceneLayerKind.Connectivity, "{interface} download estimate"));
        Register(new("lan.link.{interface}.bandwidth_up_mbps", SceneLayerKind.Connectivity, "{interface} upload estimate"));

        // Services this computer runs (ServiceSnapshotCollector) and their own status (Tailscale, Syncthing readers).
        Register(new("service.{service}.running", SceneLayerKind.Services, "{service}") { Flag = ("Running", "Stopped") });
        Register(new("service.{service}.uptime", SceneLayerKind.Services, "{service} running for"));
        Register(new("service.tailscale.connected", SceneLayerKind.Services, "Tailscale connection") { Flag = ("Connected", "Not connected") });
        Register(new("service.tailscale.state", SceneLayerKind.Services, "Tailscale state") { Words = TailscaleWords });
        Register(new("service.tailscale.address", SceneLayerKind.Services, "Tailscale address"));
        Register(new("service.tailscale.devices", SceneLayerKind.Services, "Tailscale devices"));
        Register(new("service.tailscale.devices_online", SceneLayerKind.Services, "Tailscale devices online"));
        Register(new("service.tailscale.exit_node", SceneLayerKind.Services, "Tailscale exit node") { Words = new Dictionary<string, string>(StringComparer.Ordinal) { ["none"] = "None" } });
        Register(new("service.syncthing.folders", SceneLayerKind.Services, "Syncthing folders"));
        Register(new("service.syncthing.folders_up_to_date", SceneLayerKind.Services, "Syncthing folders up to date"));
        Register(new("service.syncthing.folders_with_errors", SceneLayerKind.Services, "Syncthing folders with errors"));
        Register(new("service.syncthing.devices", SceneLayerKind.Services, "Syncthing devices"));
        Register(new("service.syncthing.devices_connected", SceneLayerKind.Services, "Syncthing devices connected"));
        Register(new("service.syncthing.folder.{folder}.state", SceneLayerKind.Services, "Syncthing folder {folder}") { Words = SyncthingWords });

        // What a computer says about itself (Peer.Collection HardwareSnapshotCollector): measured, from the machine.
        Register(new("hardware.maker", SceneLayerKind.Description, "Maker"));
        Register(new("hardware.model", SceneLayerKind.Description, "Model"));
        Register(new("hardware.cpu.model", SceneLayerKind.Description, "Processor"));
        Register(new("hardware.cpu.cores", SceneLayerKind.Description, "Processor cores"));
        Register(new("hardware.cpu.threads", SceneLayerKind.Description, "Processor threads"));
        Register(new("hardware.memory.installed", SceneLayerKind.Description, "Memory installed"));
        Register(new("hardware.gpu.{index}.model", SceneLayerKind.Description, "Graphics {index}"));
        Register(new("hardware.drive.{index}.model", SceneLayerKind.Description, "Drive {index}"));
        Register(new("hardware.drive.{index}.size", SceneLayerKind.Description, "Drive {index} size"));
        Register(new("hardware.drive.{index}.kind", SceneLayerKind.Description, "Drive {index} type"));
        Register(new("host.os.name", SceneLayerKind.Description, "Running system"));
        Register(new("host.systems", SceneLayerKind.Description, "Installed systems"));
        Register(new("host.systems.count", SceneLayerKind.Description, "Installed systems (count)"));
        Register(new("host.boot.default", SceneLayerKind.Description, "Starts by default"));
        Register(new("host.boot.current", SceneLayerKind.Description, "Started this time"));

        // Devices found on the network or the tailnet (Peer.Collection NetworkDiscovery, Tailscale devices): their own subject.
        Register(new("net.device.name", SceneLayerKind.Description, "Name on the network"));
        Register(new("net.device.address", SceneLayerKind.Description, "Address"));
        Register(new("net.device.mac", SceneLayerKind.Description, "Hardware address (MAC)"));
        Register(new("net.device.maker", SceneLayerKind.Description, "Maker"));
        Register(new("net.device.model", SceneLayerKind.Description, "Model"));
        Register(new("net.device.os", SceneLayerKind.Description, "System"));
        Register(new("net.device.kind", SceneLayerKind.Description, "Looks like") { Words = KindWords });
        Register(new("net.device.offers", SceneLayerKind.Description, "Offers"));
        Register(new("net.device.network", SceneLayerKind.Connectivity, "Network"));
        Register(new("net.device.found_by", SceneLayerKind.Connectivity, "Found by"));
        Register(new("net.device.answered", SceneLayerKind.Connectivity, "Answered last check") { Flag = ("Online", "Not answering") });
        Register(new("net.device.gateway", SceneLayerKind.Connectivity, "Router for this network") { Flag = ("Yes", "No") });
        Register(new("net.discovery.devices_found", SceneLayerKind.Connectivity, "Devices found on this network"));
        Register(new("net.discovery.devices_answered", SceneLayerKind.Connectivity, "Devices online on this network"));
        Register(new("net.discovery.networks", SceneLayerKind.Connectivity, "Networks looked at"));

        // Whether each collector is working (PeerSystemMetricsIngestionBridge records collector status as collector.health.{capability}).
        Register(new("collector.health.{collector}", SceneLayerKind.Collection, "{collector} readings") { Words = CollectorWords });
    }

    /// <summary>Adds or replaces a definition. Later registrations take precedence over earlier ones for the ids they match.</summary>
    public static void Register(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var slots = new List<string>();
        var regex = "^" + Regex.Replace(
            Regex.Escape(definition.Pattern).Replace(@"\{", "{", StringComparison.Ordinal),
            @"\{([a-z_]+)}",
            m =>
            {
                slots.Add(m.Groups[1].Value);
                return "(.+)";
            }) + "$";
        lock (Gate)
        {
            Definitions.Insert(0, (definition, new Regex(regex, RegexOptions.CultureInvariant), slots.ToArray()));
            Cache.Clear();
        }
    }

    public static SignalDescriptor Describe(string signalId)
    {
        ArgumentNullException.ThrowIfNull(signalId);
        lock (Gate)
        {
            if (Cache.TryGetValue(signalId, out var cached))
            {
                return cached;
            }

            var descriptor = Resolve(signalId);
            Cache[signalId] = descriptor;
            return descriptor;
        }
    }

    /// <summary>A readable name for an id part: <see cref="Names"/> first, else the part with underscores as spaces.</summary>
    public static string Name(string part) =>
        Names.TryGetValue(part, out var name) ? name : part.Replace('_', ' ');

    private static SignalDescriptor Resolve(string signalId)
    {
        foreach (var (definition, matcher, slots) in Definitions)
        {
            var match = matcher.Match(signalId);
            if (!match.Success)
            {
                continue;
            }

            var label = definition.Label;
            for (var i = 0; i < slots.Length; i++)
            {
                label = label.Replace("{" + slots[i] + "}", Name(match.Groups[i + 1].Value), StringComparison.Ordinal);
            }

            return new SignalDescriptor(signalId, definition.Kind, Capitalize(label), true) { Flag = definition.Flag, Words = definition.Words, Suffix = definition.Suffix };
        }

        return new SignalDescriptor(signalId, SceneLayerKind.Other, Humanize(signalId), false);
    }

    /// <summary>"vendor.fan.speed_rpm" becomes "Fan speed rpm": the vendor/area prefix is dropped and the rest read as words.</summary>
    private static string Humanize(string signalId)
    {
        var parts = signalId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return signalId;
        }

        var words = (parts.Length > 1 ? parts.Skip(1) : parts).Select(Name);
        return Capitalize(string.Join(' ', words));
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Title shown for a layer of this kind.</summary>
    public static string LayerTitle(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Compute => "Processor",
        SceneLayerKind.Memory => "Memory",
        SceneLayerKind.Storage => "Storage",
        SceneLayerKind.Network => "Network traffic",
        SceneLayerKind.Power => "Battery and power",
        SceneLayerKind.Connectivity => "Connectivity",
        SceneLayerKind.Services => "Services",
        SceneLayerKind.Description => "About this device",
        SceneLayerKind.Collection => "Monitoring status",
        _ => "Other readings",
    };

    /// <summary>True when the reading carries a value (it is not "Unavailable").</summary>
    public static bool IsAvailable(SceneReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return !reading.DisplayValue.StartsWith(ValueFormatter.Unavailable, StringComparison.Ordinal);
    }

    /// <summary>Builds the scene reading for a point. Unavailable points never carry a number.</summary>
    public static SceneReading ToReading(HistoryPointDto point)
    {
        var descriptor = Describe(point.SignalId);
        var hasValue = ValueFormatter.HasValue(point);
        return new SceneReading(
            point.SubjectEntityId,
            point.SignalId,
            descriptor.Label,
            Format(point),
            hasValue && point.Value?.Kind == "number" ? point.Value.Number : null,
            hasValue ? point.Unit : null,
            point.Quality,
            point.Freshness,
            point.ObservedAt,
            point.AgeSeconds,
            point.OriginPeerId,
            point.Source);
    }

    /// <summary>Exact display text for a point, with units. Unusable points read "Unavailable", never a number.</summary>
    public static string Format(HistoryPointDto point)
    {
        if (!ValueFormatter.HasValue(point))
        {
            return ValueFormatter.Format(point);
        }

        var value = point.Value!;
        return FormatValue(point.SignalId, point.Unit, value.Number, value.Boolean, value.Text);
    }

    public static string FormatValue(string signalId, string? unit, double? number, bool? boolean, string? text)
    {
        var descriptor = Describe(signalId);
        if (boolean is { } flag)
        {
            return descriptor.Flag is { } words ? (flag ? words.True : words.False) : (flag ? "Yes" : "No");
        }

        if (text is not null && descriptor.Words is { } map && map.TryGetValue(text, out var word))
        {
            return word;
        }

        if (text is not null)
        {
            return text;
        }

        if (number is not { } n)
        {
            return ValueFormatter.Unavailable;
        }

        if (descriptor.Suffix is { } suffix)
        {
            return Plain(n) + " " + suffix;
        }

        return unit switch
        {
            "Ratio" => Percent(n),
            "Bytes" => Bytes(n),
            "BytesPerSecond" => Bytes(n) + "/s",
            "Seconds" => Duration(n),
            "Celsius" => Plain(n) + " °C",
            "MegabitsPerSecond" => Mbps(n),
            "Count" => Plain(n),
            null or "" => Plain(n),
            _ => Plain(n) + " " + unit,
        };
    }

    /// <summary>A 0..1 ratio as a percentage; one decimal below 10% so small values are not rounded to zero.</summary>
    public static string Percent(double ratio) =>
        (ratio * 100).ToString(Math.Abs(ratio) < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Decimal (SI) units: 1 GB = 10^9 bytes. One decimal from KB upward.</summary>
    public static string Bytes(double bytes)
    {
        var abs = Math.Abs(bytes);
        return abs switch
        {
            < 1e3 => bytes.ToString("0", CultureInfo.InvariantCulture) + " B",
            < 1e6 => (bytes / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
            < 1e9 => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            < 1e12 => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            _ => (bytes / 1e12).ToString("0.0", CultureInfo.InvariantCulture) + " TB",
        };
    }

    public static string Duration(double seconds) => seconds switch
    {
        < 60 => seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s",
        < 3600 => (seconds / 60).ToString("0.#", CultureInfo.InvariantCulture) + " min",
        < 86400 => (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h",
        _ => (seconds / 86400).ToString("0.#", CultureInfo.InvariantCulture) + " d",
    };

    /// <summary>Link speeds: "1 Gbit/s" from 1000 upward, else "54 Mbit/s" (one decimal below 100).</summary>
    public static string Mbps(double mbps) => Math.Abs(mbps) switch
    {
        >= 1000 => (mbps / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " Gbit/s",
        < 100 => mbps.ToString("0.#", CultureInfo.InvariantCulture) + " Mbit/s",
        _ => mbps.ToString("0", CultureInfo.InvariantCulture) + " Mbit/s",
    };

    private static string Plain(double n) => n.ToString("G10", CultureInfo.InvariantCulture);
}
