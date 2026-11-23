using MonitorApp.Client.Core.Scene;
using Xunit;
using static MonitorApp.Client.Core.Tests.Scene.SceneTestData;

namespace MonitorApp.Client.Core.Tests.Scene;

[Trait("Category", "Unit")]
public sealed class HealthAndLayerTests
{
    private readonly HealthClassifier classifier = new();

    [Theory]
    [InlineData("Valid", "Fresh", 5, SceneHealth.Healthy)]
    [InlineData("Valid", "Fresh", 599, SceneHealth.Healthy)]
    [InlineData("Valid", "Fresh", 601, SceneHealth.Stale)]
    [InlineData("Valid", "Stale", 120, SceneHealth.Stale)]
    [InlineData("Valid", "Stale", 30 * 86400, SceneHealth.Stale)]
    [InlineData("Valid", "Offline", 3600, SceneHealth.Offline)]
    [InlineData("Unavailable", "Unavailable", 5, SceneHealth.Unknown)]
    [InlineData("PermissionDenied", "Unavailable", 5, SceneHealth.Degraded)]
    [InlineData("Error", "Unavailable", 5, SceneHealth.Degraded)]
    [InlineData("Valid", "Unknown", 5, SceneHealth.Unknown)]
    [InlineData("Unknown", "Unknown", 5, SceneHealth.Unknown)]
    public void ReadingBoundaries(string quality, string freshness, double age, SceneHealth expected) =>
        Assert.Equal(expected, classifier.Reading(quality, freshness, age));

    [Fact]
    public void SilenceAloneNeverProducesOffline()
    {
        // Thirty days without contact is Stale: nothing explicitly says the device is off.
        var node = classifier.Node([Reading(SignalIds.CpuUtilization, 0.1, "Ratio", freshness: "Stale", age: 30 * 86400)]);
        Assert.Equal(SceneHealth.Stale, node.Health);
        Assert.Equal("Last reading 30 d ago", node.Reason);
    }

    [Fact]
    public void NodeWithNoReadingsIsUnknownNotOffline()
    {
        var node = classifier.Node([]);
        Assert.Equal(SceneHealth.Unknown, node.Health);
        Assert.Null(node.LastSeen);
    }

    [Fact]
    public void NodeReasonsAreHumanReadable()
    {
        Assert.Equal("Last reading 14 min ago",
            classifier.Node([Reading(SignalIds.CpuUtilization, 0.1, "Ratio", freshness: "Stale", age: 14 * 60)]).Reason);
        // Something the device does not provide (Wi-Fi speed while on mobile data) never makes a working device look broken.
        var notProvided = classifier.Node([
            Reading(SignalIds.CpuUtilization, 0.1, "Ratio"),
            Reading("lan.link.wifi.speed_mbps", null, "MegabitsPerSecond", quality: "Unavailable", freshness: "Unavailable"),
        ]);
        Assert.Equal(SceneHealth.Healthy, notProvided.Health);
        var failed = classifier.Node([
            Reading(SignalIds.CpuUtilization, 0.1, "Ratio"),
            Reading(SignalIds.BatteryLevel, null, "Ratio", quality: "Error", freshness: "Unavailable"),
        ]);
        Assert.Equal(SceneHealth.Degraded, failed.Health);
        Assert.Equal("Battery level could not be read (collector error)", failed.Reason);
        var denied = classifier.Node([Reading(SignalIds.BatteryLevel, null, "Ratio", quality: "PermissionDenied", freshness: "Unavailable")]);
        Assert.Equal("Battery level could not be read (permission denied)", denied.Reason);
    }

    [Fact]
    public void MixedFreshAndStaleReadingsAreHealthyButOfflineOnlyWhenAllAre()
    {
        Assert.Equal(SceneHealth.Healthy, HealthClassifier.Aggregate([SceneHealth.Healthy, SceneHealth.Stale]));
        Assert.Equal(SceneHealth.Offline, HealthClassifier.Aggregate([SceneHealth.Offline, SceneHealth.Offline]));
        Assert.Equal(SceneHealth.Stale, HealthClassifier.Aggregate([SceneHealth.Offline, SceneHealth.Stale]));
        Assert.Equal(SceneHealth.Degraded, HealthClassifier.Aggregate([SceneHealth.Healthy, SceneHealth.Degraded]));
    }

    [Fact]
    public void FillIsDerivedFromRealProportionsWithTheExpectedUnits()
    {
        var memory = LayerBuilder.Fill(SceneLayerKind.Memory, [
            Reading(SignalIds.MemoryUsed, 8e9, "Bytes"), Reading(SignalIds.MemoryTotal, 32e9, "Bytes")]);
        Assert.Equal(0.25, memory);
        var storage = LayerBuilder.Fill(SceneLayerKind.Storage, [
            Reading(SignalIds.StorageFree, 750, "Bytes"), Reading(SignalIds.StorageTotal, 1000, "Bytes")]);
        Assert.Equal(0.25, storage);
        Assert.Equal(0.4, LayerBuilder.Fill(SceneLayerKind.Compute, [Reading(SignalIds.CpuUtilization, 0.4, "Ratio")]));
        Assert.Equal(0.8, LayerBuilder.Fill(SceneLayerKind.Power, [Reading(SignalIds.BatteryLevel, 0.8, "Ratio")]));
    }

    [Fact]
    public void FillIsNullWhenInputsAreMissingUnavailableOrInTheWrongUnit()
    {
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Memory, [Reading(SignalIds.MemoryUsed, 8e9, "Bytes")]));
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Memory, [
            Reading(SignalIds.MemoryUsed, null, "Bytes", "Unavailable", "Unavailable", "Unavailable"),
            Reading(SignalIds.MemoryTotal, 32e9, "Bytes")]));
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Memory, [
            Reading(SignalIds.MemoryUsed, 8, "Count"), Reading(SignalIds.MemoryTotal, 32, "Count")]));
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Power, [Reading(SignalIds.BatteryLevel, 80, "Count")]));
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Storage, [
            Reading(SignalIds.StorageFree, 1, "Bytes"), Reading(SignalIds.StorageTotal, 0, "Bytes")]));
        Assert.Null(LayerBuilder.Fill(SceneLayerKind.Network, [Reading(SignalIds.NetworkBytesSent, 5, "Bytes")]));
    }

    [Fact]
    public void BuildProducesOnlyKindsThatHaveReadingsAndSummarisesThem()
    {
        var points = new[]
        {
            Number("p", "peer:p", SignalIds.MemoryUsed, 12.4e9, "Bytes"),
            Number("p", "peer:p", SignalIds.MemoryTotal, 32e9, "Bytes"),
            Number("p", "peer:p", SignalIds.StorageFree, 3.1e12, "Bytes"),
            Number("p", "peer:p", SignalIds.StorageTotal, 8e12, "Bytes"),
            Number("p", "peer:p", "custom.widgets", 7, "Count"),
        };
        var layers = LayerBuilder.Build(points.Select(SignalCatalog.ToReading), classifier);
        Assert.Equal([SceneLayerKind.Memory, SceneLayerKind.Storage, SceneLayerKind.Other], layers.Select(l => l.Kind));
        Assert.Equal("12.4 GB of 32.0 GB used (39%)", layers[0].Summary);
        Assert.Equal("3.1 TB free of 8.0 TB", layers[1].Summary);
        Assert.Equal("Widgets 7", layers[2].Summary);
        Assert.Null(layers[2].Fill);
    }

    [Fact]
    public void CatalogFormatsKnownSignalsAndKeepsUnknownOnesTruthful()
    {
        Assert.Equal("Charging", Format(Flag("p", "s", SignalIds.BatteryCharging, true)));
        Assert.Equal("On battery", Format(Flag("p", "s", SignalIds.BatteryCharging, false)));
        Assert.Equal("VPN on", Format(Flag("p", "s", SignalIds.NetworkVpn, true)));
        Assert.Equal("41%", Format(Number("p", "s", SignalIds.BatteryLevel, 0.41, "Ratio")));
        Assert.Equal("4.5%", Format(Number("p", "s", SignalIds.CpuUtilization, 0.045, "Ratio")));
        Assert.Equal("1.5 TB", Format(Number("p", "s", SignalIds.StorageTotal, 1.5e12, "Bytes")));
        Assert.Equal("512 B", SignalCatalog.Bytes(512));

        var unknown = SignalCatalog.Describe("vendor.fan.speed");
        Assert.Equal(SceneLayerKind.Other, unknown.Kind);
        Assert.Equal("Fan speed", unknown.Label);
        Assert.Equal("2400 rpm", Format(Number("p", "s", "vendor.fan.speed", 2400, "rpm")));
        Assert.Equal("21.5 °C", Format(Number("p", "s", "vendor.temp", 21.5, "Celsius")));
    }

    [Fact]
    public void EverySignalTheDevicesPublishGetsAPlainLabelAndUnit()
    {
        // The ids from the phone's "Other signals" screen (2026-11-22): none may show as a raw id.
        Assert.Equal("Battery readings", SignalCatalog.Describe("collector.health.battery").Label);
        Assert.Equal("Network link readings", SignalCatalog.Describe("collector.health.lan").Label);
        Assert.Equal(SceneLayerKind.Collection, SignalCatalog.Describe("collector.health.network").Kind);
        Assert.Equal("Working", Format(Text("p", "s", "collector.health.battery", "Available")));
        Assert.Equal("Mobile data download estimate", SignalCatalog.Describe("lan.link.cellular.bandwidth_down_mbps").Label);
        Assert.Equal("Mobile data link", SignalCatalog.Describe("lan.link.cellular.up").Label);
        Assert.Equal("Wi-Fi data plan", SignalCatalog.Describe("lan.link.wifi.metered").Label);
        Assert.Equal("VPN max packet size", SignalCatalog.Describe("lan.link.vpn.mtu").Label);
        Assert.Equal("Eth0.10 link speed", SignalCatalog.Describe("lan.link.eth0.10.speed_mbps").Label);
        foreach (var id in (string[])["lan.link.wifi.up", "lan.link.wifi.speed_mbps", "lan.link.wifi.full_duplex", "lan.link.cellular.bandwidth_up_mbps"])
        {
            Assert.Equal(SceneLayerKind.Connectivity, SignalCatalog.Describe(id).Kind);
        }
        Assert.Equal("Up", Format(Flag("p", "s", "lan.link.wifi.up", true)));
        Assert.Equal("Metered", Format(Flag("p", "s", "lan.link.cellular.metered", true)));
        Assert.Equal("1 Gbit/s", Format(Number("p", "s", "lan.link.eth0.speed_mbps", 1000, "MegabitsPerSecond")));
        Assert.Equal("12.5 Mbit/s", Format(Number("p", "s", "lan.link.cellular.bandwidth_down_mbps", 12.5, "MegabitsPerSecond")));
        Assert.Equal("1500 bytes", Format(Number("p", "s", "lan.link.wifi.mtu", 1500, "Bytes")));

        // New collectors extend the catalog with data, not code.
        SignalCatalog.Register(new SignalDefinition("vendor.ups.{ups}.load", SceneLayerKind.Power, "{ups} load"));
        Assert.Equal("Rack ups load", SignalCatalog.Describe("vendor.ups.rack_ups.load").Label);
    }

    [Fact]
    public void LayerSummariesHideUnavailableReadingsAndNameLinksInPlainWords()
    {
        var points = new[]
        {
            Flag("p", "peer:p", SignalIds.NetworkConnected, true),
            Flag("p", "peer:p", SignalIds.NetworkVpn, true),
            Flag("p", "peer:p", "lan.link.cellular.up", true),
            Flag("p", "peer:p", "lan.link.vpn.up", true),
            Flag("p", "peer:p", "lan.link.wifi.up", false),
            Unavailable("p", "peer:p", "lan.link.wifi.speed_mbps", "MegabitsPerSecond"),
            Text("p", "peer:p", "collector.health.battery", "Available"),
            Text("p", "peer:p", "collector.health.network", "Available"),
        };
        var layers = LayerBuilder.Build(points.Select(SignalCatalog.ToReading), classifier);
        var connectivity = layers.Single(l => l.Kind == SceneLayerKind.Connectivity);
        Assert.Equal("Connected · VPN on · Links up: Mobile data, VPN", connectivity.Summary);
        Assert.Equal(SceneHealth.Healthy, connectivity.Health);
        Assert.Equal("All 2 collectors working", layers.Single(l => l.Kind == SceneLayerKind.Collection).Summary);
        Assert.Equal("Monitoring status", layers.Single(l => l.Kind == SceneLayerKind.Collection).Title);
    }

    [Fact]
    public void UnavailableReadingsCarryNoNumberAndNeverShowZero()
    {
        var reading = SignalCatalog.ToReading(Unavailable("p", "s", SignalIds.CpuUtilization, "Ratio"));
        Assert.Null(reading.Number);
        Assert.StartsWith("Unavailable", reading.DisplayValue, StringComparison.Ordinal);
        Assert.NotEqual("0%", reading.DisplayValue);
    }

    private static string Format(HistoryPointDto point) => SignalCatalog.Format(point);
}
