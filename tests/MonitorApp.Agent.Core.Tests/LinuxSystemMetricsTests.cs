using MonitorApp.Agent.Core;

namespace MonitorApp.Agent.Core.Tests;

public sealed class LinuxSystemMetricsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ReadCpuUsesAggregateIdleAndTotalCounters()
    {
        var counters = LinuxSystemMetrics.ReadCpu(
            "cpu  100 20 30 400 50 6 7 8 9 10\ncpu0 1 2 3 4\n");

        Assert.Equal(450UL, counters.Idle);
        Assert.Equal(621UL, counters.Total);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadMemoryUsesMemAvailableAndPreservesBytes()
    {
        var counters = LinuxSystemMetrics.ReadMemory(
            "MemTotal:       1024 kB\nMemFree: 100 kB\nMemAvailable: 256 kB\n");

        Assert.Equal(1_048_576UL, counters.Total);
        Assert.Equal(786_432UL, counters.Used);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReadMemoryRejectsMissingTotal()
    {
        Assert.Throws<InvalidOperationException>(
            () => LinuxSystemMetrics.ReadMemory("MemAvailable: 256 kB\n"));
    }
}
