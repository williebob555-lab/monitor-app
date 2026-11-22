using MonitorApp.Agent.Core;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using MonitorApp.Android.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection.Lan;
using Google.Protobuf.WellKnownTypes;

namespace MonitorApp.Peer.Mobile;

/// <summary>
/// Maps Android's coarse <see cref="NetworkTransportSummary"/> into the desktop's self-interface model so the shared
/// <see cref="SelfLanCollector"/> (entities, claims, relationships, ValidTo) and <see cref="LanLinkObservations"/> are
/// reused unchanged. Android does not expose interface names, MACs, subnets or gateways to apps, so none are invented:
/// each transport becomes one synthetic interface (<c>wifi</c>, <c>cellular</c>, <c>ethernet</c>, <c>vpn</c>, <c>other</c>)
/// with no addresses, so no segment, gateway or VLAN edges exist. An interface is "up" only when the network is validated.
/// A transport that is no longer present is absent from the snapshot, which ends its relationships with a ValidTo.
/// </summary>
public static class PhoneTransportMapper
{
    public const string WifiInterface = "wifi";
    public const string CellularInterface = "cellular";
    public const string EthernetInterface = "ethernet";
    public const string VpnInterface = "vpn";
    public const string OtherInterface = "other";

    public static SelfInterfaceSnapshot ToSnapshot(NetworkTransportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var interfaces = new List<SelfInterface>();
        foreach (var transport in summary.Transports)
        {
            var (name, medium) = transport switch
            {
                "Wi-Fi" => (WifiInterface, InterfaceMedium.WiFi),
                "Cellular" => (CellularInterface, InterfaceMedium.Cellular),
                "Ethernet" => (EthernetInterface, InterfaceMedium.Ethernet),
                "VPN" => (VpnInterface, InterfaceMedium.Overlay),
                "Other" => (OtherInterface, InterfaceMedium.Other),
                _ => ((string?)null, InterfaceMedium.Other), // "Unavailable": nothing is known to exist.
            };
            if (name is null)
            {
                continue;
            }

            // Link speed, duplex, MTU and MAC are not exposed to apps: null, so they stay Unavailable.
            interfaces.Add(new SelfInterface(name, medium, summary.IsConnected, null, null, null, null, [], null, null));
        }

        return new SelfInterfaceSnapshot(SelfCollectionState.Collected, null, interfaces, [], summary.SourceTime.ToUniversalTime());
    }

    /// <summary>The single non-VPN transport the network-wide metered flag and bandwidth estimates can honestly be attributed to.</summary>
    public static string? UnderlyingInterface(NetworkTransportSummary summary)
    {
        var physical = ToSnapshot(summary).Interfaces.Where(i => i.Medium != InterfaceMedium.Overlay).ToList();
        return physical.Count == 1 ? physical[0].Name : null;
    }
}

/// <summary>Holds the latest mapped phone snapshot so the shared synchronous collector contract can read it.</summary>
public sealed class PhoneTransportInterfaceSource : ISelfInterfaceCollector
{
    private SelfInterfaceSnapshot current = SelfInterfaceSnapshot.Unsupported("transport_not_read", DateTimeOffset.UnixEpoch);

    public NetworkTransportSummary? Latest { get; private set; }

    public void Update(NetworkTransportSummary summary)
    {
        Latest = summary;
        current = PhoneTransportMapper.ToSnapshot(summary);
    }

    public SelfInterfaceSnapshot Collect(DateTimeOffset now) => current;
}

/// <summary>
/// The shared <see cref="LanLinkObservations"/> (up and mtu; speed and duplex Unavailable for Wi-Fi and Ethernet) plus the
/// phone-only network-wide signals <c>lan.link.{if}.metered</c>, <c>.bandwidth_down_mbps</c> and <c>.bandwidth_up_mbps</c>
/// (platform estimates, not measured link speed). The extras are emitted for the one underlying transport only;
/// with several non-VPN transports the attribution is ambiguous and nothing is emitted for them.
/// </summary>
public sealed class PhoneTransportSnapshotCollector(PhoneTransportInterfaceSource source) : ISystemSnapshotCollector
{
    public const string MeteredMetric = "metered";
    public const string DownstreamMetric = "bandwidth_down_mbps";
    public const string UpstreamMetric = "bandwidth_up_mbps";

    public ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = LanLinkObservations.ToSnapshot(source.Collect(DateTimeOffset.UtcNow));
        if (source.Latest is not { } summary || PhoneTransportMapper.UnderlyingInterface(summary) is not { } name)
        {
            return ValueTask.FromResult(snapshot);
        }

        var metrics = snapshot.Metrics.ToList();
        var meteredId = LanLinkObservations.Signal(name, MeteredMetric);
        metrics.Add(summary.IsMetered is { } metered
            ? new CollectedMetric(meteredId, MetricUnit.Boolean, ObservationQuality.Valid, new MetricValue { BooleanValue = metered })
            : new CollectedMetric(meteredId, MetricUnit.Boolean, ObservationQuality.Unavailable, null));
        metrics.Add(Estimate(name, DownstreamMetric, summary.DownstreamKbps));
        metrics.Add(Estimate(name, UpstreamMetric, summary.UpstreamKbps));
        return ValueTask.FromResult(new SystemSnapshot(snapshot.ObservedAt, metrics, snapshot.Health));
    }

    private static CollectedMetric Estimate(string name, string metric, long? kbps)
    {
        var id = LanLinkObservations.Signal(name, metric);
        return kbps is { } value
            ? new CollectedMetric(id, MetricUnit.MegabitsPerSecond, ObservationQuality.Valid, new MetricValue { DoubleValue = value / 1000D })
            : new CollectedMetric(id, MetricUnit.MegabitsPerSecond, ObservationQuality.Unavailable, null);
    }
}

/// <summary>Remembers the sample the ordinary collector just read, so the LAN step maps that same reading (one platform read per tick).</summary>
internal sealed class RecordingDeviceReader(IMobileDeviceReader inner) : IMobileDeviceReader
{
    public MobileDeviceSample? Last { get; private set; }

    public async ValueTask<MobileDeviceSample> ReadAsync(CancellationToken cancellationToken)
    {
        var sample = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        Last = sample;
        return sample;
    }
}

/// <summary>
/// The phone's own-LAN step, run on the runtime's collection schedule right after the ordinary reading, in the same order
/// as the desktop node: topology (entities, claims, relationships with ValidTo) first, then link observations.
/// </summary>
internal sealed class PhoneLanCollection
{
    private readonly RecordingDeviceReader reader;
    private readonly PhoneTransportInterfaceSource source;
    private readonly SelfLanCollector topology;
    private readonly PeerSystemMetricsIngestionBridge links;

    private PhoneLanCollection(RecordingDeviceReader reader, PhoneTransportInterfaceSource source, SelfLanCollector topology, PeerSystemMetricsIngestionBridge links)
    {
        this.reader = reader;
        this.source = source;
        this.topology = topology;
        this.links = links;
    }

    public string? LastError { get; private set; }

    public static PhoneLanCollection Create(PeerStore store, ILocalPeerIdentityProvider identity, RecordingDeviceReader reader)
    {
        var peerId = identity.PublicIdentity.PeerId;
        var source = new PhoneTransportInterfaceSource();
        var topology = new SelfLanCollector(peerId, source, store);
        var links = new PeerSystemMetricsIngestionBridge(
            store,
            identity,
            new PhoneTransportSnapshotCollector(source),
            PeerCollectionOptions.Default($"peer:{peerId}", Guid.NewGuid().ToString("N")) with
            {
                CollectorId = LanLinkObservations.CollectorId,
            });
        return new PhoneLanCollection(reader, source, topology, links);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (reader.Last is not { } sample)
        {
            return;
        }

        try
        {
            source.Update(sample.Network);
            await topology.CollectAndPersistAsync(cancellationToken).ConfigureAwait(false);
            await links.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.GetType().Name;
        }
    }
}
