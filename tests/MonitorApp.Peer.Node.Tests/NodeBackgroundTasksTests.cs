using System.Net;
using System.Net.Sockets;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class NodeBackgroundTasksTests
{
    [Fact]
    public async Task PeriodicTaskRunsAtStartThenOnEachIntervalAndSurvivesAFailedRun()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        await using var task = new NodePeriodicTask(
            "probe",
            TimeSpan.FromMinutes(5),
            _ => Interlocked.Increment(ref calls) == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask,
            time);
        task.Start();

        await WaitAsync(() => task.RunCount == 1);
        Assert.Null(task.LastFailureKind);
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(5), () => task.RunCount == 2);
        Assert.Equal(nameof(InvalidOperationException), task.LastFailureKind);
        Assert.Equal(1, task.FailureCount);
        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(5), () => task.RunCount == 3);
        Assert.Null(task.LastFailureKind);
        Assert.Throws<InvalidOperationException>(task.Start);
    }

    [Fact]
    public async Task NodeDefaultsScheduleSelfLanEveryMinuteAndRetentionEveryHourWithTheConfirmedPolicy()
    {
        var root = NewDirectory();
        try
        {
            await using var node = await PeerNodeRuntime.StartAsync(
                new NodeOptions(root, IPAddress.Loopback, FreePort(), 0),
                new NodeLiveOptions(LanSource: new FakeInterfaces(Snapshot(DateTimeOffset.UtcNow, Eth("eth0")))));

            Assert.Equal(TimeSpan.FromMinutes(1), node.LanCollection!.Interval);
            Assert.Equal(TimeSpan.FromHours(1), node.RetentionSchedule.Interval);
            Assert.Equal(
                (TimeSpan.FromDays(7), TimeSpan.FromDays(365), TimeSpan.FromDays(5 * 365)),
                (node.Retention.Policy.RawRetention, node.Retention.Policy.RollupRetention, node.Retention.Policy.EventRetention));
            await WaitAsync(() => node.LanCollection.RunCount >= 1 && node.RetentionSchedule.RunCount >= 1);
            Assert.Null(node.LanCollection.LastFailureKind);
            Assert.Null(node.RetentionSchedule.LastFailureKind);

            // The shared model follows monitoring: this computer becomes a thing and its adapter a part of it.
            Assert.Equal(TimeSpan.FromMinutes(1), node.ModelBridgeSchedule!.Interval);
            await node.ModelBridgeSchedule.RunNowAsync();
            Assert.Null(node.ModelBridgeSchedule.LastFailureKind);
            var model = await node.Model!.LoadAsync();
            var computer = model.FindBound(MonitorApp.Model.MonitoringSubjectKind.Peer, node.Identity.PeerId)!;
            Assert.Equal(Environment.MachineName, computer.Name);
            var adapter = Assert.Single(model.ChildrenOf(computer.Id));
            Assert.Equal("Network adapter eth0", model.Get(adapter.TargetId).Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelfLanCollectionRunsAtStartupAndEveryIntervalPersistingObservationsAndEndingVanishedRelationships()
    {
        var root = NewDirectory();
        try
        {
            var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var source = new FakeInterfaces(Snapshot(time.GetUtcNow(), Eth("eth0"), Eth("eth1", subnet: "10.0.0.0/24")));
            await using var node = await PeerNodeRuntime.StartAsync(
                new NodeOptions(root, IPAddress.Loopback, FreePort(), 0),
                new NodeLiveOptions(Time: time, LanSource: source));
            var peerId = node.Identity.PeerId;
            var lan = node.LanCollection!;

            await WaitAsync(() => lan.RunCount >= 1);
            Assert.Null(lan.LastFailureKind);
            var first = await ReadLinkAsync(node.Store, peerId, "lan.link.eth1.speed_mbps");
            Assert.Single(first);
            Assert.True(await HasOpenRelationshipAsync(node.Store, peerId, "eth1"));

            // eth1 disappears and eth0 renegotiates: the next scheduled run records the new speed and ends eth1's links.
            source.Snapshot = Snapshot(time.GetUtcNow().AddMinutes(1), Eth("eth0", speed: 100));
            var runs = lan.RunCount;
            await AdvanceUntilAsync(time, TimeSpan.FromMinutes(1), () => lan.RunCount > runs);

            Assert.False(await HasOpenRelationshipAsync(node.Store, peerId, "eth1"));
            Assert.True(await HasOpenRelationshipAsync(node.Store, peerId, "eth0"));
            var eth0Speed = await ReadLinkAsync(node.Store, peerId, "lan.link.eth0.speed_mbps");
            Assert.Equal(2, eth0Speed.Count);
            Assert.Equal(100, PeerHistoryQueryService.Decode(eth0Speed[^1].RecordBytes).Value!.Number);
            Assert.All(eth0Speed, record => Assert.Equal(LocalRecordCollector, record.AdapterInstanceId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetentionRunsAtStartupAndHourlyPruningRawRecordsAndLeavingRollups()
    {
        var root = NewDirectory();
        try
        {
            // The node's schedule clock is 30 days ahead, so records collected "now" are past raw retention.
            var time = new ManualTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
            var policy = new PeerRetentionPolicy(TimeSpan.FromDays(7), TimeSpan.FromDays(365), TimeSpan.FromDays(7), TimeSpan.FromDays(30));
            await using var node = await PeerNodeRuntime.StartAsync(
                new NodeOptions(root, IPAddress.Loopback, FreePort(), 0),
                new NodeLiveOptions(CollectSelfLan: false, Time: time, Retention: policy));
            await WaitAsync(() => node.RetentionSchedule.RunCount >= 1);
            var collected = (await node.CollectOnceAsync()).AppendedRecords;
            Assert.True(collected > 0);
            Assert.Equal(collected, (await node.Store.ReadOriginRangeAsync(node.Identity.PeerId, await StreamAsync(node), 1, 10_000)).Count);

            var runs = node.RetentionSchedule.RunCount;
            await AdvanceUntilAsync(time, TimeSpan.FromHours(1), () => node.RetentionSchedule.RunCount > runs);

            Assert.Null(node.RetentionSchedule.LastFailureKind);
            var stream = await StreamAsync(node);
            Assert.Empty(await node.Store.ReadOriginRangeAsync(node.Identity.PeerId, stream, 1, 10_000));
            var range = await node.Store.ReadPrunedRangeContainingAsync(node.Identity.PeerId, stream, 1);
            Assert.Equal((1UL, (ulong)collected), (range!.FirstSequence, range.LastSequence));
            var rollups = await node.Store.ReadHourlyRollupsAsync(new HourlyRollupQuery(
                DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(2), null, null, null, null, 1000));
            Assert.NotEmpty(rollups.Rollups);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string LocalRecordCollector = LanLinkObservations.CollectorId;

    private static async Task<string> StreamAsync(PeerNodeRuntime node) => (await node.Store.ReadLocalIdentityAsync())!.OriginStreamId;

    private static async Task<IReadOnlyList<StoredOriginRecord>> ReadLinkAsync(PeerStore store, string peerId, string signal)
    {
        var stream = (await store.ReadLocalIdentityAsync())!.OriginStreamId;
        return (await store.ReadOriginRangeAsync(peerId, stream, 1, 10_000)).Where(record => record.SignalId == signal).ToArray();
    }

    private static async Task<bool> HasOpenRelationshipAsync(PeerStore store, string peerId, string nic) =>
        (await store.ReadOpenRelationshipsByObserverAsync(peerId)).Any(r => r.SourceEntityId == LanEntityIds.Interface(peerId, nic));

    private static SelfInterface Eth(string name, long speed = 1000, string subnet = "192.168.1.0/24") =>
        new(name, InterfaceMedium.Ethernet, true, speed, "full", 1500, "aa:bb:cc:00:11:22", [subnet], null, null);

    private static SelfInterfaceSnapshot Snapshot(DateTimeOffset at, params SelfInterface[] nics) =>
        new(SelfCollectionState.Collected, null, nics, [], at);

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(25, cancellation.Token);
        }
    }

    // The schedule's timer exists only after its startup run finished, so keep advancing until the run is seen.
    private static async Task AdvanceUntilAsync(ManualTimeProvider time, TimeSpan step, Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            time.Advance(step);
            await Task.Delay(50, cancellation.Token);
        }
    }

    private static string NewDirectory()
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

    private sealed class FakeInterfaces(SelfInterfaceSnapshot initial) : ISelfInterfaceCollector
    {
        private SelfInterfaceSnapshot snapshot = initial;

        public SelfInterfaceSnapshot Snapshot
        {
            get => Volatile.Read(ref snapshot);
            set => Volatile.Write(ref snapshot, value);
        }

        public SelfInterfaceSnapshot Collect(DateTimeOffset now) => Snapshot;
    }
}
