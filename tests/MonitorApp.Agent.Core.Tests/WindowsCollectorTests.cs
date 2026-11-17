using System.Diagnostics;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core.Tests;

public sealed class WindowsCollectorTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public async Task CollectAsyncReadsRealWindowsHostMetrics()
    {
        Assert.True(OperatingSystem.IsWindows(), "This platform-local test must run on Windows.");
        var identity = HostIdentityProvider.ReadCurrent();
        Assert.Equal(Platform.Windows, identity.Platform);
        Assert.False(string.IsNullOrWhiteSpace(identity.BootId));

        var collector = new SystemSnapshotCollector(Platform.Windows);
        var snapshot = await collector.CollectAsync();
        var stopwatch = Stopwatch.StartNew();
        while (snapshot.Metrics.Single(metric => metric.MetricId == "system.cpu.utilization").Quality !=
               ObservationQuality.Valid && stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Yield();
            snapshot = await collector.CollectAsync();
        }

        var cpu = snapshot.Metrics.Single(metric => metric.MetricId == "system.cpu.utilization");
        Assert.Equal(ObservationQuality.Valid, cpu.Quality);
        Assert.InRange(cpu.Value!.DoubleValue, 0D, 1D);

        var totalMemory = snapshot.Metrics.Single(metric => metric.MetricId == "system.memory.total");
        var usedMemory = snapshot.Metrics.Single(metric => metric.MetricId == "system.memory.used");
        Assert.Equal(ObservationQuality.Valid, totalMemory.Quality);
        Assert.InRange(usedMemory.Value!.UnsignedIntegerValue, 0UL, totalMemory.Value!.UnsignedIntegerValue);

        var totalStorage = snapshot.Metrics.Single(metric => metric.MetricId == "system.storage.total");
        var freeStorage = snapshot.Metrics.Single(metric => metric.MetricId == "system.storage.free");
        Assert.Equal(ObservationQuality.Valid, totalStorage.Quality);
        Assert.InRange(freeStorage.Value!.UnsignedIntegerValue, 0UL, totalStorage.Value!.UnsignedIntegerValue);

        var networkHealth = snapshot.Health.Single(item => item.CapabilityId == "network");
        var received = snapshot.Metrics.Single(metric => metric.MetricId == "system.network.bytes_received");
        var sent = snapshot.Metrics.Single(metric => metric.MetricId == "system.network.bytes_sent");
        if (networkHealth.State == CapabilityState.Available)
        {
            Assert.Equal(ObservationQuality.Valid, received.Quality);
            Assert.Equal(ObservationQuality.Valid, sent.Quality);
            Assert.NotNull(received.Value);
            Assert.NotNull(sent.Value);
        }
        else
        {
            Assert.Contains(
                networkHealth.State,
                new[] { CapabilityState.Unsupported, CapabilityState.Error });
            Assert.Null(received.Value);
            Assert.Null(sent.Value);
            Assert.NotEqual(ObservationQuality.Valid, received.Quality);
            Assert.NotEqual(ObservationQuality.Valid, sent.Quality);
        }
    }
}
