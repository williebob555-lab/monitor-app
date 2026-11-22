using System.Globalization;
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
/// <param name="IsKnown">False for ids the catalog has no entry for; they are shown under <see cref="SceneLayerKind.Other"/>.</param>
public sealed record SignalDescriptor(string SignalId, SceneLayerKind Kind, string Label, bool IsKnown);

/// <summary>
/// Maps signal ids to the layer they belong to, a human label, and exact value formatting. The catalog only
/// describes how to show what a peer reported; it never supplies a value. Unknown signals keep their raw id as
/// the label and are formatted generically from the unit the peer sent.
/// </summary>
public static class SignalCatalog
{
    private static readonly Dictionary<string, SignalDescriptor> Known = new(StringComparer.Ordinal)
    {
        [SignalIds.CpuUtilization] = new(SignalIds.CpuUtilization, SceneLayerKind.Compute, "CPU utilization", true),
        [SignalIds.MemoryUsed] = new(SignalIds.MemoryUsed, SceneLayerKind.Memory, "Memory used", true),
        [SignalIds.MemoryTotal] = new(SignalIds.MemoryTotal, SceneLayerKind.Memory, "Memory total", true),
        [SignalIds.StorageFree] = new(SignalIds.StorageFree, SceneLayerKind.Storage, "Storage free", true),
        [SignalIds.StorageTotal] = new(SignalIds.StorageTotal, SceneLayerKind.Storage, "Storage total", true),
        [SignalIds.NetworkBytesSent] = new(SignalIds.NetworkBytesSent, SceneLayerKind.Network, "Network sent (counter)", true),
        [SignalIds.NetworkBytesReceived] = new(SignalIds.NetworkBytesReceived, SceneLayerKind.Network, "Network received (counter)", true),
        [SignalIds.BatteryLevel] = new(SignalIds.BatteryLevel, SceneLayerKind.Power, "Battery level", true),
        [SignalIds.BatteryCharging] = new(SignalIds.BatteryCharging, SceneLayerKind.Power, "Battery charging", true),
        [SignalIds.NetworkConnected] = new(SignalIds.NetworkConnected, SceneLayerKind.Connectivity, "Network connection", true),
        [SignalIds.NetworkVpn] = new(SignalIds.NetworkVpn, SceneLayerKind.Connectivity, "VPN", true),
    };

    public static SignalDescriptor Describe(string signalId) =>
        Known.TryGetValue(signalId, out var descriptor)
            ? descriptor
            : new SignalDescriptor(signalId, SceneLayerKind.Other, signalId, false);

    /// <summary>Title shown for a layer of this kind.</summary>
    public static string LayerTitle(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Compute => "Processor",
        SceneLayerKind.Memory => "Memory",
        SceneLayerKind.Storage => "Storage",
        SceneLayerKind.Network => "Network traffic",
        SceneLayerKind.Power => "Battery and power",
        SceneLayerKind.Connectivity => "Connectivity",
        _ => "Other signals",
    };

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
        switch (signalId)
        {
            case SignalIds.BatteryCharging when boolean is { } charging:
                return charging ? "Charging" : "On battery";
            case SignalIds.NetworkConnected when boolean is { } connected:
                return connected ? "Connected" : "Not connected";
            case SignalIds.NetworkVpn when boolean is { } vpn:
                return vpn ? "VPN on" : "VPN off";
        }

        if (boolean is { } flag)
        {
            return flag ? "Yes" : "No";
        }

        if (text is not null)
        {
            return text;
        }

        if (number is not { } n)
        {
            return ValueFormatter.Unavailable;
        }

        return unit switch
        {
            "Ratio" => Percent(n),
            "Bytes" => Bytes(n),
            "BytesPerSecond" => Bytes(n) + "/s",
            "Seconds" => Duration(n),
            "Celsius" => Plain(n) + " °C",
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

    private static string Plain(double n) => n.ToString("G10", CultureInfo.InvariantCulture);
}
