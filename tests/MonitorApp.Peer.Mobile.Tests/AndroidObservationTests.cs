using MonitorApp.Android.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

public sealed class AndroidObservationTests
{
    [Fact]
    public void DeclarationPassesTheProductionContractValidator()
    {
        var result = CapabilityContractValidator.Validate(AndroidCapabilityDeclaration.Create());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task ReadingsFlowThroughIngestionAsSignedRecordsKeepingUnitsQualityAndSourceTime()
    {
        var root = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            var sourceTime = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
            var reader = new ScriptedDeviceReader { Battery = 42, Charging = true, FixedTime = sourceTime };
            await using var phone = await MobilePeerRuntime.CreateAsync(
                MobilePeerOptions.Default(root, "phone"), identity, reader);
            var run = await phone.CollectOnceAsync();
            Assert.Equal(6, run.AppendedRecords);
            Assert.Equal(sourceTime, run.ObservedAt);

            var local = (await phone.Store.ReadLocalIdentityAsync())!;
            var records = await phone.Store.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, 1, 100);
            var level = Metric(records, AndroidDeviceSnapshotCollector.BatteryLevel);
            Assert.Equal(MetricUnit.Ratio, level.Metric.Unit);
            Assert.Equal(ObservationQuality.Valid, level.Metric.Quality);
            Assert.Equal(0.42, level.Metric.Value.DoubleValue, 3);
            Assert.Equal(sourceTime, level.ObservedAt.ToDateTimeOffset());
            Assert.True(Metric(records, AndroidDeviceSnapshotCollector.BatteryCharging).Metric.Value.BooleanValue);
            Assert.Equal(MetricUnit.Boolean, Metric(records, AndroidDeviceSnapshotCollector.NetworkConnected).Metric.Unit);
            Assert.All(records, record => Assert.Equal(RecordProvenance.LocalOrigin, record.Provenance));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnavailableBatteryIsRecordedExplicitlyNotAsZero()
    {
        var root = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            var reader = new ScriptedDeviceReader { Battery = -1 };
            await using var phone = await MobilePeerRuntime.CreateAsync(
                MobilePeerOptions.Default(root, "phone"), identity, reader);
            await phone.CollectOnceAsync();
            var local = (await phone.Store.ReadLocalIdentityAsync())!;
            var records = await phone.Store.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, 1, 100);
            var level = Metric(records, AndroidDeviceSnapshotCollector.BatteryLevel);
            Assert.Equal(ObservationQuality.Unavailable, level.Metric.Quality);
            Assert.Equal(MetricValue.ValueOneofCase.None, level.Metric.Value?.ValueCase ?? MetricValue.ValueOneofCase.None);
            var health = records.Select(r => TelemetryEnvelope.Parser.ParseFrom(r.RecordBytes))
                .Single(e => e.PayloadCase == TelemetryEnvelope.PayloadOneofCase.CollectorStatus && e.CollectorStatus.CapabilityId == "battery");
            Assert.Equal(CapabilityState.Unsupported, health.CollectorStatus.State);
            Assert.Equal("reading_unavailable", health.CollectorStatus.DetailCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IdentityMismatchWithExistingDatabaseIsRefused()
    {
        var root = TestDirectories.Create();
        try
        {
            using var first = new SoftwareIdentity();
            using var second = new SoftwareIdentity();
            await using (await MobilePeerRuntime.CreateAsync(MobilePeerOptions.Default(root, "phone"), first, new ScriptedDeviceReader()))
            {
            }

            await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() =>
                MobilePeerRuntime.CreateAsync(MobilePeerOptions.Default(root, "phone"), second, new ScriptedDeviceReader()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TelemetryEnvelope Metric(IReadOnlyList<StoredOriginRecord> records, string metricId) =>
        records.Select(r => TelemetryEnvelope.Parser.ParseFrom(r.RecordBytes))
            .Single(e => e.PayloadCase == TelemetryEnvelope.PayloadOneofCase.Metric && e.Metric.MetricId == metricId);
}
