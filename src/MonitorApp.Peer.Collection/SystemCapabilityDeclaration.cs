using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection;

public static class SystemCapabilityDeclaration
{
    public static CapabilityModuleDeclaration Create()
    {
        var declaration = new CapabilityModuleDeclaration
        {
            ModuleId = "monitor.system",
            ModuleVersion = new SemanticVersion { Major = 1 },
            CompatiblePeerProtocol = new ProtocolCompatibility
            {
                MinimumMajor = PeerContractRules.ProtocolMajor,
                MinimumMinor = PeerContractRules.BilateralSessionProofMinor,
                MaximumMajor = PeerContractRules.ProtocolMajor,
                MaximumMinor = PeerContractRules.BilateralSessionProofMinor,
            },
            ModuleKind = CapabilityModuleKind.OrdinaryMonitoring,
            EntityTypes =
            {
                new EntityTypeDeclaration
                {
                    EntityTypeId = "monitor.entity.peer",
                    DisplayName = "Monitor App peer",
                },
            },
        };
        declaration.Observations.Add([
            Metric("system.cpu.utilization", "monitor.subsystem.cpu", "unit.ratio", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge),
            Metric("system.memory.total", "monitor.subsystem.memory", "unit.bytes", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge),
            Metric("system.memory.used", "monitor.subsystem.memory", "unit.bytes", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge),
            Metric("system.storage.total", "monitor.subsystem.storage", "unit.bytes", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge),
            Metric("system.storage.free", "monitor.subsystem.storage", "unit.bytes", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge),
            Metric("system.network.bytes_received", "monitor.subsystem.network", "unit.bytes", ObservationValueKind.Counter, ObservationTemporalBehavior.MonotonicCounter, NetworkDataClass.AggregateOperational),
            Metric("system.network.bytes_sent", "monitor.subsystem.network", "unit.bytes", ObservationValueKind.Counter, ObservationTemporalBehavior.MonotonicCounter, NetworkDataClass.AggregateOperational),
        ]);
        declaration.Observations.Add([
            Health("cpu"),
            Health("memory"),
            Health("storage"),
            Health("network"),
        ]);
        var fallback = new InspectorDescriptor
        {
            InspectorId = "monitor.inspector.system_fallback",
            Title = "System observations",
            Kind = InspectorKind.GeneratedFallback,
        };
        fallback.SignalIds.Add(declaration.Observations.Select(item => item.SignalId));
        declaration.Inspectors.Add(fallback);
        return declaration;
    }

    private static ObservationDeclaration Metric(
        string signalId,
        string subsystem,
        string unit,
        ObservationValueKind valueKind,
        ObservationTemporalBehavior temporalBehavior,
        NetworkDataClass networkDataClass = NetworkDataClass.NotNetworkData) => new()
        {
            SignalId = signalId,
            EntityTypeId = "monitor.entity.peer",
            Domain = "monitor.domain.system",
            Subsystem = subsystem,
            Source = "monitor.source.operating_system",
            ValueKind = valueKind,
            Unit = unit,
            TemporalBehavior = temporalBehavior,
            Importance = ObservationImportance.Normal,
            Privacy = ObservationPrivacyClass.Operational,
            CollectionCost = ObservationCollectionCost.Low,
            NetworkDataClass = networkDataClass,
            DefaultSamplingInterval = Duration.FromTimeSpan(TimeSpan.FromMinutes(1)),
            StaleAfter = Duration.FromTimeSpan(TimeSpan.FromMinutes(3)),
            DefaultRetention = Duration.FromTimeSpan(TimeSpan.FromDays(30)),
        };

    private static ObservationDeclaration Health(string capabilityId) => new()
    {
        SignalId = $"collector.health.{capabilityId}",
        EntityTypeId = "monitor.entity.peer",
        Domain = "monitor.domain.system",
        Subsystem = "monitor.subsystem.collection",
        Source = "monitor.source.system_collector",
        ValueKind = ObservationValueKind.State,
        TemporalBehavior = ObservationTemporalBehavior.DiscreteState,
        Importance = ObservationImportance.High,
        Privacy = ObservationPrivacyClass.Operational,
        CollectionCost = ObservationCollectionCost.Low,
        NetworkDataClass = NetworkDataClass.NotNetworkData,
        DefaultSamplingInterval = Duration.FromTimeSpan(TimeSpan.FromMinutes(1)),
        StaleAfter = Duration.FromTimeSpan(TimeSpan.FromMinutes(3)),
        DefaultRetention = Duration.FromTimeSpan(TimeSpan.FromDays(30)),
    };
}
