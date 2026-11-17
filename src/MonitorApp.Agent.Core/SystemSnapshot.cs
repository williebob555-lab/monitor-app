using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public sealed record CollectedMetric(
    string MetricId,
    MetricUnit Unit,
    ObservationQuality Quality,
    MetricValue? Value);

public sealed record CollectorHealth(
    string CapabilityId,
    CapabilityState State,
    string DetailCode);

public sealed record SystemSnapshot(
    Timestamp ObservedAt,
    IReadOnlyList<CollectedMetric> Metrics,
    IReadOnlyList<CollectorHealth> Health);

public interface ISystemSnapshotCollector
{
    ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default);
}
