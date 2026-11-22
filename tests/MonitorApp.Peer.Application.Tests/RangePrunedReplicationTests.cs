using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

/// <summary>
/// Receiver-side RANGE_PRUNED handling with two real file-backed stores: the origin prunes through the production
/// retention service, the replica applies what the origin tells it through the production replication coordinators.
/// </summary>
public sealed class RangePrunedReplicationTests : IDisposable
{
    private const string Origin = "peer-a";
    private const string Stream = "stream-a";
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);
    private static readonly PeerRetentionPolicy NoEvents =
        new(TimeSpan.FromDays(2), TimeSpan.FromDays(365), TimeSpan.FromDays(2), TimeSpan.FromDays(30));

    private readonly string directory = Path.Combine(Path.GetTempPath(), "MonitorApp.RangePruned.Tests", Guid.NewGuid().ToString("N"));
    private readonly EcdsaPeerIdentityKey originKey = EcdsaPeerIdentityKey.Create();

    public void Dispose()
    {
        originKey.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReplicaPullingPrunedRangesRecordsThemAbsentDurablyAndResumesWithoutAPerpetualGap()
    {
        var (origin, replica) = await CreatePairAsync();
        var head = await PruneOriginAsync(origin); // 1..6; 3 pinned survives; 1,2 and 4,5 pruned; 6 recent survives
        var originCoordinator = new OriginReplicationCoordinator(origin);
        var replicaCoordinator = new OriginReplicationCoordinator(replica, utcNow: () => Now);

        var rounds = 0;
        var prunedAnswers = new List<PrunedOriginRange>();
        while (true)
        {
            var cursor = (await replica.ReadReplicationCursorAsync(Origin, Origin, Stream)).Cursor?.AcceptedThrough ?? 0;
            if (cursor >= head) break;
            Assert.True(++rounds < 10, "replication did not converge");
            try
            {
                var batch = await originCoordinator.ReadAsync(Range(cursor + 1));
                await replicaCoordinator.ApplyAsync(Origin, batch);
            }
            catch (OriginRangePrunedException pruned)
            {
                // What the node transports as RANGE_PRUNED with the pruned range attached.
                prunedAnswers.Add(pruned.Range!);
                var ack = await replicaCoordinator.ApplyPrunedRangeAsync(Origin, ToContract(pruned.Range!));
                Assert.True(ack.Cursor.AcceptedThroughSequence >= pruned.PrunedThroughSequence);
            }
        }

        Assert.Equal([(1UL, 2UL), (4UL, 5UL)], prunedAnswers.Select(range => (range.FirstSequence, range.LastSequence)).ToArray());
        await AssertConvergedAsync(replica, head);

        // Durable: a fresh store instance on the same file sees the absence and the cursor, and a restart does not reopen the gap.
        var reopened = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "replica.db")));
        await reopened.InitializeAsync();
        await AssertConvergedAsync(reopened, head);
        Assert.NotNull(await reopened.ReadPrunedRangeContainingAsync(Origin, Stream, 1));
        Assert.NotNull(await reopened.ReadPrunedRangeContainingAsync(Origin, Stream, 5));
        Assert.Null(await reopened.ReadPrunedRangeContainingAsync(Origin, Stream, 3));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task OriginPushingAcrossAPrunedRangeSendsItWithTheRecordsAfterIt()
    {
        var (origin, replica) = await CreatePairAsync();
        var head = await PruneOriginAsync(origin);
        var originCoordinator = new OriginReplicationCoordinator(origin);
        var replicaCoordinator = new OriginReplicationCoordinator(replica, utcNow: () => Now);

        var statements = new List<(ulong First, ulong Last, int Records)>();
        for (var round = 0; round < 10; round++)
        {
            var cursor = (await replica.ReadReplicationCursorAsync(Origin, Origin, Stream)).Cursor?.AcceptedThrough ?? 0;
            if (cursor >= head) break;
            var batch = await originCoordinator.ReadForPushAsync(Range(cursor + 1));
            if (batch.PrunedRange is { } range) statements.Add((range.FirstSequence, range.LastSequence, batch.Records.Count));
            var result = await replicaCoordinator.ApplyAsync(Origin, batch);
            Assert.Empty(result.Gaps);
        }

        Assert.Equal([(1UL, 2UL, 1), (4UL, 5UL, 1)], statements.ToArray());
        await AssertConvergedAsync(replica, head);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task PushedRangeWithNoRecordsAfterItStillAdvancesTheReplicaCursor()
    {
        var (origin, replica) = await CreatePairAsync();
        for (var i = 0; i < 3; i++) await AppendAsync(origin, Now.AddDays(-10).AddMinutes(i));
        await new PeerRetentionService(origin, new FixedClock(Now), NoEvents).RunAsync();
        var originCoordinator = new OriginReplicationCoordinator(origin);
        var replicaCoordinator = new OriginReplicationCoordinator(replica, utcNow: () => Now);

        var batch = await originCoordinator.ReadForPushAsync(Range(1, 3));
        Assert.Empty(batch.Records);
        var result = await replicaCoordinator.ApplyAsync(Origin, batch);

        Assert.Equal(3UL, result.Acknowledgement.Cursor.AcceptedThroughSequence);
        Assert.Equal(3UL, (await replica.ReadReplicationCursorAsync(Origin, Origin, Stream)).Cursor!.AcceptedThrough);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task PrunedRangeIsRejectedFromUntrustedRelayingOrGapHidingSources()
    {
        var (_, replica) = await CreatePairAsync();
        var coordinator = new OriginReplicationCoordinator(replica, utcNow: () => Now);
        var range = new PrunedRange { OriginPeerId = Origin, OriginStreamId = Stream, FirstSequence = 1, LastSequence = 2 };

        // A different peer cannot declare someone else's stream pruned.
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyPrunedRangeAsync("peer-c", range));
        // The range must start no later than the next sequence the replica needs: it may not hide missing records.
        var hiding = new PrunedRange { OriginPeerId = Origin, OriginStreamId = Stream, FirstSequence = 5, LastSequence = 9 };
        await Assert.ThrowsAsync<OriginReplicationValidationException>(() => coordinator.ApplyPrunedRangeAsync(Origin, hiding));
        Assert.Null(await replica.ReadPrunedRangeContainingAsync(Origin, Stream, 5));
        // A revoked origin is not believed.
        await replica.RevokeTrustedPeerAsync(Origin, Now);
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyPrunedRangeAsync(Origin, range));
        Assert.Null(await replica.ReadPrunedRangeContainingAsync(Origin, Stream, 1));
    }

    private static async Task AssertConvergedAsync(PeerStore replica, ulong head)
    {
        var state = await replica.ReadReplicationCursorAsync(Origin, Origin, Stream);
        Assert.Equal(head, state.Cursor!.AcceptedThrough);
        Assert.Empty(state.Gaps);
        Assert.Equal(new ulong[] { 3, 6 }, (await replica.ReadOriginRangeAsync(Origin, Stream, 1, 100)).Select(record => record.Sequence).ToArray());
    }

    // Records 1..5 are ten days old (3 is pinned and survives); record 6 is recent. Returns the head sequence.
    private async Task<ulong> PruneOriginAsync(PeerStore origin)
    {
        for (var i = 1; i <= 5; i++) await AppendAsync(origin, Now.AddDays(-10).AddMinutes(i));
        await AppendAsync(origin, Now.AddHours(-1));
        var pinned = Now.AddDays(-10).AddMinutes(3);
        await origin.PinIncidentAsync(new PinnedIncident("inc", "keep", pinned, pinned.AddSeconds(1), Now));
        var report = await new PeerRetentionService(origin, new FixedClock(Now), NoEvents).RunAsync();
        Assert.Equal(4, report.RawDeleted);
        Assert.Equal(2, report.PrunedRangesWritten);
        return 6;
    }

    private async Task<(PeerStore Origin, PeerStore Replica)> CreatePairAsync()
    {
        Directory.CreateDirectory(directory);
        var origin = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "origin.db")));
        var replica = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "replica.db")));
        await origin.InitializeAsync();
        await replica.InitializeAsync();
        var identity = PeerPublicIdentity.Create(Origin, originKey);
        await origin.SetLocalIdentityAsync(new LocalPeerIdentity(
            Origin, identity.PublicKeySpki, identity.PublicKeyFingerprint, "keystore://peer-a", Stream, 1, Now.AddDays(-400)));
        await replica.UpsertTrustedPeerAsync(new TrustedPeer(
            Origin, identity.PublicKeySpki, identity.PublicKeyFingerprint, "Origin", PeerTrustState.Active, Now.AddDays(-400), null));
        return (origin, replica);
    }

    private async Task AppendAsync(PeerStore origin, DateTimeOffset observedAt)
    {
        var local = (await origin.ReadLocalIdentityAsync())!;
        var sequence = local.NextSequence;
        var envelope = new TelemetryEnvelope
        {
            AgentId = "agent",
            Metric = new MetricSample { MetricId = "cpu", Unit = MetricUnit.Ratio, Quality = ObservationQuality.Valid, Value = new MetricValue { DoubleValue = sequence } },
        };
        var signed = OriginRecordCryptography.CreateSigned(
            originKey, Origin, Stream, sequence, "host-a", "adapter", "cpu", envelope.ToByteString(),
            Timestamp.FromDateTimeOffset(observedAt), ObservationQuality.Valid);
        await origin.AppendOriginRecordAsync(new OriginRecordWrite(
            Origin, Stream, sequence, "host-a", "adapter", "cpu", signed.RecordBytes.ToByteArray(), signed.OriginSignature.ToByteArray(),
            observedAt, observedAt.AddMilliseconds(5), null, RecordProvenance.LocalOrigin, "Valid"));
    }

    private static OriginRangeRequest Range(ulong first, ulong last = 0) => new()
    {
        RequestId = "range-" + first,
        OriginPeerId = Origin,
        OriginStreamId = Stream,
        FirstSequence = first,
        LastSequence = last,
        MaximumRecordCount = 100,
        MaximumBatchBytes = 1_000_000,
    };

    private static PrunedRange ToContract(PrunedOriginRange range) => new()
    {
        OriginPeerId = range.OriginPeerId,
        OriginStreamId = range.OriginStreamId,
        FirstSequence = range.FirstSequence,
        LastSequence = range.LastSequence,
    };

    private sealed class FixedClock(DateTimeOffset now) : IPeerClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
