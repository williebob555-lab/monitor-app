using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Collection;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class YamahaAg06Mk2AdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(YamahaAg06Mk2ProbeStatus.Unavailable, ObservationQuality.Unavailable)]
    [InlineData(YamahaAg06Mk2ProbeStatus.PermissionDenied, ObservationQuality.PermissionDenied)]
    [InlineData(YamahaAg06Mk2ProbeStatus.Error, ObservationQuality.Error)]
    [Trait("Category", "Windows")]
    public async Task FailureStatesRemainExplicitAndNeverBecomeZero(
        YamahaAg06Mk2ProbeStatus status,
        ObservationQuality expectedQuality)
    {
        var adapter = new YamahaAg06Mk2Adapter(
            "yamaha-ag06mk2-user-1",
            "peer-a",
            new StubProbe(new YamahaAg06Mk2ProbeResult(status, null, "safe-reason")),
            () => Now);

        var observation = await adapter.CollectAsync();

        Assert.Equal(expectedQuality, observation.Quality);
        Assert.Null(observation.Device);
        Assert.Equal("safe-reason", observation.SafeReason);
        Assert.Equal(Now, observation.ObservedAt);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task AvailableDevicePreservesConfiguredIdentityCollectorAndDriverEvidence()
    {
        var device = new YamahaAg06Mk2Device(
            "Yamaha Corporation.",
            "Yamaha AG06MK2",
            "USB\\VID_0499&PID_1757&REV_0100",
            "Yamaha Corporation.",
            "2.1.7.5",
            "container-correlation-only",
            "Port_#0001.Hub_#0005");
        var adapter = new YamahaAg06Mk2Adapter(
            "yamaha-ag06mk2-user-1",
            "peer-b",
            new StubProbe(new YamahaAg06Mk2ProbeResult(
                YamahaAg06Mk2ProbeStatus.Available,
                device,
                null)),
            () => Now);

        var observation = await adapter.CollectAsync();

        Assert.Equal("yamaha-ag06mk2-user-1", observation.ObservedEntityId);
        Assert.Equal("peer-b", observation.CollectorPeerId);
        Assert.Equal(ObservationQuality.Valid, observation.Quality);
        Assert.Equal("2.1.7.5", observation.Device!.DriverVersion);
    }

    private sealed class StubProbe(YamahaAg06Mk2ProbeResult result) : IYamahaAg06Mk2Probe
    {
        public Task<YamahaAg06Mk2ProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
