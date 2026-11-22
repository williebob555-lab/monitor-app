using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Lan;

/// <summary>
/// The peer's own link state, speed, duplex and MTU as ordinary observations, so they ride the same signed
/// origin-stream ingestion and replication path as the system metrics. Signal ids are
/// <c>lan.link.{interface}.{up|speed_mbps|full_duplex|mtu}</c>; a value the host does not expose (Windows duplex, the
/// speed of a down link) is recorded as Unavailable, never guessed.
/// </summary>
public static class LanLinkObservations
{
    public const string CollectorId = "monitor.lan.self";
    public const string HealthCapabilityId = "lan";

    public static string Signal(string interfaceName, string metric) => $"lan.link.{interfaceName}.{metric}";

    public static SystemSnapshot ToSnapshot(SelfInterfaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var observedAt = Timestamp.FromDateTimeOffset(snapshot.CollectedAt.ToUniversalTime());
        var metrics = new List<CollectedMetric>();
        if (snapshot.State == SelfCollectionState.Collected)
        {
            foreach (var nic in snapshot.Interfaces.Where(i => i.Medium != InterfaceMedium.Loopback))
            {
                metrics.Add(Known(Signal(nic.Name, "up"), MetricUnit.Boolean, new MetricValue { BooleanValue = nic.IsUp }));
                metrics.Add(nic.Mtu is { } mtu
                    ? Known(Signal(nic.Name, "mtu"), MetricUnit.Bytes, new MetricValue { UnsignedIntegerValue = (ulong)mtu })
                    : Unknown(Signal(nic.Name, "mtu"), MetricUnit.Bytes));
                if (!nic.IsPhysical)
                {
                    continue; // speed and duplex are properties of hardware links; virtual/overlay interfaces have none.
                }

                metrics.Add(nic.IsUp && nic.SpeedMbps is { } speed
                    ? Known(Signal(nic.Name, "speed_mbps"), MetricUnit.MegabitsPerSecond, new MetricValue { UnsignedIntegerValue = (ulong)speed })
                    : Unknown(Signal(nic.Name, "speed_mbps"), MetricUnit.MegabitsPerSecond));
                metrics.Add(nic.IsUp && nic.Duplex is "full" or "half"
                    ? Known(Signal(nic.Name, "full_duplex"), MetricUnit.Boolean, new MetricValue { BooleanValue = nic.Duplex == "full" })
                    : Unknown(Signal(nic.Name, "full_duplex"), MetricUnit.Boolean));
            }
        }

        var (state, detail) = snapshot.State switch
        {
            SelfCollectionState.Collected => (CapabilityState.Available, "lan.collected"),
            SelfCollectionState.Unsupported => (CapabilityState.Unsupported, "lan.unsupported"),
            _ => (CapabilityState.Error, "lan.failed"),
        };
        return new SystemSnapshot(observedAt, metrics, [new CollectorHealth(HealthCapabilityId, state, detail)]);
    }

    private static CollectedMetric Known(string id, MetricUnit unit, MetricValue value) =>
        new(id, unit, ObservationQuality.Valid, value);

    private static CollectedMetric Unknown(string id, MetricUnit unit) =>
        new(id, unit, ObservationQuality.Unavailable, null);
}

/// <summary>Adapts the host's own interface collector to the shared ingestion bridge.</summary>
public sealed class LanLinkSnapshotCollector(ISelfInterfaceCollector source, TimeProvider? clock = null) : ISystemSnapshotCollector
{
    public ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(LanLinkObservations.ToSnapshot(source.Collect((clock ?? TimeProvider.System).GetUtcNow())));
}
