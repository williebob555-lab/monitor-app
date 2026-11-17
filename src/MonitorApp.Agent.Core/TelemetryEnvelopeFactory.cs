using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public static class TelemetryEnvelopeFactory
{
    public static IReadOnlyList<TelemetryEnvelope> Create(
        string agentId,
        string bootId,
        string collectorId,
        SystemSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectorId);
        ArgumentNullException.ThrowIfNull(snapshot);

        var envelopes = new List<TelemetryEnvelope>(snapshot.Metrics.Count + snapshot.Health.Count);
        foreach (var reading in snapshot.Metrics)
        {
            var sample = new MetricSample
            {
                MetricId = reading.MetricId,
                Unit = reading.Unit,
                Quality = reading.Quality,
            };
            if (reading.Value is not null)
            {
                sample.Value = reading.Value.Clone();
            }

            envelopes.Add(new TelemetryEnvelope
            {
                AgentId = agentId,
                BootId = bootId,
                CollectorId = collectorId,
                ObservedAt = snapshot.ObservedAt.Clone(),
                Metric = sample,
            });
        }

        foreach (var status in snapshot.Health)
        {
            envelopes.Add(new TelemetryEnvelope
            {
                AgentId = agentId,
                BootId = bootId,
                CollectorId = collectorId,
                ObservedAt = snapshot.ObservedAt.Clone(),
                CollectorStatus = new CollectorStatus
                {
                    CapabilityId = status.CapabilityId,
                    State = status.State,
                    DetailCode = status.DetailCode,
                },
            });
        }

        return envelopes;
    }
}
