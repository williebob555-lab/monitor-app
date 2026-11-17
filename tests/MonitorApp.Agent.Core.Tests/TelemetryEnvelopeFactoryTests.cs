using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using System.Globalization;

namespace MonitorApp.Agent.Core.Tests;

public sealed class TelemetryEnvelopeFactoryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void CreatePreservesValidZeroAndUnavailableValueAbsence()
    {
        var snapshot = new SystemSnapshot(
            Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-11-16T12:00:00Z", CultureInfo.InvariantCulture)),
            [
                new CollectedMetric(
                    "system.network.bytes_sent",
                    MetricUnit.Bytes,
                    ObservationQuality.Valid,
                    new MetricValue { UnsignedIntegerValue = 0 }),
                new CollectedMetric(
                    "system.cpu.utilization",
                    MetricUnit.Ratio,
                    ObservationQuality.Unavailable,
                    null),
            ],
            [new CollectorHealth("cpu", CapabilityState.Degraded, "warmup_required")]);

        var envelopes = TelemetryEnvelopeFactory.Create("agent-1", "boot-1", "system", snapshot);

        Assert.Equal(3, envelopes.Count);
        Assert.Equal(
            MetricValue.ValueOneofCase.UnsignedIntegerValue,
            envelopes[0].Metric.Value.ValueCase);
        Assert.Equal(0UL, envelopes[0].Metric.Value.UnsignedIntegerValue);
        Assert.Null(envelopes[1].Metric.Value);
        Assert.All(envelopes, envelope =>
        {
            envelope.Sequence = 1;
            Assert.True(ContractValidator.Validate(envelope).IsValid);
        });
    }
}
