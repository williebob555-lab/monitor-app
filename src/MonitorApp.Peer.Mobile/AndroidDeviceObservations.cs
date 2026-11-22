using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Android.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Mobile;

/// <summary>One platform read of battery and network state, already normalized by Android.Core.</summary>
public sealed record MobileDeviceSample(
    DateTimeOffset SourceTime,
    DeviceReading<int> BatteryPercent,
    DeviceReading<bool> Charging,
    NetworkTransportSummary Network);

/// <summary>Implemented by the Android app with BatteryManager/ConnectivityManager.</summary>
public interface IMobileDeviceReader
{
    ValueTask<MobileDeviceSample> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Adapts Android readings to the same <see cref="SystemSnapshot"/> that desktop collectors produce, so they pass
/// through the unchanged peer ingestion path (signed origin records). Unavailable readings stay explicit.
/// </summary>
public sealed class AndroidDeviceSnapshotCollector(IMobileDeviceReader reader) : ISystemSnapshotCollector
{
    public const string ModuleId = "monitor.android.device";
    public const string BatteryLevel = "android.battery.level";
    public const string BatteryCharging = "android.battery.charging";
    public const string NetworkConnected = "android.network.connected";
    public const string NetworkVpn = "android.network.vpn";

    public async ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var sample = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (sample.SourceTime.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Android reading source time must be UTC.");
        }

        var metrics = new List<CollectedMetric>();
        var health = new List<CollectorHealth>();

        if (sample.BatteryPercent is { IsAvailable: true, Value: { } percent })
        {
            metrics.Add(new CollectedMetric(BatteryLevel, MetricUnit.Ratio, ObservationQuality.Valid,
                new MetricValue { DoubleValue = percent / 100D }));
            health.Add(new CollectorHealth("battery", CapabilityState.Available, string.Empty));
        }
        else
        {
            metrics.Add(new CollectedMetric(BatteryLevel, MetricUnit.Ratio, ObservationQuality.Unavailable, null));
            health.Add(new CollectorHealth("battery", CapabilityState.Unsupported, "reading_unavailable"));
        }

        metrics.Add(sample.Charging is { IsAvailable: true, Value: { } charging }
            ? new CollectedMetric(BatteryCharging, MetricUnit.Boolean, ObservationQuality.Valid,
                new MetricValue { BooleanValue = charging })
            : new CollectedMetric(BatteryCharging, MetricUnit.Boolean, ObservationQuality.Unavailable, null));

        metrics.Add(new CollectedMetric(NetworkConnected, MetricUnit.Boolean, ObservationQuality.Valid,
            new MetricValue { BooleanValue = sample.Network.IsConnected }));
        metrics.Add(new CollectedMetric(NetworkVpn, MetricUnit.Boolean, ObservationQuality.Valid,
            new MetricValue { BooleanValue = sample.Network.IsVpn }));
        health.Add(new CollectorHealth("network", CapabilityState.Available, string.Empty));

        return new SystemSnapshot(Timestamp.FromDateTimeOffset(sample.SourceTime), metrics, health);
    }
}

public static class AndroidCapabilityDeclaration
{
    public static CapabilityModuleDeclaration Create()
    {
        var declaration = new CapabilityModuleDeclaration
        {
            ModuleId = AndroidDeviceSnapshotCollector.ModuleId,
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
                new EntityTypeDeclaration { EntityTypeId = "monitor.entity.peer", DisplayName = "Monitor App peer" },
            },
        };
        declaration.Observations.Add([
            Signal(AndroidDeviceSnapshotCollector.BatteryLevel, "monitor.subsystem.battery", "unit.ratio", ObservationValueKind.Numeric, ObservationTemporalBehavior.Gauge, NetworkDataClass.NotNetworkData),
            Signal(AndroidDeviceSnapshotCollector.BatteryCharging, "monitor.subsystem.battery", string.Empty, ObservationValueKind.State, ObservationTemporalBehavior.DiscreteState, NetworkDataClass.NotNetworkData),
            Signal(AndroidDeviceSnapshotCollector.NetworkConnected, "monitor.subsystem.network", string.Empty, ObservationValueKind.State, ObservationTemporalBehavior.DiscreteState, NetworkDataClass.AggregateOperational),
            Signal(AndroidDeviceSnapshotCollector.NetworkVpn, "monitor.subsystem.network", string.Empty, ObservationValueKind.State, ObservationTemporalBehavior.DiscreteState, NetworkDataClass.AggregateOperational),
            Signal("collector.health.battery", "monitor.subsystem.collection", string.Empty, ObservationValueKind.State, ObservationTemporalBehavior.DiscreteState, NetworkDataClass.NotNetworkData),
            Signal("collector.health.network", "monitor.subsystem.collection", string.Empty, ObservationValueKind.State, ObservationTemporalBehavior.DiscreteState, NetworkDataClass.NotNetworkData),
        ]);
        var fallback = new InspectorDescriptor
        {
            InspectorId = "monitor.inspector.android_device_fallback",
            Title = "Android device observations",
            Kind = InspectorKind.GeneratedFallback,
        };
        fallback.SignalIds.Add(declaration.Observations.Select(item => item.SignalId));
        declaration.Inspectors.Add(fallback);
        return declaration;
    }

    private static ObservationDeclaration Signal(
        string signalId,
        string subsystem,
        string unit,
        ObservationValueKind kind,
        ObservationTemporalBehavior behavior,
        NetworkDataClass networkClass) => new()
        {
            SignalId = signalId,
            EntityTypeId = "monitor.entity.peer",
            Domain = "monitor.domain.android",
            Subsystem = subsystem,
            Source = "monitor.source.android_platform",
            ValueKind = kind,
            Unit = unit,
            TemporalBehavior = behavior,
            Importance = ObservationImportance.Normal,
            Privacy = ObservationPrivacyClass.Operational,
            CollectionCost = ObservationCollectionCost.Low,
            NetworkDataClass = networkClass,
            DefaultSamplingInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(30)),
            StaleAfter = Duration.FromTimeSpan(TimeSpan.FromMinutes(3)),
            DefaultRetention = Duration.FromTimeSpan(TimeSpan.FromDays(7)),
        };
}
