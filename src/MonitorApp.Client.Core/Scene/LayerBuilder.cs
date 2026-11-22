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
        SceneLayerKind.Network, SceneLayerKind.Power, SceneLayerKind.Connectivity, SceneLayerKind.Other,
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

        return string.Join(" · ", readings.Select(Describe).Take(3))
            + (readings.Count > 3 ? $" · {readings.Count - 3} more" : string.Empty);
    }

    private static string Describe(SceneReading reading)
    {
        var descriptor = SignalCatalog.Describe(reading.SignalId);
        var unavailable = reading.DisplayValue.StartsWith("Unavailable", StringComparison.Ordinal);
        if (unavailable)
        {
            return $"{reading.Label} reading unavailable";
        }

        // Counters and generic signals need their label to be meaningful; self-describing words stand alone.
        return reading.SignalId is SignalIds.BatteryCharging or SignalIds.NetworkConnected or SignalIds.NetworkVpn
            ? reading.DisplayValue
            : $"{(descriptor.IsKnown ? reading.Label : reading.SignalId)} {reading.DisplayValue}";
    }

    private static SceneReading? Find(IReadOnlyList<SceneReading> readings, string signalId) =>
        readings.FirstOrDefault(r => r.SignalId == signalId);

    private static double? Ratio(SceneReading? reading) =>
        reading is { Number: { } n, Unit: "Ratio" } ? Clamp(n) : null;

    private static double? Bytes(SceneReading? reading) =>
        reading is { Number: { } n, Unit: "Bytes" } ? n : null;

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
