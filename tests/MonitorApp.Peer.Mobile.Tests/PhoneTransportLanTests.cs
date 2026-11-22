using MonitorApp.Android.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

public sealed class PhoneTransportLanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TransportSequenceBuildsPhoneLanEntitiesEndsVanishedLinksAndEmitsHonestObservations()
    {
        var root = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            var reader = new TransportReader();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                MobilePeerOptions.Default(root, "phone"), identity, reader);
            var peerId = phone.Identity.PeerId;
            var wifiRel = $"{LanEntityIds.Interface(peerId, "wifi")}|AttachedTo|{LanEntityIds.Peer(peerId)}";
            var vpnRel = $"{LanEntityIds.Interface(peerId, "vpn")}|AttachedTo|{LanEntityIds.Peer(peerId)}";
            var cellRel = $"{LanEntityIds.Interface(peerId, "cellular")}|AttachedTo|{LanEntityIds.Peer(peerId)}";

            // Wi-Fi, validated, unmetered, with a bandwidth estimate.
            reader.Network = NetworkTransportSummary.Create(true, false, true, false, false, T0, isMetered: false, downstreamKbps: 120_000, upstreamKbps: 40_000);
            await phone.CollectOnceAsync();
            Assert.Null(phone.LastLanError);
            var rels = await RelationshipsAsync(phone, peerId);
            Assert.Equal([wifiRel], rels.Keys.ToArray());
            Assert.Null(rels[wifiRel].ValidTo);
            Assert.Equal(T0, rels[wifiRel].ValidFrom);

            // Wi-Fi plus VPN: the VPN is a separate overlay interface; Wi-Fi keeps its original ValidFrom.
            reader.Network = NetworkTransportSummary.Create(true, true, true, false, false, T0.AddMinutes(1), isMetered: false, downstreamKbps: 120_000);
            await phone.CollectOnceAsync();
            rels = await RelationshipsAsync(phone, peerId);
            Assert.Null(rels[wifiRel].ValidTo);
            Assert.Equal(T0, rels[wifiRel].ValidFrom);
            Assert.Null(rels[vpnRel].ValidTo);

            // Cellular only, metered: Wi-Fi and VPN links vanish and are ended at the reading time.
            reader.Network = NetworkTransportSummary.Create(true, false, false, true, false, T0.AddMinutes(2), isMetered: true);
            await phone.CollectOnceAsync();
            rels = await RelationshipsAsync(phone, peerId);
            Assert.Equal(T0.AddMinutes(2), rels[wifiRel].ValidTo);
            Assert.Equal(T0.AddMinutes(2), rels[vpnRel].ValidTo);
            Assert.Null(rels[cellRel].ValidTo);

            // No validated network: nothing is reported, so the cellular link ends too (an unavailable state, not a guessed one).
            reader.Network = NetworkTransportSummary.Create(false, false, false, false, false, T0.AddMinutes(3));
            await phone.CollectOnceAsync();
            rels = await RelationshipsAsync(phone, peerId);
            Assert.All(rels.Values, r => Assert.NotNull(r.ValidTo));
            Assert.Equal(T0.AddMinutes(3), rels[cellRel].ValidTo);
            Assert.Equal(T0.AddMinutes(2), rels[wifiRel].ValidTo);

            // Entities exist with LAN kinds and no invented identity or address claims.
            foreach (var name in new[] { "wifi", "cellular", "vpn" })
            {
                var claims = await phone.Store.ReadIdentityClaimsAsync(LanEntityIds.Interface(peerId, name));
                Assert.DoesNotContain(claims, c => c.ClaimType == LanClaimTypes.InterfaceMac);
                Assert.Equal(name == "vpn" ? "overlay" : name, Assert.Single(claims, c => c.ClaimType == LanClaimTypes.InterfaceMedium).NormalizedValue);
            }

            Assert.All(rels.Values, r => Assert.Equal(RelationshipKind.AttachedTo, r.Kind)); // no segment, gateway or VLAN edges: no addresses are known.

            // Observations: up follows validation, bandwidth/metered are Valid only when reported, speed and duplex are Unavailable.
            var local = (await phone.Store.ReadLocalIdentityAsync())!;
            var records = await phone.Store.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, 1, 1000);
            var metrics = records.Select(r => TelemetryEnvelope.Parser.ParseFrom(r.RecordBytes))
                .Where(e => e.PayloadCase == TelemetryEnvelope.PayloadOneofCase.Metric)
                .Select(e => e.Metric)
                .ToList();
            List<MetricSample> Of(string id) => metrics.Where(m => m.MetricId == id).ToList();

            var wifiUp = Of(LanLinkObservations.Signal("wifi", "up"));
            Assert.Equal(2, wifiUp.Count); // steps 1 and 2 only; absent afterwards.
            Assert.All(wifiUp, m => Assert.True(m.Value.BooleanValue));
            Assert.Single(Of(LanLinkObservations.Signal("cellular", "up")), m => m.Value.BooleanValue);
            Assert.Empty(Of(LanLinkObservations.Signal("cellular", "speed_mbps")));
            Assert.All(Of(LanLinkObservations.Signal("wifi", "speed_mbps")), m => Assert.Equal(ObservationQuality.Unavailable, m.Quality));
            Assert.All(Of(LanLinkObservations.Signal("wifi", "full_duplex")), m => Assert.Equal(ObservationQuality.Unavailable, m.Quality));
            Assert.All(Of(LanLinkObservations.Signal("wifi", "mtu")), m => Assert.Equal(ObservationQuality.Unavailable, m.Quality));

            var down = Of(LanLinkObservations.Signal("wifi", PhoneTransportSnapshotCollector.DownstreamMetric));
            Assert.Equal(2, down.Count); // Wi-Fi is the only non-VPN transport in steps 1 and 2.
            Assert.All(down, m => Assert.Equal(120D, m.Value.DoubleValue));
            var up = Of(LanLinkObservations.Signal("wifi", PhoneTransportSnapshotCollector.UpstreamMetric));
            Assert.Equal(ObservationQuality.Valid, up[0].Quality);
            Assert.Equal(ObservationQuality.Unavailable, up[1].Quality); // not reported in step 2: never carried over.
            Assert.All(Of(LanLinkObservations.Signal("wifi", PhoneTransportSnapshotCollector.MeteredMetric)), m => Assert.False(m.Value.BooleanValue));
            Assert.True(Assert.Single(Of(LanLinkObservations.Signal("cellular", PhoneTransportSnapshotCollector.MeteredMetric))).Value.BooleanValue);
            Assert.Empty(Of(LanLinkObservations.Signal("vpn", PhoneTransportSnapshotCollector.DownstreamMetric))); // overlay never gets the underlay's estimate.
            Assert.Single(Of(LanLinkObservations.Signal("vpn", "up")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Dictionary<string, RelationshipObservation>> RelationshipsAsync(MobilePeerRuntime phone, string peerId)
    {
        var result = new Dictionary<string, RelationshipObservation>(StringComparer.Ordinal);
        foreach (var name in new[] { "wifi", "cellular", "ethernet", "vpn", "other" })
        {
            foreach (var r in await phone.Store.ReadRelationshipsAsync(LanEntityIds.Interface(peerId, name)))
            {
                result[r.RelationshipId] = r;
            }
        }

        return result;
    }

    private sealed class TransportReader : IMobileDeviceReader
    {
        public NetworkTransportSummary Network { get; set; } = NetworkTransportSummary.Create(false, false, false, false, false, T0);

        public ValueTask<MobileDeviceSample> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MobileDeviceSample(
                Network.SourceTime,
                BatteryReadingNormalizer.NormalizeCapacity(70, Network.SourceTime),
                new DeviceReading<bool>(true, false, Network.SourceTime, "test"),
                Network));
    }
}
