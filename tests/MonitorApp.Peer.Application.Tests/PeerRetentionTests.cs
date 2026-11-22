using System.Security.Cryptography;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

public sealed class PeerRetentionTests : IDisposable
{
    private const string Local = "peer-a";
    private const string Stream = "origin-a";
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MonitorApp.Peer.Application.Tests", Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock = new() { UtcNow = Now };
    private PeerStore store = null!;
    private ulong nextSequence = 1;

    // Event retention equal to raw retention: nothing is kept as a transition, isolating the other rules.
    private static readonly PeerRetentionPolicy NoEvents =
        new(TimeSpan.FromDays(2), TimeSpan.FromDays(365), TimeSpan.FromDays(2), TimeSpan.FromDays(30));

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task RollupAggregatesNumbersAndCountsStatesWithoutAveragingThem()
    {
        await InitializeAsync();
        var hour = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        await AppendAsync(hour.AddMinutes(1), "Valid", 10);
        await AppendAsync(hour.AddMinutes(2), "Valid", 30);
        await AppendAsync(hour.AddMinutes(3), "Estimated", 20);
        await AppendAsync(hour.AddMinutes(4), "Unavailable", null);
        await AppendAsync(hour.AddMinutes(5), "PermissionDenied", null);
        await AppendAsync(hour.AddMinutes(6), "Error", 999);

        var report = await Service().RunAsync();

        Assert.Equal(1, report.RollupHoursComputed);
        var rollup = Assert.Single((await store.ReadHourlyRollupsAsync(Window(hour))).Rollups);
        Assert.Equal(6, rollup.SampleCount);
        Assert.Equal(3, rollup.NumericCount);
        Assert.Equal(10, rollup.MinValue);
        Assert.Equal(30, rollup.MaxValue);
        Assert.Equal(60, rollup.SumValue);
        Assert.Equal((2, 1, 1, 1, 1, 0), (rollup.ValidCount, rollup.EstimatedCount, rollup.UnavailableCount, rollup.DeniedCount, rollup.ErrorCount, rollup.OtherQualityCount));
        Assert.Equal("Error", rollup.WorstQuality);
        Assert.Equal(hour.AddMinutes(1), rollup.FirstObservedAt);
        Assert.Equal(hour.AddMinutes(6), rollup.LastObservedAt);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task RerunIsIdempotentAndLateArrivalsRecomputeTheRetainedHour()
    {
        await InitializeAsync();
        var hour = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        await AppendAsync(hour.AddMinutes(1), "Valid", 1);
        await AppendAsync(hour.AddMinutes(2), "Valid", 3);
        var service = Service();

        await service.RunAsync();
        var first = (await store.ReadHourlyRollupsAsync(Window(hour))).Rollups;
        var second = await service.RunAsync();

        Assert.Equal(first, (await store.ReadHourlyRollupsAsync(Window(hour))).Rollups);
        Assert.Equal((0, 0, 0), (second.RollupHoursComputed, second.RollupRowsWritten, second.RawDeleted));

        await AppendAsync(hour.AddMinutes(3), "Valid", 8);
        await service.RunAsync();
        var updated = Assert.Single((await store.ReadHourlyRollupsAsync(Window(hour))).Rollups);
        Assert.Equal(3, updated.SampleCount);
        Assert.Equal(12, updated.SumValue);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task PruningKeepsTransitionsAndPinnedWindowsAndRecordsTombstones()
    {
        await InitializeAsync();
        var start = new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
        await AppendAsync(start.AddMinutes(1), "Valid", 1);   // 1: first of series, kept as transition
        await AppendAsync(start.AddMinutes(2), "Valid", 2);   // 2: pruned
        await AppendAsync(start.AddMinutes(3), "Error", null); // 3: transition
        await AppendAsync(start.AddMinutes(4), "Error", null); // 4: pinned
        await AppendAsync(start.AddMinutes(5), "Valid", 5);   // 5: transition
        await AppendAsync(start.AddMinutes(6), "Valid", 6);   // 6: pruned
        await AppendAsync(Now.AddHours(-3), "Valid", 7);      // 7: recent
        await store.PinIncidentAsync(new PinnedIncident("inc-1", "outage", start.AddMinutes(4), start.AddMinutes(4).AddSeconds(30), Now));

        var report = await Service().RunAsync();

        Assert.Equal(2, report.RawDeleted);
        Assert.Equal(1, report.RawRetainedPinned);
        Assert.Equal(3, report.RawRetainedTransitions);
        await AssertRemainingAsync(1UL, 3, 4, 5, 7);
        var hourRollup = Assert.Single((await store.ReadHourlyRollupsAsync(Window(start))).Rollups);
        Assert.Equal(6, hourRollup.SampleCount);
        Assert.Equal(2, hourRollup.UnavailableCount + hourRollup.ErrorCount);
        Assert.NotNull(await store.ReadPrunedRangeContainingAsync(Local, Stream, 2));
        Assert.NotNull(await store.ReadPrunedRangeContainingAsync(Local, Stream, 6));
        Assert.Null(await store.ReadPrunedRangeContainingAsync(Local, Stream, 3));

        Assert.True(await store.UnpinIncidentAsync("inc-1"));
        await Service().RunAsync();
        await AssertRemainingAsync(1UL, 3, 5, 7);
        Assert.Equal<ulong>(4, (await store.ReadPrunedRangeContainingAsync(Local, Stream, 4))!.LastSequence);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task TransitionsAreKeptOnlyUntilEventRetentionExpires()
    {
        await InitializeAsync();
        var policy = new PeerRetentionPolicy(TimeSpan.FromDays(2), TimeSpan.FromDays(365), TimeSpan.FromDays(30), TimeSpan.FromDays(30));
        await AppendAsync(Now.AddDays(-40), "Valid", 1);   // transition, beyond event retention
        await AppendAsync(Now.AddDays(-40).AddMinutes(1), "Valid", 2);
        await AppendAsync(Now.AddDays(-10), "Error", null); // transition inside event retention
        await AppendAsync(Now.AddDays(-10).AddMinutes(1), "Error", null);

        await Service(policy).RunAsync();

        await AssertRemainingAsync(3UL);
        Assert.Equal<ulong>(2, (await store.ReadPrunedRangeContainingAsync(Local, Stream, 1))!.LastSequence);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task RecordsLaggingTrustedPeersAreHeldUntilAcknowledgedOrHardCap()
    {
        await InitializeAsync();
        await TrustAsync("peer-b", PeerTrustState.Active);
        await TrustAsync("peer-c", PeerTrustState.Revoked);
        await AppendAsync(Now.AddDays(-40), "Valid", 1); // 1: beyond the hard cap, pruned despite lag
        await AppendAsync(Now.AddDays(-10), "Valid", 2); // 2: acknowledged
        await AppendAsync(Now.AddDays(-10).AddMinutes(1), "Valid", 3); // 3: not acknowledged
        await AppendAsync(Now.AddDays(-10).AddMinutes(2), "Valid", 4); // 4: not acknowledged
        await AckAsync("peer-b", 2);

        var held = await Service(NoEvents).RunAsync();

        await AssertRemainingAsync(3UL, 4);
        Assert.Equal((2, 2), (held.RawDeleted, held.RawHeldForReplication));

        await AckAsync("peer-b", 4);
        var released = await Service(NoEvents).RunAsync();

        await AssertRemainingAsync();
        Assert.Equal((2, 0), (released.RawDeleted, released.RawHeldForReplication));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReplicaRequestingPrunedRangeGetsExplicitOutcomeAndBatchesStopAtTheRange()
    {
        await InitializeAsync();
        await TrustAsync("peer-b", PeerTrustState.Active);
        await AppendAsync(Now.AddDays(-10), "Valid", 1);
        await AppendAsync(Now.AddDays(-10).AddMinutes(1), "Valid", 2);
        await AppendAsync(Now.AddDays(-10).AddMinutes(2), "Valid", 3); // pinned, survives
        await AppendAsync(Now.AddDays(-10).AddMinutes(3), "Valid", 4);
        await AppendAsync(Now.AddHours(-1), "Valid", 5);
        await store.PinIncidentAsync(new PinnedIncident("inc", "keep", Now.AddDays(-10).AddMinutes(2), Now.AddDays(-10).AddMinutes(2).AddSeconds(1), Now));
        await AckAsync("peer-b", 5);
        await Service(NoEvents).RunAsync();
        var coordinator = new OriginReplicationCoordinator(store);

        var pruned = await Assert.ThrowsAsync<OriginRangePrunedException>(() => coordinator.ReadAsync(Range(1)));
        Assert.Equal<ulong>(3, pruned.ResumeSequence);

        var survivor = await coordinator.ReadAsync(Range(3));
        Assert.Equal([3UL], survivor.Records.Select(record => record.Sequence).ToArray());
        Assert.True(survivor.HasMore);

        var afterSurvivor = await Assert.ThrowsAsync<OriginRangePrunedException>(() => coordinator.ReadAsync(Range(4)));
        Assert.Equal<ulong>(5, afterSurvivor.ResumeSequence);
        var tail = await coordinator.ReadAsync(Range(5));
        Assert.Equal([5UL], tail.Records.Select(record => record.Sequence).ToArray());
        Assert.False(tail.HasMore);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task HistoryServesHourlyRollupsOutsideRawRetentionAndRawInside()
    {
        await InitializeAsync();
        var old = new DateTimeOffset(2026, 11, 10, 6, 0, 0, TimeSpan.Zero);
        await AppendAsync(old.AddMinutes(1), "Valid", 10);
        await AppendAsync(old.AddMinutes(2), "Valid", 20);
        await AppendAsync(old.AddHours(1).AddMinutes(1), "Unavailable", null);
        await AppendAsync(Now.AddHours(-2), "Valid", 5);
        await Service().RunAsync();
        var history = new PeerHistoryQueryService(store, clock, TimeSpan.FromMinutes(5));
        var oldWindow = (From: old.ToString("O"), To: old.AddDays(1).ToString("O"));

        var auto = await history.QueryHistoryAsync(oldWindow.From, oldWindow.To, null, null, null, null, null, default);
        Assert.Equal("hourly", auto.Resolution);
        Assert.Empty(auto.Points);
        Assert.Equal(2, auto.Count);
        Assert.Equal(15, auto.Rollups![0].Numeric!.Mean);
        Assert.Null(auto.Rollups[1].Numeric);
        Assert.Equal(1, auto.Rollups[1].QualityCounts.Unavailable);

        var firstPage = await history.QueryHistoryAsync(oldWindow.From, oldWindow.To, null, null, null, 1, null, default, "hourly");
        Assert.True(firstPage.Truncated);
        var secondPage = await history.QueryHistoryAsync(oldWindow.From, oldWindow.To, null, null, null, 1, firstPage.NextCursor, default, "hourly");
        Assert.Equal(old.AddHours(1), Assert.Single(secondPage.Rollups!).HourStart);

        var recent = await history.QueryHistoryAsync(Now.AddHours(-3).ToString("O"), Now.ToString("O"), null, null, null, null, null, default);
        Assert.Equal("raw", recent.Resolution);
        Assert.Single(recent.Points);

        var forcedRaw = await history.QueryHistoryAsync(oldWindow.From, oldWindow.To, null, null, null, null, null, default, "raw");
        Assert.Equal("raw", forcedRaw.Resolution);
        Assert.Equal(2, forcedRaw.Count); // the middle Valid record was pruned; first-of-series and the Unavailable transition remain
        await Assert.ThrowsAsync<PeerQueryException>(() =>
            history.QueryHistoryAsync(oldWindow.From, oldWindow.To, null, null, null, null, null, default, "minutely"));
    }

    private static OriginRangeRequest Range(ulong first) => new()
    {
        RequestId = "range-" + first,
        OriginPeerId = Local,
        OriginStreamId = Stream,
        FirstSequence = first,
        MaximumRecordCount = 100,
        MaximumBatchBytes = 1_000_000,
    };

    private static HourlyRollupQuery Window(DateTimeOffset hour) => new(hour, hour.AddHours(1), null, null, null, null, 100);

    private PeerRetentionService Service(PeerRetentionPolicy? policy = null) => new(store, clock, policy);

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        await store.InitializeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        await store.SetLocalIdentityAsync(new LocalPeerIdentity(Local, spki, SHA256.HashData(spki), "keystore://peer-a", Stream, 1, Now.AddDays(-400)));
    }

    private async Task TrustAsync(string peerId, PeerTrustState state)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        await store.UpsertTrustedPeerAsync(new TrustedPeer(
            peerId, spki, SHA256.HashData(spki), peerId, state, Now.AddDays(-400), state == PeerTrustState.Revoked ? Now.AddDays(-399) : null));
    }

    private Task AckAsync(string peerId, ulong through) =>
        store.UpdateReplicationCursorAsync(new ReplicationCursor(peerId, Local, Stream, through, "raw", Now), []);

    private async Task AppendAsync(DateTimeOffset observedAt, string quality, double? value)
    {
        var metric = new MetricSample { MetricId = "cpu", Unit = MetricUnit.Ratio };
        if (value is { } number)
        {
            metric.Value = new MetricValue { DoubleValue = number };
        }

        var bytes = new TelemetryEnvelope { AgentId = "agent", Metric = metric }.ToByteArray();
        await store.AppendOriginRecordAsync(new OriginRecordWrite(
            Local, Stream, nextSequence++, "host-a", "adapter", "cpu", bytes, RandomNumberGenerator.GetBytes(64),
            observedAt, observedAt.AddMilliseconds(5), null, RecordProvenance.LocalOrigin, quality));
    }

    private async Task AssertRemainingAsync(params ulong[] expected) =>
        Assert.Equal(expected, await RemainingSequencesAsync());

    private async Task<ulong[]> RemainingSequencesAsync() =>
        (await store.ReadOriginRangeAsync(Local, Stream, 1, 1000)).Select(record => record.Sequence).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class FixedClock : IPeerClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
