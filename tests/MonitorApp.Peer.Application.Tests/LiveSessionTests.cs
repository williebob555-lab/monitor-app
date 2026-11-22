using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

public sealed class LiveInterestRegistryTests
{
    private const string Local = "peer-local";

    [Fact]
    public void InterestIsGrantedClampedToTheMaximumLeaseAndExpires()
    {
        var clock = new ManualTime();
        var registry = new LiveInterestRegistry(
            new LiveInterestPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), 4, 4), clock);
        var normal = TimeSpan.FromSeconds(30);
        Assert.Equal(normal, registry.EffectiveInterval(Local, normal));

        var ack = registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 3600 });

        Assert.True(ack.Accepted);
        Assert.Equal(60u, ack.GrantedLeaseSeconds);
        Assert.Equal(1000u, ack.SamplingIntervalMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(1), registry.EffectiveInterval(Local, normal));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(registry.HasActiveInterest(Local));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(registry.HasActiveInterest(Local));
        Assert.Equal(normal, registry.EffectiveInterval(Local, normal));
        Assert.Equal(0, registry.ActiveInterestCount);
    }

    [Fact]
    public void RenewalExtendsTheLeaseAndZeroLeaseWithdraws()
    {
        var clock = new ManualTime();
        var registry = new LiveInterestRegistry(timeProvider: clock);
        registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 10 });
        clock.Advance(TimeSpan.FromSeconds(8));
        registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 10 });
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(registry.HasActiveInterest(Local));
        Assert.Equal(1, registry.ActiveInterestCount);

        var withdrawal = registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 0 });
        Assert.True(withdrawal.Accepted);
        Assert.False(registry.HasActiveInterest(Local));
    }

    [Fact]
    public void ForeignSubjectsOversizedSignalListsAndTooManyConcurrentInterestsAreRefused()
    {
        var registry = new LiveInterestRegistry(
            new LiveInterestPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), 2, 2),
            new ManualTime());
        var interest = new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 30 };

        Assert.False(registry.Grant("peer-a", Local, new LiveInterest { SubjectPeerId = "peer-other", LeaseSeconds = 30 }).Accepted);
        var tooManySignals = new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 30 };
        tooManySignals.SignalIds.AddRange(["a", "b", "c"]);
        Assert.False(registry.Grant("peer-a", Local, tooManySignals).Accepted);

        Assert.True(registry.Grant("peer-a", Local, interest).Accepted);
        Assert.True(registry.Grant("peer-b", Local, interest).Accepted);
        var refused = registry.Grant("peer-c", Local, interest);
        Assert.False(refused.Accepted);
        Assert.Contains("concurrent", refused.SafeMessage, StringComparison.Ordinal);
        Assert.True(registry.Grant("peer-a", Local, interest).Accepted); // renewal of an existing interest is not new
        Assert.Equal(2, registry.ActiveInterestCount);
    }

    [Fact]
    public void WithdrawingARequesterAndLocalViewingNotifyListeners()
    {
        var registry = new LiveInterestRegistry();
        var notifications = 0;
        using var listener = registry.Subscribe(() => Interlocked.Increment(ref notifications));

        using (registry.WatchLocal(Local))
        {
            Assert.True(registry.HasActiveInterest(Local));
        }

        Assert.False(registry.HasActiveInterest(Local));
        registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 30 });
        registry.WithdrawRequester("peer-phone");
        Assert.False(registry.HasActiveInterest(Local));
        Assert.Equal(4, notifications);
    }

    [Fact]
    public async Task AdaptiveSamplerSwitchesToTheFastIntervalOnInterestAndBackWhenItLapses()
    {
        var registry = new LiveInterestRegistry(
            new LiveInterestPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), 4, 4));
        var intervals = new List<TimeSpan>();
        var runs = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = AdaptiveSampler.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            },
            Local,
            TimeSpan.FromMinutes(10),
            registry,
            collectImmediately: true,
            interval => { lock (intervals) intervals.Add(interval); },
            cancellation.Token);

        await WaitAsync(() => Volatile.Read(ref runs) == 1);
        registry.Grant("peer-phone", Local, new LiveInterest { SubjectPeerId = Local, LeaseSeconds = 4 });
        await WaitAsync(() => Volatile.Read(ref runs) >= 3); // 10-minute interval would never reach this
        await WaitAsync(() => !registry.HasActiveInterest(Local));
        await WaitAsync(() => { lock (intervals) return intervals[^1] == TimeSpan.FromMinutes(10); });
        var settled = Volatile.Read(ref runs);
        await Task.Delay(1500);
        Assert.True(Volatile.Read(ref runs) <= settled + 1, "Sampling must return to the normal interval after the lease lapses.");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }
}

public sealed class PeerLiveSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-live-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PushesUnsentRangesWithinTheInFlightWindowAndRewindsOnAGapAcknowledgement()
    {
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        await store.InitializeAsync();
        using var key = MonitorApp.Peer.Core.EcdsaPeerIdentityKey.Create();
        var spki = key.ExportSubjectPublicKeyInfo();
        await store.SetLocalIdentityAsync(new LocalPeerIdentity(
            "peer-local", spki, System.Security.Cryptography.SHA256.HashData(spki), "key", "stream-local", 10,
            DateTimeOffset.UtcNow)); // next sequence 10: sequences 1..9 are the local head
        var requested = new List<(ulong First, ulong Last)>();
        using var session = new PeerLiveSession(new PeerLiveSessionOptions(
            store,
            "peer-remote",
            (request, _) =>
            {
                requested.Add((request.FirstSequence, request.LastSequence));
                var batch = new OriginRecordBatch { RequestId = request.RequestId };
                var last = Math.Min(request.LastSequence, request.FirstSequence + 2);
                for (var sequence = request.FirstSequence; sequence <= last; sequence++)
                {
                    batch.Records.Add(new OriginRecord { OriginPeerId = "peer-local", Sequence = sequence });
                }

                return ValueTask.FromResult(batch);
            },
            _ => ValueTask.CompletedTask,
            Interests: null,
            RequestRemotePush: true,
            MaximumInFlightBatches: 2,
            MaximumBatchRecords: 3));

        Assert.Equal([1UL, 2, 3], Sequences(await session.TryNextBatchAsync(default)));
        Assert.Equal([4UL, 5, 6], Sequences(await session.TryNextBatchAsync(default)));
        Assert.Null(await session.TryNextBatchAsync(default)); // window full: backpressure, nothing read

        session.OnBatchAcknowledged(Cursor(3));
        Assert.Equal([7UL, 8, 9], Sequences(await session.TryNextBatchAsync(default)));
        Assert.Null(await session.TryNextBatchAsync(default)); // caught up to the local head

        // The receiver acknowledges below what was sent for a batch: resend from its authoritative cursor.
        session.OnBatchAcknowledged(Cursor(4)); // oldest in flight (4..6) only reached 4
        Assert.Equal([5UL, 6, 7], Sequences(await session.TryNextBatchAsync(default)));
        Assert.Equal((5UL, 9UL), requested[^1]);
    }

    private static OriginCursor Cursor(ulong through) => new()
    {
        OriginPeerId = "peer-local",
        OriginStreamId = "stream-local",
        AcceptedThroughSequence = through,
    };

    private static ulong[] Sequences(OriginRecordBatch? batch) =>
        batch!.Records.Select(record => record.Sequence).ToArray();

    public void Dispose()
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
