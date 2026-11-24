using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class PeerNodeRuntimeTests
{
    static PeerNodeRuntimeTests() => SqliteConnectionTracker.TrackStacks = true;

    // Tests that compare exact record counts disable the scheduled self-LAN collection, which adds its own records.
    private static readonly NodeLiveOptions NoLan = new(CollectSelfLan: false);

    /// <summary>
    /// The origin prunes its oldest records (real retention service, scheduled on the node), then a replica pairs and
    /// replicates over the real gRPC session. Whichever side dials, the replica must mark the pruned range intentionally
    /// absent, resume after it, and converge with its cursor at the origin head and no gap.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplicaMarksPrunedRangeAbsentAndConvergesWhetherTheOriginOrTheReplicaDials(bool originDials)
    {
        var originRoot = CreateTemporaryDirectory();
        var replicaRoot = CreateTemporaryDirectory();
        try
        {
            var replicaPort = FreePort();
            var originPort = FreePort();
            var time = new ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
            var policy = new PeerRetentionPolicy(TimeSpan.FromDays(7), TimeSpan.FromDays(365), TimeSpan.FromDays(7), TimeSpan.FromDays(30));
            await using var origin = await PeerNodeRuntime.StartAsync(
                new NodeOptions(originRoot, IPAddress.Loopback, originPort, 0),
                new NodeLiveOptions(CollectSelfLan: false, Time: time, Retention: policy));
            await using var replica = await PeerNodeRuntime.StartAsync(
                new NodeOptions(replicaRoot, IPAddress.Loopback, replicaPort, 0), NoLan);
            var stream = (await origin.Store.ReadLocalIdentityAsync())!.OriginStreamId;

            var pruned = (await origin.CollectOnceAsync()).AppendedRecords;
            var runs = origin.RetentionSchedule.RunCount;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                while (origin.RetentionSchedule.RunCount <= runs)
                {
                    time.Advance(TimeSpan.FromHours(1));
                    await Task.Delay(50, timeout.Token);
                }
            }

            Assert.Empty(await origin.Store.ReadOriginRangeAsync(origin.Identity.PeerId, stream, 1, 10_000));
            var kept = (await origin.CollectOnceAsync()).AppendedRecords;
            var head = (ulong)(pruned + kept);

            if (originDials)
            {
                await PairAsync(origin, replica, replicaPort);
            }
            else
            {
                await PairAsync(replica, origin, originPort);
            }

            await WaitUntilAsync(async () =>
                (await replica.Store.ReadReplicationCursorAsync(origin.Identity.PeerId, origin.Identity.PeerId, stream)).Cursor?.AcceptedThrough == head,
                TimeSpan.FromSeconds(20));
            var state = await replica.Store.ReadReplicationCursorAsync(origin.Identity.PeerId, origin.Identity.PeerId, stream);
            Assert.Empty(state.Gaps);
            var replicated = await replica.Store.ReadOriginRangeAsync(origin.Identity.PeerId, stream, 1, 10_000);
            Assert.Equal(Enumerable.Range(pruned + 1, kept).Select(value => (ulong)value), replicated.Select(record => record.Sequence));
            var range = await replica.Store.ReadPrunedRangeContainingAsync(origin.Identity.PeerId, stream, 1);
            Assert.Equal((1UL, (ulong)pruned), (range!.FirstSequence, range.LastSequence));

            // The origin learned where the replica is, so its own retention no longer waits for it.
            await WaitUntilAsync(async () =>
                (await origin.Store.ReadReplicationCursorAsync(replica.Identity.PeerId, origin.Identity.PeerId, stream)).Cursor?.AcceptedThrough == head,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            Directory.Delete(originRoot, recursive: true);
            Directory.Delete(replicaRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task IdentityIsRestartStableAndKeyFileIsNotRawKeyMaterial()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(root, "peer.db")));
            await store.InitializeAsync();
            string firstPeerId;
            byte[] firstFingerprint;
            await using (var first = new AsyncIdentity(await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
                Path.Combine(root, "identity.dpapi"), store)))
            {
                firstPeerId = first.Value.PublicIdentity.PeerId;
                firstFingerprint = first.Value.PublicIdentity.PublicKeyFingerprint;
            }

            await using (var second = new AsyncIdentity(await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
                Path.Combine(root, "identity.dpapi"), store)))
            {
                Assert.Equal(firstPeerId, second.Value.PublicIdentity.PeerId);
                Assert.Equal(firstFingerprint, second.Value.PublicIdentity.PublicKeyFingerprint);
                var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(root, "identity.dpapi"));
                Assert.False(ContainsSequence(protectedBytes, second.Value.PublicIdentity.PublicKeySpki));
                Assert.False(ContainsSequence(protectedBytes, firstFingerprint));
                using var parser = ECDsa.Create();
                Assert.ThrowsAny<CryptographicException>(() =>
                    parser.ImportPkcs8PrivateKey(protectedBytes, out _));
            }
        }
        finally
        {
            DeleteDirectory(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoNodesStartWithSeparateStoresAndExposeLocalPages()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var firstOptions = new NodeOptions(firstRoot, IPAddress.Loopback, FreePort(), 0);
            var secondOptions = new NodeOptions(secondRoot, IPAddress.Loopback, FreePort(), 0);
            await using var first = await PeerNodeRuntime.StartAsync(firstOptions);
            await using var second = await PeerNodeRuntime.StartAsync(secondOptions);
            Assert.NotEqual(first.Identity.PeerId, second.Identity.PeerId);
            Assert.NotNull(first.AdminAddress);
            Assert.NotNull(second.AdminAddress);
            Assert.NotEqual(first.AdminAddress, second.AdminAddress);

            using var client = new HttpClient();
            var firstPage = await client.GetStringAsync(first.AdminAddress);
            var secondPage = await client.GetStringAsync(second.AdminAddress);
            Assert.Contains("Monitor App local administration", firstPage, StringComparison.Ordinal);
            Assert.Contains("Monitor App local administration", secondPage, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(firstRoot, "peer.db")));
            Assert.True(File.Exists(Path.Combine(secondRoot, "peer.db")));
            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var capability = Assert.Single(await firstStore.ReadCapabilitiesAsync());
            var declaration = CapabilityModuleDeclaration.Parser.ParseFrom(capability.Descriptor);
            Assert.Equal("monitor.system", declaration.ModuleId);
            Assert.True(CapabilityContractValidator.Validate(declaration).IsValid);
        }
        finally
        {
            DeleteDirectory(firstRoot, recursive: true);
            DeleteDirectory(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TwoFreshNodesPairThroughLocalWebApproval()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var firstPeerPort = FreePort();
            var secondPeerPort = FreePort();
            await using var first = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0));
            await using var second = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0));
            await PairAsync(first, second, secondPeerPort);

            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var secondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));
            var firstTrust = await firstStore.ReadTrustedPeerAsync(second.Identity.PeerId);
            var secondTrust = await secondStore.ReadTrustedPeerAsync(first.Identity.PeerId);
            Assert.Equal(PeerTrustState.Active, firstTrust?.TrustState);
            Assert.Equal(PeerTrustState.Active, secondTrust?.TrustState);
        }
        finally
        {
            DeleteDirectory(firstRoot, recursive: true);
            DeleteDirectory(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SeparateStoresCollectOfflineThenReconnectAndConvergeWithoutDuplicates()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        var firstPeerPort = FreePort();
        var secondPeerPort = FreePort();
        try
        {
            string firstPeerId;
            string secondPeerId;
            string firstOriginStreamId;
            string secondOriginStreamId;
            int firstInitialCollection;
            int secondInitialCollection;
            await using (var first = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0), NoLan))
            await using (var second = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0), NoLan))
            {
                firstPeerId = first.Identity.PeerId;
                secondPeerId = second.Identity.PeerId;
                firstInitialCollection = (await first.CollectOnceAsync()).AppendedRecords;
                secondInitialCollection = (await second.CollectOnceAsync()).AppendedRecords;
                Assert.True(firstInitialCollection > 0);
                Assert.True(secondInitialCollection > 0);
                await PairAsync(first, second, secondPeerPort);
                var initialFirstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
                var initialSecondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));
                firstOriginStreamId = (await initialFirstStore.ReadLocalIdentityAsync())!.OriginStreamId;
                secondOriginStreamId = (await initialSecondStore.ReadLocalIdentityAsync())!.OriginStreamId;
                await WaitUntilAsync(async () =>
                {
                    var onFirst = await initialFirstStore.ReadOriginRangeAsync(
                        secondPeerId,
                        secondOriginStreamId,
                        1,
                        10_000);
                    var onSecond = await initialSecondStore.ReadOriginRangeAsync(
                        firstPeerId,
                        firstOriginStreamId,
                        1,
                        10_000);
                    return onFirst.Count == secondInitialCollection &&
                           onSecond.Count == firstInitialCollection;
                }, TimeSpan.FromSeconds(20));
            }

            int firstCollected;
            await using (var firstOffline = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0), NoLan))
            {
                firstCollected = (await firstOffline.CollectOnceAsync()).AppendedRecords;
                Assert.True(firstCollected > 0);
            }

            int secondCollected;
            await using (var secondOffline = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0), NoLan))
            {
                secondCollected = (await secondOffline.CollectOnceAsync()).AppendedRecords;
                Assert.True(secondCollected > 0);
            }

            await using var firstRestarted = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0), NoLan);
            await using var secondRestarted = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0), NoLan);
            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var secondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));

            await WaitUntilAsync(async () =>
            {
                var onFirst = await firstStore.ReadOriginRangeAsync(
                    secondPeerId,
                    secondOriginStreamId,
                    1,
                    10_000);
                var onSecond = await secondStore.ReadOriginRangeAsync(
                    firstPeerId,
                    firstOriginStreamId,
                    1,
                    10_000);
                return onFirst.Count == secondInitialCollection + secondCollected &&
                       onSecond.Count == firstInitialCollection + firstCollected;
            }, TimeSpan.FromSeconds(20));

            await AssertOriginMatchesAsync(
                firstStore,
                secondStore,
                firstPeerId,
                firstOriginStreamId,
                firstInitialCollection + firstCollected);
            await AssertOriginMatchesAsync(
                firstStore,
                secondStore,
                secondPeerId,
                secondOriginStreamId,
                secondInitialCollection + secondCollected);
        }
        finally
        {
            DeleteDirectory(firstRoot, recursive: true);
            DeleteDirectory(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HeldOpenNodeSessionStreamsNewlyCollectedRecordsBothWaysWithoutReconnecting()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var secondPeerPort = FreePort();
            await using var first = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, FreePort(), 0));
            await using var second = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0));
            await PairAsync(first, second, secondPeerPort);
            var firstStream = (await first.Store.ReadLocalIdentityAsync())!.OriginStreamId;
            var secondStream = (await second.Store.ReadLocalIdentityAsync())!.OriginStreamId;
            // The first records may ride the session's initial catch-up; once they arrived the session is held open
            // and everything measured next can only arrive live (the first node dials once; the heartbeat is 30 s).
            await CollectAndMeasureAsync(first, second);
            await CollectAndMeasureAsync(second, first);

            var toSecond = await CollectAndMeasureAsync(first, second);
            var toFirst = await CollectAndMeasureAsync(second, first);
            Console.WriteLine($"node live latency: A->B {toSecond.TotalMilliseconds:F1} ms, B->A {toFirst.TotalMilliseconds:F1} ms");

            await AssertOriginMatchesAsync(
                first.Store, second.Store, first.Identity.PeerId, firstStream,
                (int)((await first.Store.ReadLocalIdentityAsync())!.NextSequence - 1));
            await AssertOriginMatchesAsync(
                first.Store, second.Store, second.Identity.PeerId, secondStream,
                (int)((await second.Store.ReadLocalIdentityAsync())!.NextSequence - 1));
        }
        finally
        {
            DeleteDirectory(firstRoot, recursive: true);
            DeleteDirectory(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TwoNodesDialingEachOtherReceiveEveryLiveObservationOnce()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var firstPort = FreePort();
            var secondPort = FreePort();
            await using var first = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, firstPort, 0));
            await using var second = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPort, 0));
            await PairAsync(first, second, secondPort);

            // Now the second node dials the first as well: each node holds two sessions to the other.
            await second.TrackPeerAsync(first.Identity.PeerId, IPAddress.Loopback, firstPort);
            string Diagnostics() =>
                $"first->second {first.LivePushDiagnostics(second.Identity.PeerId)}, " +
                $"second->first {second.LivePushDiagnostics(first.Identity.PeerId)}";
            await WaitUntilAsync(
                () => Task.FromResult(
                    first.LivePushDiagnostics(second.Identity.PeerId).Sessions == 2 &&
                    second.LivePushDiagnostics(first.Identity.PeerId).Sessions == 2),
                TimeSpan.FromSeconds(30));

            // Let any catch-up traffic settle, then measure only what is pushed live from here on.
            await CollectAndMeasureAsync(first, second);
            await CollectAndMeasureAsync(second, first);
            await Task.Delay(500);
            var firstBefore = first.LivePushDiagnostics(second.Identity.PeerId).RecordsPushed;
            var secondBefore = second.LivePushDiagnostics(first.Identity.PeerId).RecordsPushed;

            var appendedByFirst = 0;
            var appendedBySecond = 0;
            for (var round = 0; round < 3; round++)
            {
                appendedByFirst += (await first.CollectOnceAsync()).AppendedRecords;
                appendedBySecond += (await second.CollectOnceAsync()).AppendedRecords;
            }

            var firstHead = (await first.Store.ReadLocalIdentityAsync())!.NextSequence - 1;
            var secondHead = (await second.Store.ReadLocalIdentityAsync())!.NextSequence - 1;
            await WaitUntilAsync(async () =>
                (await second.Store.ReadOriginRangeAsync(
                    first.Identity.PeerId, (await first.Store.ReadLocalIdentityAsync())!.OriginStreamId, firstHead, 1)).Count == 1 &&
                (await first.Store.ReadOriginRangeAsync(
                    second.Identity.PeerId, (await second.Store.ReadLocalIdentityAsync())!.OriginStreamId, secondHead, 1)).Count == 1,
                TimeSpan.FromSeconds(30));
            // A duplicate push on the second session would show up shortly after the first delivery.
            await Task.Delay(1000);

            Assert.True(
                appendedByFirst + appendedBySecond > 0, "Collection appended nothing; " + Diagnostics());
            Assert.True(
                appendedByFirst == first.LivePushDiagnostics(second.Identity.PeerId).RecordsPushed - firstBefore,
                $"first appended {appendedByFirst} records but pushed " +
                $"{first.LivePushDiagnostics(second.Identity.PeerId).RecordsPushed - firstBefore}; " + Diagnostics());
            Assert.True(
                appendedBySecond == second.LivePushDiagnostics(first.Identity.PeerId).RecordsPushed - secondBefore,
                $"second appended {appendedBySecond} records but pushed " +
                $"{second.LivePushDiagnostics(first.Identity.PeerId).RecordsPushed - secondBefore}; " + Diagnostics());
            await AssertOriginMatchesAsync(
                first.Store, second.Store, first.Identity.PeerId,
                (await first.Store.ReadLocalIdentityAsync())!.OriginStreamId, (int)firstHead);
            await AssertOriginMatchesAsync(
                first.Store, second.Store, second.Identity.PeerId,
                (await second.Store.ReadLocalIdentityAsync())!.OriginStreamId, (int)secondHead);
        }
        finally
        {
            DeleteDirectory(firstRoot, recursive: true);
            DeleteDirectory(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ViewerAskingForOneSignalReceivesOnlyThatSignalAtTheFastRate()
    {
        var viewerRoot = CreateTemporaryDirectory();
        var targetRoot = CreateTemporaryDirectory();
        try
        {
            var targetPort = FreePort();
            await using var viewer = await PeerNodeRuntime.StartAsync(
                new NodeOptions(viewerRoot, IPAddress.Loopback, FreePort(), 0));
            // The normal interval is an hour: anything collected during the test is the fast loop's doing. Self-description
            // (hardware, services) runs on its own schedule and is not part of the live loop, so it stays off here.
            await using var target = await PeerNodeRuntime.StartAsync(
                new NodeOptions(targetRoot, IPAddress.Loopback, targetPort, 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), DescribeSelf: false));
            await PairAsync(viewer, target, targetPort);
            var targetStream = (await target.Store.ReadLocalIdentityAsync())!.OriginStreamId;

            Assert.True((await target.CollectOnceAsync()).AppendedRecords > 1);
            var all = await target.Store.ReadOriginRangeAsync(target.Identity.PeerId, targetStream, 1, 10_000);
            var signal = all.Select(record => record.SignalId).Distinct().OrderBy(id => id, StringComparer.Ordinal).First();
            var distinctSignals = all.Select(record => record.SignalId).Distinct().Count();
            Assert.True(distinctSignals > 1, "The system collector produced a single signal; the test needs several.");
            var baseline = all[^1].Sequence;
            await WaitUntilAsync(
                async () => (await viewer.Store.ReadOriginRangeAsync(target.Identity.PeerId, targetStream, baseline, 1)).Count == 1,
                TimeSpan.FromSeconds(30));

            using var viewing = viewer.BeginViewing(target.Identity.PeerId, [signal]);
            await WaitUntilAsync(
                () => Task.FromResult(target.CurrentCollectionInterval == TimeSpan.FromSeconds(1)),
                TimeSpan.FromSeconds(30));
            var wanted = baseline + 3;
            await WaitUntilAsync(
                async () => (await viewer.Store.ReadOriginRangeAsync(target.Identity.PeerId, targetStream, wanted, 1)).Count == 1,
                TimeSpan.FromSeconds(30));

            var fresh = (await viewer.Store.ReadOriginRangeAsync(target.Identity.PeerId, targetStream, baseline + 1, 10_000))
                .Where(record => record.Sequence > baseline).ToList();
            var targetFresh = (await target.Store.ReadOriginRangeAsync(target.Identity.PeerId, targetStream, baseline + 1, 10_000))
                .Where(record => record.Sequence > baseline).ToList();
            var summary = string.Join(", ", targetFresh.GroupBy(record => record.SignalId).Select(g => $"{g.Key} x{g.Count()}"));
            Assert.True(fresh.Count >= 3, $"Viewer received {fresh.Count} fast records; target stored: {summary}");
            Assert.True(
                fresh.All(record => record.SignalId == signal) && targetFresh.All(record => record.SignalId == signal),
                $"Expected only '{signal}' while viewing; target stored: {summary}");

            // Withdrawing the interest returns the target to its normal (here: effectively idle) interval.
            viewing.Dispose();
            await WaitUntilAsync(
                () => Task.FromResult(target.CurrentCollectionInterval == TimeSpan.FromHours(1) && target.ActiveLiveInterestCount == 0),
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            DeleteDirectory(viewerRoot, recursive: true);
            DeleteDirectory(targetRoot, recursive: true);
        }
    }

    /// <summary>
    /// Chain phone-like C - relay A - B - D. C is paired only with A and views B and D. A relays C's interest for B over
    /// A's own session with B (B samples fast, narrowed to the named signal, and A receives B's fast records),
    /// but cannot relay C's interest for D: A has no session with D, and a forwarded interest carries no hops, so B never
    /// passes anything on to D. Ending C's viewing returns B to its normal interval.
    /// </summary>
    [Fact]
    public async Task RelayForwardsInterestToTheSubjectOnlyWhileTheLeaseLivesAndNeverFurther()
    {
        var roots = Enumerable.Range(0, 4).Select(_ => CreateTemporaryDirectory()).ToArray();
        try
        {
            var ports = Enumerable.Range(0, 4).Select(_ => FreePort()).ToArray();
            // Without the mesh (T080) the four nodes stay a chain, which is the topology relayed interest is for.
            var hour = new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false, Mesh: false);
            await using var viewer = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, ports[0], 0), hour);
            await using var relay = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[1], IPAddress.Loopback, ports[1], 0), hour);
            await using var subject = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[2], IPAddress.Loopback, ports[2], 0), hour);
            await using var far = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[3], IPAddress.Loopback, ports[3], 0), hour);
            await PairAsync(viewer, relay, ports[1]);
            await PairAsync(relay, subject, ports[2]);
            await PairAsync(subject, far, ports[3]);
            var subjectStream = (await subject.Store.ReadLocalIdentityAsync())!.OriginStreamId;

            string State() =>
                $"relay holds {relay.RelayedLiveInterestCount} relayed/{relay.ActiveLiveInterestCount} own; " +
                $"subject(B) interval {subject.CurrentCollectionInterval}, {subject.ActiveLiveInterestCount} granted/" +
                $"{subject.RelayedLiveInterestCount} relayed; far(D) interval {far.CurrentCollectionInterval}, " +
                $"{far.ActiveLiveInterestCount} granted/{far.RelayedLiveInterestCount} relayed; viewer holds " +
                $"{viewer.ActiveLiveInterestCount}";

            Assert.True((await subject.CollectOnceAsync()).AppendedRecords > 1);
            var all = await subject.Store.ReadOriginRangeAsync(subject.Identity.PeerId, subjectStream, 1, 10_000);
            var signal = all.Select(record => record.SignalId).Distinct().OrderBy(id => id, StringComparer.Ordinal).First();
            Assert.True(all.Select(record => record.SignalId).Distinct().Count() > 1, "The test needs several signals.");
            var baseline = all[^1].Sequence;
            await WaitUntilAsync(
                async () => (await relay.Store.ReadOriginRangeAsync(subject.Identity.PeerId, subjectStream, baseline, 1)).Count == 1,
                TimeSpan.FromSeconds(30));

            // D is two hops behind the relay: the interest is held by A but A has no session with D to pass it to.
            using var viewingFar = viewer.BeginViewing(far.Identity.PeerId);
            await WaitUntilAsync(() => Task.FromResult(relay.RelayedLiveInterestCount == 1), TimeSpan.FromSeconds(30));
            await Task.Delay(2500);
            Assert.True(
                far.CurrentCollectionInterval == TimeSpan.FromHours(1) && far.ActiveLiveInterestCount == 0 &&
                subject.ActiveLiveInterestCount == 0 && subject.RelayedLiveInterestCount == 0 &&
                subject.CurrentCollectionInterval == TimeSpan.FromHours(1),
                "Interest for a peer the relay has no session with must not reach anyone: " + State());

            // B is held by the relay: the interest is passed on, narrowed, and B (not the relay) samples fast.
            var viewingSubject = viewer.BeginViewing(subject.Identity.PeerId, [signal]);
            try
            {
                await WaitUntilAsync(
                    () => Task.FromResult(subject.CurrentCollectionInterval == TimeSpan.FromSeconds(1)),
                    TimeSpan.FromSeconds(30));
                Assert.True(subject.ActiveLiveInterestCount == 1 && subject.RelayedLiveInterestCount == 0, State());
                await WaitUntilAsync(
                    async () => (await relay.Store.ReadOriginRangeAsync(
                        subject.Identity.PeerId, subjectStream, baseline + 3, 1)).Count == 1,
                    TimeSpan.FromSeconds(30));
                var fresh = (await subject.Store.ReadOriginRangeAsync(subject.Identity.PeerId, subjectStream, baseline + 1, 10_000))
                    .Where(record => record.Sequence > baseline).ToList();
                var received = await relay.Store.ReadOriginRangeAsync(subject.Identity.PeerId, subjectStream, baseline + 1, 3);
                Assert.True(
                    fresh.Count >= 3 && fresh.All(record => record.SignalId == signal) && received.Count == 3,
                    $"B stored {fresh.Count} fast records ({string.Join(",", fresh.Select(r => r.SignalId).Distinct())}), " +
                    $"expected only '{signal}'; relay received {received.Count}; " + State());
                Assert.True(
                    relay.CurrentCollectionInterval == TimeSpan.FromHours(1) && far.CurrentCollectionInterval == TimeSpan.FromHours(1) &&
                    far.ActiveLiveInterestCount == 0 && far.RelayedLiveInterestCount == 0,
                    "Only the subject may sample fast, and nothing is forwarded again: " + State());
            }
            finally
            {
                viewingSubject.Dispose();
            }

            // The originating lease ended: the relay withdraws and B returns to its normal interval.
            await WaitUntilAsync(
                () => Task.FromResult(
                    subject.CurrentCollectionInterval == TimeSpan.FromHours(1) && subject.ActiveLiveInterestCount == 0 &&
                    relay.RelayedLiveInterestCount == 1),
                TimeSpan.FromSeconds(30)).ContinueWith(task =>
                    task.IsCompletedSuccessfully ? task : throw new Xunit.Sdk.XunitException(
                        "B did not return to normal after the lease ended: " + State()), TaskScheduler.Default).Unwrap();
            viewingFar.Dispose();
            await WaitUntilAsync(() => Task.FromResult(relay.RelayedLiveInterestCount == 0), TimeSpan.FromSeconds(30));
        }
        finally
        {
            foreach (var root in roots)
            {
                DeleteDirectory(root, recursive: true);
            }
        }
    }

    private static async Task<TimeSpan> CollectAndMeasureAsync(PeerNodeRuntime origin, PeerNodeRuntime receiver)
    {
        var originId = origin.Identity.PeerId;
        var committed = new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        var arrived = new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        using var localSubscription = origin.Store.Live.Subscribe(record =>
        {
            if (record.OriginPeerId == originId) committed.TryAdd(record.Sequence, System.Diagnostics.Stopwatch.GetTimestamp());
        });
        using var remoteSubscription = receiver.Store.Live.Subscribe(record =>
        {
            if (record.OriginPeerId == originId) arrived.TryAdd(record.Sequence, System.Diagnostics.Stopwatch.GetTimestamp());
        });
        var result = await origin.CollectOnceAsync();
        Assert.True(result.AppendedRecords > 0);
        var last = result.LastSequence!.Value;
        await WaitUntilAsync(() => Task.FromResult(arrived.ContainsKey(last)), TimeSpan.FromSeconds(30));
        var latency = System.Diagnostics.Stopwatch.GetElapsedTime(committed[last], arrived[last]);
        Assert.True(latency < TimeSpan.FromSeconds(10), $"Live delivery took {latency}.");
        return latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
    }

    [Fact]
    public async Task PairingWithOneComputerJoinsTheWholeMeshAndRemovingADeviceAnywhereRemovesItEverywhere()
    {
        var roots = Enumerable.Range(0, 3).Select(_ => CreateTemporaryDirectory()).ToArray();
        try
        {
            var ports = Enumerable.Range(0, 3).Select(_ => FreePort()).ToArray();
            var options = new NodeLiveOptions(CollectionInterval: TimeSpan.FromHours(1), CollectSelfLan: false,
                PeerStateInterval: TimeSpan.FromSeconds(1));
            await using var laptop = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[0], IPAddress.Loopback, ports[0], 0), options);
            await using var desktop = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[1], IPAddress.Loopback, ports[1], 0), options);
            await using var server = await PeerNodeRuntime.StartAsync(new NodeOptions(roots[2], IPAddress.Loopback, ports[2], 0), options);

            // Only two pairings, both through the desktop: laptop-desktop, then desktop-server.
            await PairAsync(laptop, desktop, ports[1]);
            await PairAsync(desktop, server, ports[2]);

            async Task<PeerTrustState?> TrustAsync(PeerNodeRuntime on, PeerNodeRuntime of) =>
                (await on.Store.ReadTrustedPeerAsync(of.Identity.PeerId))?.TrustState;

            // The laptop and the server trust each other without ever being paired by hand.
            await WaitUntilAsync(async () => await TrustAsync(laptop, server) == PeerTrustState.Active &&
                await TrustAsync(server, laptop) == PeerTrustState.Active, TimeSpan.FromSeconds(60));

            // And they talk directly: the server's readings reach the laptop from the server itself.
            Assert.True((await server.CollectOnceAsync()).AppendedRecords > 0);
            var serverStream = (await server.Store.ReadLocalIdentityAsync())!.OriginStreamId;
            await WaitUntilAsync(async () => (await laptop.Store.ReadOriginRangeAsync(server.Identity.PeerId, serverStream, 1, 1))
                .SingleOrDefault()?.ReceivedFromPeerId == server.Identity.PeerId, TimeSpan.FromSeconds(60));

            // Remove the server on the laptop (the owner's action, through the local API).
            using var client = CreateCookieClient();
            var csrf = await ReadCsrfAsync(client, laptop.AdminAddress!);
            using var revoke = new HttpRequestMessage(HttpMethod.Post,
                new Uri(laptop.AdminAddress!, $"/api/peers/{Uri.EscapeDataString(server.Identity.PeerId)}/revoke"));
            revoke.Headers.Add("X-CSRF-TOKEN", csrf);
            using var revoked = await client.SendAsync(revoke);
            revoked.EnsureSuccessStatusCode();

            // The desktop, which paired the server directly, drops it too.
            await WaitUntilAsync(async () => await TrustAsync(desktop, server) == PeerTrustState.Revoked, TimeSpan.FromSeconds(60));
            Assert.Equal(PeerTrustState.Active, await TrustAsync(desktop, laptop));
            Assert.Equal(PeerTrustState.Active, await TrustAsync(laptop, desktop));
        }
        finally
        {
            foreach (var root in roots)
            {
                DeleteDirectory(root, recursive: true);
            }
        }
    }

    private static async Task PairAsync(
        PeerNodeRuntime first,
        PeerNodeRuntime second,
        int secondPeerPort)
    {
        using var firstClient = CreateCookieClient();
        using var secondClient = CreateCookieClient();
        var firstCsrf = await ReadCsrfAsync(firstClient, first.AdminAddress!);
        var invitation = JsonSerializer.Serialize(new
        {
            expectedPeerId = second.Identity.PeerId,
            address = "127.0.0.1",
            port = secondPeerPort,
            expectedFingerprintSha256 = Convert.ToHexString(second.Identity.PublicKeyFingerprint),
            securityPolicy = "LoopbackPrototype",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(first.AdminAddress!, "/api/invitations"))
        {
            Content = new StringContent(invitation, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", firstCsrf);
        var invitationTask = firstClient.SendAsync(request);

        string? pairingId = null;
        for (var attempt = 0; attempt < 50 && pairingId is null; attempt++)
        {
            await Task.Delay(100);
            var json = await secondClient.GetStringAsync(new Uri(second.AdminAddress!, "/api/pairings"));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetArrayLength() > 0)
            {
                pairingId = document.RootElement[0].GetProperty("pairingId").GetString();
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(pairingId));
        var secondCsrf = await ReadCsrfAsync(secondClient, second.AdminAddress!);
        using var approval = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(second.AdminAddress!, $"/api/pairings/{Uri.EscapeDataString(pairingId!)}/approve"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        approval.Headers.Add("X-CSRF-TOKEN", secondCsrf);
        using var approvalResponse = await secondClient.SendAsync(approval);
        approvalResponse.EnsureSuccessStatusCode();
        using var invitationResponse = await invitationTask.WaitAsync(TimeSpan.FromSeconds(15));
        invitationResponse.EnsureSuccessStatusCode();
        var result = await invitationResponse.Content.ReadAsStringAsync();
        using var resultDocument = JsonDocument.Parse(result);
        Assert.Equal(1, resultDocument.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(
            "Pairing completed",
            resultDocument.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertOriginMatchesAsync(
        PeerStore first,
        PeerStore second,
        string originPeerId,
        string originStreamId,
        int expectedCount)
    {
        var firstRecords = await first.ReadOriginRangeAsync(originPeerId, originStreamId, 1, 10_000);
        var secondRecords = await second.ReadOriginRangeAsync(originPeerId, originStreamId, 1, 10_000);
        Assert.Equal(expectedCount, firstRecords.Count);
        Assert.Equal(expectedCount, secondRecords.Count);
        Assert.Equal(
            Enumerable.Range(1, expectedCount).Select(value => (ulong)value),
            firstRecords.Select(record => record.Sequence));
        Assert.Equal(
            firstRecords.Select(record => Convert.ToHexString(record.ContentHash)),
            secondRecords.Select(record => Convert.ToHexString(record.ContentHash)));
        Assert.Equal(
            firstRecords.Select(record => Convert.ToHexString(record.Signature)),
            secondRecords.Select(record => Convert.ToHexString(record.Signature)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            await Task.Delay(100, cancellation.Token);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-node-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpClient CreateCookieClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    });

    private static async Task<string> ReadCsrfAsync(HttpClient client, Uri baseAddress)
    {
        var json = await client.GetStringAsync(new Uri(baseAddress, "/api/csrf"));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("requestToken").GetString() ??
            throw new InvalidOperationException("CSRF endpoint returned no request token.");
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return true;
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle)) return true;
        }

        return false;
    }

    private sealed class AsyncIdentity(INodeIdentityProvider value) : IAsyncDisposable
    {
        public INodeIdentityProvider Value { get; } = value;

        public ValueTask DisposeAsync()
        {
            Value.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Fails (on every OS) when a SQLite connection to a database under <paramref name="path"/> is still open after the
    /// runtimes were disposed; Windows would otherwise only show it as a locked peer.db at delete time. A short grace
    /// period lets a handler that was just cancelled finish unwinding.
    /// </summary>
    internal static void AssertNoOpenConnections(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (SqliteConnectionTracker.OpenCountUnder(path) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        var open = SqliteConnectionTracker.OpenCountUnder(path);
        Assert.True(open == 0, $"{open} SQLite connection(s) still open under {path} after dispose:{Environment.NewLine}"
            + SqliteConnectionTracker.DescribeOpenUnder(path));
    }

    /// <summary>
    /// Windows keeps pooled SQLite handles (and briefly, handles of just-stopped background work) open after a runtime is
    /// disposed, so the temp directory delete clears the pools and retries briefly instead of failing a passing test.
    /// </summary>
    private static void DeleteDirectory(string path, bool recursive = true)
    {
        AssertNoOpenConnections(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(path, recursive);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }
}
