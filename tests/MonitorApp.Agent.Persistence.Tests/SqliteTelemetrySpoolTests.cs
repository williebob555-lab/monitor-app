using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Persistence.Tests;

public sealed class SqliteTelemetrySpoolTests
{
    [Fact]
    [Trait("Category", "SQLite")]
    public async Task FileBackedSpoolSurvivesRestartAndContinuesSequence()
    {
        using var fixture = new SpoolFixture();
        await using (var first = await fixture.OpenAsync())
        {
            var saved = await first.EnqueueAsync(CreateEnvelope(10));
            Assert.Equal(1UL, saved.Sequence);
        }

        await using (var restarted = await fixture.OpenAsync())
        {
            var state = await restarted.GetStateAsync();
            Assert.Equal(2UL, state.NextSequence);
            Assert.Equal(1, state.PendingCount);
            var pending = await restarted.ReadBatchAsync(10, 1_000_000);
            Assert.Equal(1UL, pending[0].Sequence);
            Assert.Equal(10UL, pending[0].Metric.Value.UnsignedIntegerValue);

            var second = await restarted.EnqueueAsync(CreateEnvelope(20));
            Assert.Equal(2UL, second.Sequence);
        }
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task AcknowledgementDeletesOnlyAcceptedPrefixAndPersists()
    {
        using var fixture = new SpoolFixture();
        await using (var spool = await fixture.OpenAsync())
        {
            await spool.EnqueueAsync(CreateEnvelope(1));
            await spool.EnqueueAsync(CreateEnvelope(2));
            await spool.EnqueueAsync(CreateEnvelope(3));
            await spool.AcknowledgeAsync(2);
            var pending = await spool.ReadBatchAsync(10, 1_000_000);
            Assert.Collection(pending, envelope => Assert.Equal(3UL, envelope.Sequence));
        }

        await using var restarted = await fixture.OpenAsync();
        var state = await restarted.GetStateAsync();
        Assert.Equal(2UL, state.AcceptedThroughSequence);
        Assert.Equal(3UL, state.LowestPendingSequence);
        Assert.Equal(1, state.PendingCount);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CapacityFailurePreservesUnacknowledgedTelemetryAndSequence()
    {
        using var fixture = new SpoolFixture(maximumPendingCount: 1);
        await using var spool = await fixture.OpenAsync();
        await spool.EnqueueAsync(CreateEnvelope(1));

        await Assert.ThrowsAsync<SpoolCapacityExceededException>(
            () => spool.EnqueueAsync(CreateEnvelope(2)));

        var state = await spool.GetStateAsync();
        Assert.Equal(2UL, state.NextSequence);
        Assert.Equal(1, state.PendingCount);
        Assert.Equal(1UL, (await spool.ReadBatchAsync(10, 1_000_000))[0].Sequence);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task FailedUploadLeavesBatchForIdenticalRetryThenAckRemovesIt()
    {
        using var fixture = new SpoolFixture();
        await using var spool = await fixture.OpenAsync();
        await spool.EnqueueAsync(CreateEnvelope(1));
        await spool.EnqueueAsync(CreateEnvelope(2));
        var transport = new RecordingTransport { FailNextSend = true };
        var coordinator = new AgentSessionCoordinator(spool, transport);

        await Assert.ThrowsAsync<IOException>(() => coordinator.FlushOnceAsync(
            "agent-1",
            "boot-1",
            "0.1.0",
            Platform.Windows,
            [new Capability { Id = "cpu", State = CapabilityState.Available }]));
        Assert.Equal(2, (await spool.GetStateAsync()).PendingCount);

        var sent = await coordinator.FlushOnceAsync(
            "agent-1",
            "boot-1",
            "0.1.0",
            Platform.Windows,
            [new Capability { Id = "cpu", State = CapabilityState.Available }]);

        Assert.Equal(2, sent);
        Assert.Equal([1UL, 2UL], transport.Attempts[0]);
        Assert.Equal([1UL, 2UL], transport.Attempts[1]);
        Assert.Equal(0, (await spool.GetStateAsync()).PendingCount);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task BatchLimitNeverSendsAnOversizedOldestEnvelope()
    {
        using var fixture = new SpoolFixture();
        await using var spool = await fixture.OpenAsync();
        await spool.EnqueueAsync(CreateEnvelope(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => spool.ReadBatchAsync(10, 1));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await spool.GetStateAsync()).PendingCount);
    }

    private static TelemetryEnvelope CreateEnvelope(ulong value) => new()
    {
        AgentId = "agent-1",
        BootId = "boot-1",
        CollectorId = "system",
        ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Metric = new MetricSample
        {
            MetricId = "system.network.bytes_sent",
            Unit = MetricUnit.Bytes,
            Quality = ObservationQuality.Valid,
            Value = new MetricValue { UnsignedIntegerValue = value },
        },
    };

    private sealed class SpoolFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "MonitorApp.Agent.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly int maximumPendingCount;

        public SpoolFixture(int maximumPendingCount = 100)
        {
            this.maximumPendingCount = maximumPendingCount;
        }

        public Task<SqliteTelemetrySpool> OpenAsync() => SqliteTelemetrySpool.OpenAsync(
            new TelemetrySpoolOptions(
                Path.Combine(directory, "spool.db"),
                maximumPendingCount,
                1_000_000));

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingTransport : IAgentSessionTransport
    {
        public bool FailNextSend { get; set; }

        public List<ulong[]> Attempts { get; } = [];

        public Task<ControllerHello> OpenAsync(AgentHello hello, CancellationToken cancellationToken) =>
            Task.FromResult(new ControllerHello
            {
                ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                SessionId = Guid.NewGuid().ToString("N"),
                AcceptedThroughSequence = 0,
                MaximumBatchCount = 100,
                MaximumBatchBytes = 1_000_000,
                ServerTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

        public Task<TelemetryAck> SendAsync(TelemetryBatch batch, CancellationToken cancellationToken)
        {
            Attempts.Add(batch.Envelopes.Select(static envelope => envelope.Sequence).ToArray());
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new IOException("Simulated connection loss before acknowledgement.");
            }

            return Task.FromResult(new TelemetryAck
            {
                AcceptedThroughSequence = batch.Envelopes[^1].Sequence,
            });
        }
    }
}
