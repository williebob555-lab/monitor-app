using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Contracts.V1;
using Xunit;

namespace MonitorApp.Peer.Replication.Tests;

public sealed class OriginReplicationCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-replication-" + Guid.NewGuid().ToString("N"));
    private readonly EcdsaPeerIdentityKey originKey = EcdsaPeerIdentityKey.Create();

    [Fact]
    public async Task IdenticalRetryIsIdempotentAndCursorSurvivesRestart()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        var batch = Batch(Record(1, [1]), Record(2, [2]));

        var first = await coordinator.ApplyAsync("origin-peer", batch);
        var retry = await Coordinator(store, now: Now.AddMinutes(5)).ApplyAsync("origin-peer", batch);

        Assert.Equal(2, first.InsertedCount);
        Assert.Equal(2, retry.IdenticalRetryCount);
        Assert.Equal<ulong>(2, retry.Acknowledgement.Cursor.AcceptedThroughSequence);
        var restarted = await CreateStoreAsync();
        var state = await restarted.ReadReplicationCursorAsync("origin-peer", "origin-peer", "stream-1");
        Assert.Equal<ulong>(2, state.Cursor!.AcceptedThrough);
        Assert.Empty(state.Gaps);
        Assert.Equal(2, (await restarted.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 10)).Count);
    }

    [Fact]
    public async Task ConflictingOriginSequenceIsRejectedWithoutAdvancingCursor()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        await coordinator.ApplyAsync("origin-peer", Batch(Record(1, [1])));

        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() =>
            coordinator.ApplyAsync("origin-peer", Batch(Record(1, [9]))));

        var state = await store.ReadReplicationCursorAsync("origin-peer", "origin-peer", "stream-1");
        Assert.Equal<ulong>(1, state.Cursor!.AcceptedThrough);
        Assert.Equal([1], (await store.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 1))[0].RecordBytes);
    }

    [Fact]
    public async Task OutOfOrderBatchesTrackGapThenConverge()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);

        var later = await coordinator.ApplyAsync("origin-peer", Batch(Record(3, [3]), Record(4, [4])));
        Assert.Equal<ulong>(0, later.Acknowledgement.Cursor.AcceptedThroughSequence);
        var gap = Assert.Single(later.Gaps);
        Assert.Equal<ulong>(1, gap.FirstSequence);
        Assert.Equal<ulong>(2, gap.LastSequence);

        var earlier = await coordinator.ApplyAsync("origin-peer", Batch(Record(1, [1]), Record(2, [2])));
        Assert.Equal<ulong>(4, earlier.Acknowledgement.Cursor.AcceptedThroughSequence);
        Assert.Empty(earlier.Gaps);
        var state = await store.ReadReplicationCursorAsync("origin-peer", "origin-peer", "stream-1");
        Assert.Equal<ulong>(4, state.Cursor!.AcceptedThrough);
        Assert.Empty(state.Gaps);
    }

    [Fact]
    public async Task InvalidHashSignatureAndAlgorithmAreRejectedBeforePersistence()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        var badHash = Record(1, [1]);
        badHash.ContentSha256 = ByteString.CopyFrom(new byte[32]);
        var badSignature = Record(1, [1]);
        badSignature.OriginSignature = ByteString.CopyFrom(new byte[64]);
        var badAlgorithm = Record(1, [1]);
        badAlgorithm.SignatureAlgorithm = SignatureAlgorithm.Unspecified;

        await Assert.ThrowsAsync<OriginReplicationValidationException>(() => coordinator.ApplyAsync("origin-peer", Batch(badHash)));
        await Assert.ThrowsAsync<OriginReplicationValidationException>(() => coordinator.ApplyAsync("origin-peer", Batch(badSignature)));
        await Assert.ThrowsAsync<OriginReplicationValidationException>(() => coordinator.ApplyAsync("origin-peer", Batch(badAlgorithm)));

        Assert.Empty(await store.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 10));
        Assert.Null((await store.ReadReplicationCursorAsync("origin-peer", "origin-peer", "stream-1")).Cursor);
    }

    [Fact]
    public async Task RevokedOrDifferentSourceCannotSupplyOriginRecords()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyAsync("relay-peer", Batch(Record(1, [1]))));
        await store.RevokeTrustedPeerAsync("origin-peer", Now.AddMinutes(1));
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyAsync("origin-peer", Batch(Record(1, [1]))));
    }

    [Fact]
    public async Task ATrustedDeviceCanRelayAnOriginsRecordsOnlyUnalteredAndOnlyWhileTheOriginIsTrusted()
    {
        using var relayKey = EcdsaPeerIdentityKey.Create();
        var store = await CreateStoreAsync();
        var relay = PeerPublicIdentity.Create("relay-peer", relayKey);
        await store.UpsertTrustedPeerAsync(new TrustedPeer(relay.PeerId, relay.PublicKeySpki, relay.PublicKeyFingerprint, "Relay",
            PeerTrustState.Active, Now, null));
        var coordinator = Coordinator(store);

        // Forwarded by the relay, verified against the origin's key.
        var (one, two) = (Record(1, [1]), Record(2, [2]));
        var forwarded = await coordinator.ApplyAsync("relay-peer", Batch(one, two));
        Assert.Equal(2, forwarded.InsertedCount);
        Assert.Equal<ulong>(2, forwarded.Acknowledgement.Cursor.AcceptedThroughSequence);
        Assert.Equal("relay-peer", (await store.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 1))[0].ReceivedFromPeerId);

        // The relay cannot sign in the origin's name, nor declare the origin's records pruned.
        var forged = OriginRecordCryptography.CreateSigned(relayKey, "origin-peer", "stream-1", 3, "device-1", "adapter-1",
            "cpu.utilization", ByteString.CopyFrom([3]), Timestamp.FromDateTimeOffset(Now.AddSeconds(3)), ObservationQuality.Valid);
        await Assert.ThrowsAsync<OriginReplicationValidationException>(() => coordinator.ApplyAsync("relay-peer", Batch(forged)));
        var pruning = Batch(Record(5, [5]));
        pruning.PrunedRange = new PrunedRange { OriginPeerId = "origin-peer", OriginStreamId = "stream-1", FirstSequence = 3, LastSequence = 4 };
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyAsync("relay-peer", pruning));
        Assert.Equal(2, (await store.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 10)).Count);

        // The origin delivering the same records later is a retry, not a conflict, and its cursor starts where the relay's is.
        var direct = await coordinator.ApplyAsync("origin-peer", Batch(one, two, Record(3, [3])));
        Assert.Equal((1, 2), (direct.InsertedCount, direct.IdenticalRetryCount));
        Assert.Equal<ulong>(3, direct.Acknowledgement.Cursor.AcceptedThroughSequence);
        Assert.Equal<ulong>(3, await store.ReadHeldThroughAsync("origin-peer", "stream-1"));
        Assert.Equal("origin-peer", Assert.Single(await store.ReadHeldOriginStreamsAsync("relay-peer", 10)).OriginPeerId);

        // Once the origin is removed, nobody can relay its records.
        await store.RevokeTrustedPeerAsync("origin-peer", Now.AddMinutes(1));
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => coordinator.ApplyAsync("relay-peer", Batch(Record(4, [4]))));
    }

    [Fact]
    public async Task ARelayForwardsOnlyContiguousRecordsItHolds()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        await coordinator.ApplyAsync("origin-peer", Batch(Record(1, [1]), Record(2, [2])));
        await coordinator.ApplyAsync("origin-peer", Batch(Record(4, [4])));
        OriginRangeRequest From(ulong first) => new()
        {
            RequestId = "relay-" + first,
            OriginPeerId = "origin-peer",
            OriginStreamId = "stream-1",
            FirstSequence = first,
            MaximumRecordCount = 10,
            MaximumBatchBytes = 65536,
        };

        var head = await coordinator.ReadRelayAsync(From(1));
        Assert.Equal([1UL, 2UL], head.Records.Select(record => record.Sequence));
        Assert.True(head.HasMore);
        Assert.Equal([4UL], (await coordinator.ReadRelayAsync(From(3))).Records.Select(record => record.Sequence));
        var past = await coordinator.ReadRelayAsync(From(5));
        Assert.Empty(past.Records);
        Assert.False(past.HasMore);
        Assert.Null(past.PrunedRange);
    }

    [Fact]
    public async Task BatchRecordAndByteLimitsAreEnforced()
    {
        var store = await CreateStoreAsync();
        var countLimited = Coordinator(store, new OriginReplicationOptions(1, 4096, 100));
        await Assert.ThrowsAsync<OriginReplicationLimitException>(() =>
            countLimited.ApplyAsync("origin-peer", Batch(Record(1, [1]), Record(2, [2]))));

        var byteLimited = Coordinator(store, new OriginReplicationOptions(10, 1, 100));
        await Assert.ThrowsAsync<OriginReplicationLimitException>(() =>
            byteLimited.ApplyAsync("origin-peer", Batch(Record(1, [1]))));
        Assert.Empty(await store.ReadOriginRangeAsync("origin-peer", "stream-1", 1, 10));
    }

    [Fact]
    public async Task RangeProviderHonorsRecordLimitAndPreservesVerifiableRecords()
    {
        var store = await CreateStoreAsync();
        var coordinator = Coordinator(store);
        await coordinator.ApplyAsync("origin-peer", Batch(Record(1, [1]), Record(2, [2])));
        var response = await coordinator.ReadAsync(new OriginRangeRequest
        {
            RequestId = "range-1",
            OriginPeerId = "origin-peer",
            OriginStreamId = "stream-1",
            FirstSequence = 1,
            MaximumRecordCount = 1,
            MaximumBatchBytes = 4096,
        });

        var returned = Assert.Single(response.Records);
        Assert.True(response.HasMore);
        Assert.Equal<ulong>(1, returned.Sequence);
        Assert.True(OriginRecordCryptography.Verify(returned, PeerPublicIdentity.Create("origin-peer", originKey)));
    }

    public void Dispose()
    {
        originKey.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task<PeerStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        await store.InitializeAsync();
        var identity = PeerPublicIdentity.Create("origin-peer", originKey);
        await store.UpsertTrustedPeerAsync(new TrustedPeer(
            identity.PeerId,
            identity.PublicKeySpki,
            identity.PublicKeyFingerprint,
            "Origin",
            PeerTrustState.Active,
            Now,
            null));
        return store;
    }

    private static OriginReplicationCoordinator Coordinator(
        PeerStore store,
        OriginReplicationOptions? options = null,
        DateTimeOffset? now = null) =>
        new(store, options, () => now ?? Now.AddMinutes(2));

    private OriginRecord Record(ulong sequence, byte[] bytes) => OriginRecordCryptography.CreateSigned(
        originKey,
        "origin-peer",
        "stream-1",
        sequence,
        "device-1",
        "adapter-1",
        "cpu.utilization",
        ByteString.CopyFrom(bytes),
        Timestamp.FromDateTimeOffset(Now.AddSeconds((long)sequence)),
        ObservationQuality.Valid);

    private static OriginRecordBatch Batch(params OriginRecord[] records)
    {
        var batch = new OriginRecordBatch { RequestId = Guid.NewGuid().ToString("N") };
        batch.Records.Add(records);
        return batch;
    }
}
