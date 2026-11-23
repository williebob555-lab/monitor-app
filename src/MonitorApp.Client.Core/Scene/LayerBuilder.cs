using System.Globalization;

namespace MonitorApp.Client.Core.Scene;

/// <summary>
/// Groups a node's readings into layers. Only kinds that actually have readings are produced. A layer's fill is
/// set only when a real proportion can be derived from readings that carry usable numbers and the expected unit
/// (memory used/total, storage (total-free)/total, CPU utilization ratio, battery level ratio); otherwise it is null.
/// </summary>
public static class LayerBuilder
{
    private static readonly SceneLayerKind[] Order =
    [
        SceneLayerKind.Compute, SceneLayerKind.Memory, SceneLayerKind.Storage,
        SceneLayerKind.Network, SceneLayerKind.Power, SceneLayerKind.Connectivity, SceneLayerKind.Other, SceneLayerKind.Collection,
    ];

    public static IReadOnlyList<SceneLayer> Build(IEnumerable<SceneReading> readings, HealthClassifier classifier)
    {
        var grouped = readings
            .OrderBy(r => r.SignalId, StringComparer.Ordinal)
            .ThenBy(r => r.SubjectEntityId, StringComparer.Ordinal)
            .GroupBy(r => SignalCatalog.Describe(r.SignalId).Kind)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SceneReading>)g.ToList());
        var layers = new List<SceneLayer>();
        foreach (var kind in Order)
        {
            if (!grouped.TryGetValue(kind, out var list))
            {
                continue;
            }

            layers.Add(new SceneLayer(
                kind.ToString().ToLowerInvariant(),
                SignalCatalog.LayerTitle(kind),
                kind,
                HealthClassifier.Aggregate(list.Select(classifier.Reading)),
                Fill(kind, list),
                Summary(kind, list),
                list));
        }

        return layers;
    }

    public static double? Fill(SceneLayerKind kind, IReadOnlyList<SceneReading> readings)
    {
        switch (kind)
        {
            case SceneLayerKind.Compute:
                return Ratio(Find(readings, SignalIds.CpuUtilization));
            case SceneLayerKind.Power:
                return Ratio(Find(readings, SignalIds.BatteryLevel));
            case SceneLayerKind.Memory:
                {
                    var used = Bytes(Find(readings, SignalIds.MemoryUsed));
                    var total = Bytes(Find(readings, SignalIds.MemoryTotal));
                    return used is { } u && total is > 0 ? Clamp(u / total.Value) : null;
                }

            case SceneLayerKind.Storage:
                {
                    var free = Bytes(Find(readings, SignalIds.StorageFree));
                    var total = Bytes(Find(readings, SignalIds.StorageTotal));
                    return free is { } f && total is > 0 ? Clamp((total.Value - f) / total.Value) : null;
                }

            default:
                return null;
        }
    }

    public static string Summary(SceneLayerKind kind, IReadOnlyList<SceneReading> readings)
    {
        switch (kind)
        {
            case SceneLayerKind.Compute when Find(readings, SignalIds.CpuUtilization) is { } cpu:
                return $"CPU {cpu.DisplayValue}";
            case SceneLayerKind.Memory:
                {
                    var used = Find(readings, SignalIds.MemoryUsed);
                    var total = Find(readings, SignalIds.MemoryTotal);
                    if (used?.Number is { } u && total?.Number is > 0 and { } t && Fill(kind, readings) is { } fill)
                    {
                        return $"{SignalCatalog.Bytes(u)} of {SignalCatalog.Bytes(t)} used ({SignalCatalog.Percent(fill)})";
                    }

                    break;
                }

            case SceneLayerKind.Storage:
                {
                    var free = Find(readings, SignalIds.StorageFree);
                    var total = Find(readings, SignalIds.StorageTotal);
                    if (free?.Number is { } f && total?.Number is > 0 and { } t)
                    {
                        return $"{SignalCatalog.Bytes(f)} free of {SignalCatalog.Bytes(t)}";
                    }

                    break;
                }
        }

        var available = readings.Where(SignalCatalog.IsAvailable).ToList();
        if (available.Count == 0)
        {
            return "Nothing reported";
        }

        switch (kind)
        {
            case SceneLayerKind.Connectivity:
                return ConnectivitySummary(available);
            case SceneLayerKind.Collection:
                {
                    var failing = available.Where(r => r.DisplayValue != "Working").ToList();
                    return failing.Count == 0
                        ? (available.Count == 1 ? "Collector working" : $"All {available.Count.ToString(CultureInfo.InvariantCulture)} collectors working")
                        : string.Join(" · ", failing.Select(r => $"{r.Label}: {r.DisplayValue}"));
                }
        }

        return string.Join(" · ", available.Select(Describe).Take(3))
            + (available.Count > 3 ? $" · {(available.Count - 3).ToString(CultureInfo.InvariantCulture)} more" : string.Empty);
    }

    /// <summary>"Connected · VPN on · Links up: Mobile data, VPN": the state words first, then which links are up.</summary>
    private static string ConnectivitySummary(List<SceneReading> available)
    {
        var parts = new List<string>();
        if (Find(available, SignalIds.NetworkConnected) is { } connected)
        {
            parts.Add(connected.DisplayValue);
        }

        if (Find(available, SignalIds.NetworkVpn) is { } vpn)
        {
            parts.Add(vpn.DisplayValue);
        }

        var links = available.Where(r => r.SignalId.StartsWith("lan.link.", StringComparison.Ordinal) && r.SignalId.EndsWith(".up", StringComparison.Ordinal)).ToList();
        var up = links.Where(r => r.DisplayValue == "Up").Select(r => SignalCatalog.Name(r.SignalId["lan.link.".Length..^".up".Length])).ToList();
        if (links.Count > 0)
        {
            parts.Add(up.Count == 0 ? "No links up" : (up.Count == 1 ? "Link up: " : "Links up: ") + string.Join(", ", up));
        }

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : string.Join(" · ", available.Take(3).Select(Describe));
    }

    private static string Describe(SceneReading reading) =>
        // Self-describing words ("Charging", "VPN on") stand alone; everything else needs its label.
        reading.SignalId is SignalIds.BatteryCharging or SignalIds.NetworkConnected or SignalIds.NetworkVpn
            ? reading.DisplayValue
            : $"{reading.Label} {reading.DisplayValue}";

    private static SceneReading? Find(IReadOnlyList<SceneReading> readings, string signalId) =>
        readings.FirstOrDefault(r => r.SignalId == signalId);

    private static double? Ratio(SceneReading? reading) =>
        reading is { Number: { } n, Unit: "Ratio" } ? Clamp(n) : null;

    private static double? Bytes(SceneReading? reading) =>
        reading is { Number: { } n, Unit: "Bytes" } ? n : null;

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
