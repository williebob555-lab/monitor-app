using MonitorApp.Peer.Collection.Switches;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Collection.Tests;

public sealed class CiscoSg250PersistenceTests : IDisposable
{
    private const string SwitchId = "switch:rack-sg250";
    private const string Owner = "peer-rack";
    private const string AdapterId = "adapter:sg250";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sg250-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task OnlyAssignedCollectorMayPollOrWrite()
    {
        var store = await NewStoreAsync();
        var source = new ScriptedReader(Fixture(1));
        var clock = new TestClock();
        var pipeline = NewPipeline(store, source, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CollectAndPersistAsync());
        Assert.Equal(0, source.ReadCount);
        Assert.Null(await store.ReadLatestAdapterStatusAsync(AdapterId));

        await store.UpsertObservedEntityAsync(new ObservedEntity(SwitchId, "lan.switch", clock.GetUtcNow()));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            AdapterId, "monitor.cisco.sg250", "1", Owner, SwitchId, "local:snmpconfig", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CollectAndPersistAsync());
        Assert.Equal(0, source.ReadCount);

        await store.AssignCollectorAsync(new CollectorAssignment(SwitchId, AdapterId, Owner, clock.GetUtcNow()));
        var snapshot = await pipeline.CollectAndPersistAsync();
        Assert.Equal(Sg250CollectionStatus.Available, snapshot.Status);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal("available", (await store.ReadLatestAdapterStatusAsync(AdapterId))?.State);

        var wrongSource = new ScriptedReader(Fixture(1));
        var unauthorized = NewPipeline(store, wrongSource, clock, "peer-not-assigned");
        await Assert.ThrowsAsync<InvalidOperationException>(() => unauthorized.CollectAndPersistAsync());
        Assert.Equal(0, wrongSource.ReadCount);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task RecordedPortChangesSurviveRestartAndFailedPollDoesNotEraseTopology()
    {
        var store = await NewStoreAsync();
        var clock = new TestClock();
        await ProvisionAsync(store, clock);
        var source = new ScriptedReader(Fixture(1, 2), Fixture(2));
        var pipeline = NewPipeline(store, source, clock);

        var first = await pipeline.CollectAndPersistAsync();
        Assert.Equal(2, first.Ports.Count);
        var firstAt = first.ObservedAt;
        var firstPortId = SwitchId + ":if:1";
        Assert.Contains(await store.ReadIdentityClaimsAsync(SwitchId),
            c => c.Strength == IdentityStrength.Strong &&
                 c.ClaimType == "lan.switch.bridge_mac" &&
                 c.NormalizedValue == "00:11:22:33:44:55");

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await pipeline.CollectAndPersistAsync();
        Assert.Single(second.Ports);
        var portOne = Assert.Single(await store.ReadRelationshipsAsync(firstPortId));
        Assert.Equal(firstAt, portOne.ValidFrom);
        Assert.Equal(second.ObservedAt, portOne.ValidTo);
        var portTwo = Assert.Single(await store.ReadRelationshipsAsync(SwitchId + ":if:2"));
        Assert.Null(portTwo.ValidTo);
        Assert.Equal(firstAt, portTwo.ValidFrom);

        // Reopen the file-backed database as a new store and retain the original timestamps.
        var reopened = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "node.db")));
        await reopened.InitializeAsync();
        Assert.Equal(portOne, Assert.Single(await reopened.ReadRelationshipsAsync(firstPortId)));

        clock.Advance(TimeSpan.FromMinutes(1));
        var unavailable = NewPipeline(reopened, new ScriptedReader(
            new Sg250ReadException(Sg250CollectionStatus.Unavailable)), clock);
        var failed = await unavailable.CollectAndPersistAsync();
        Assert.Equal(Sg250CollectionStatus.Unavailable, failed.Status);
        Assert.Empty(failed.Topology.Relationships);
        Assert.Null(Assert.Single(await reopened.ReadRelationshipsAsync(SwitchId + ":if:2")).ValidTo);
        Assert.Equal("unavailable", (await reopened.ReadLatestAdapterStatusAsync(AdapterId))?.State);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ConflictingStrongBridgeIdentityIsRejectedWithoutCorruptingStoredClaims()
    {
        var store = await NewStoreAsync();
        var clock = new TestClock();
        await ProvisionAsync(store, clock);
        var reader = new ScriptedReader(Fixture(1), Fixture("00 11 22 33 44 66", 1));
        var pipeline = NewPipeline(store, reader, clock);
        Assert.Equal(Sg250CollectionStatus.Available, (await pipeline.CollectAndPersistAsync()).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        var rejected = await pipeline.CollectAndPersistAsync();
        Assert.Equal(Sg250CollectionStatus.Error, rejected.Status);
        Assert.Contains("identity conflicts", rejected.SafeReason);
        Assert.Empty(rejected.Topology.Entities);
        Assert.Equal("error", (await store.ReadLatestAdapterStatusAsync(AdapterId))?.State);
        var strong = (await store.ReadIdentityClaimsAsync(SwitchId))
            .Where(c => c.ClaimType == "lan.switch.bridge_mac").ToList();
        Assert.Single(strong);
        Assert.Equal("00:11:22:33:44:55", strong[0].NormalizedValue);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CollectorDisabledDuringReadCannotPersistStatusOrTopology()
    {
        var store = await NewStoreAsync();
        var clock = new TestClock();
        await ProvisionAsync(store, clock);
        var source = new CallbackReader(Fixture(1), () => store.UpsertAdapterInstanceAsync(new AdapterInstance(
            AdapterId, "monitor.cisco.sg250", "1", Owner, SwitchId, "local:snmpconfig", false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewPipeline(store, source, clock).CollectAndPersistAsync());

        Assert.Equal(1, source.ReadCount);
        Assert.Null(await store.ReadLatestAdapterStatusAsync(AdapterId));
        Assert.DoesNotContain(await store.ReadObservedEntitiesAsync(),
            entity => entity.EntityId == SwitchId + ":if:1");
        Assert.Empty(await store.ReadIdentityClaimsAsync(SwitchId));
        Assert.Empty(await store.ReadRelationshipsAsync(SwitchId));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CollidingNonPortEntityIdIsRejectedBeforeClaimsOrRelationshipsAreWritten()
    {
        var store = await NewStoreAsync();
        var clock = new TestClock();
        await ProvisionAsync(store, clock);
        var newPortId = SwitchId + ":if:1";
        var collidingPortId = SwitchId + ":if:2";
        await store.UpsertObservedEntityAsync(new ObservedEntity(
            collidingPortId, "service", clock.GetUtcNow().AddMinutes(-1)));

        var rejected = await NewPipeline(store, new ScriptedReader(Fixture(1, 2)), clock)
            .CollectAndPersistAsync();

        Assert.Equal(Sg250CollectionStatus.Error, rejected.Status);
        Assert.Equal("Stored entity identity conflicts with switch topology.", rejected.SafeReason);
        var entities = await store.ReadObservedEntitiesAsync();
        Assert.DoesNotContain(entities, entity => entity.EntityId == newPortId);
        Assert.Equal("service", Assert.Single(
            entities, entity => entity.EntityId == collidingPortId).EntityKind);
        Assert.Empty(await store.ReadIdentityClaimsAsync(SwitchId));
        Assert.Empty(await store.ReadIdentityClaimsAsync(newPortId));
        Assert.Empty(await store.ReadIdentityClaimsAsync(collidingPortId));
        Assert.Empty(await store.ReadRelationshipsAsync(SwitchId));
        Assert.Equal("error", (await store.ReadLatestAdapterStatusAsync(AdapterId))?.State);
    }

    private async Task<PeerStore> NewStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "node.db")));
        await store.InitializeAsync();
        return store;
    }

    private static Sg250CollectionPipeline NewPipeline(PeerStore store, ISg250SnmpReader reader, TestClock clock, string peer = Owner) =>
        new(new CiscoSg250Adapter(SwitchId, peer, reader, clock), AdapterId, store);

    private static async Task ProvisionAsync(PeerStore store, TestClock clock)
    {
        await store.UpsertObservedEntityAsync(new ObservedEntity(SwitchId, "lan.switch", clock.GetUtcNow()));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            AdapterId, "monitor.cisco.sg250", "1", Owner, SwitchId, "local:snmpconfig", true));
        await store.AssignCollectorAsync(new CollectorAssignment(SwitchId, AdapterId, Owner, clock.GetUtcNow()));
    }

    private static Sg250SnmpValue[] Fixture(params int[] ports) => Fixture("00 11 22 33 44 55", ports);

    private static Sg250SnmpValue[] Fixture(string mac, params int[] ports)
    {
        var result = new List<Sg250SnmpValue>
        {
            new(Sg250Oids.BridgeAddress, "Hex-STRING: " + mac),
        };
        foreach (var number in ports)
        {
            result.Add(new Sg250SnmpValue(Sg250Oids.IfType + "." + number, "INTEGER: ethernetCsmacd(6)"));
            result.Add(new Sg250SnmpValue(Sg250Oids.IfName + "." + number, "STRING: port-" + number));
            result.Add(new Sg250SnmpValue(Sg250Oids.IfOperStatus + "." + number, "INTEGER: up(1)"));
            result.Add(new Sg250SnmpValue(Sg250Oids.IfHighSpeed + "." + number, "Gauge32: 1000"));
        }

        return [.. result];
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class ScriptedReader : ISg250SnmpReader
    {
        private readonly Queue<object> steps = new();

        public ScriptedReader(params Sg250SnmpValue[][] snapshots)
        {
            foreach (var snapshot in snapshots) steps.Enqueue(snapshot);
        }

        public ScriptedReader(Sg250ReadException failure) => steps.Enqueue(failure);

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<Sg250SnmpValue>> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            var next = steps.Dequeue();
            if (next is Exception failure)
                return Task.FromException<IReadOnlyList<Sg250SnmpValue>>(failure);
            return Task.FromResult<IReadOnlyList<Sg250SnmpValue>>((Sg250SnmpValue[])next);
        }
    }

    private sealed class CallbackReader(Sg250SnmpValue[] snapshot, Func<Task> callback) : ISg250SnmpReader
    {
        public int ReadCount { get; private set; }

        public async Task<IReadOnlyList<Sg250SnmpValue>> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            await callback().ConfigureAwait(false);
            return snapshot;
        }
    }
}
