using MonitorApp.Peer.Application;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>
/// The phone's real collection loop (AdaptiveSampler, LiveInterestRegistry, ingestion bridge, SQLite) honours the
/// signals a live viewer named: fast collections store only those, normal-interval collections still store everything.
/// </summary>
public sealed class LiveCollectionNarrowingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task FastCollectionsStoreOnlyTheViewedSignalsAndNormalIntervalCollectionsStoreEverything()
    {
        var root = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            var options = new MobilePeerOptions(
                root,
                "narrow-phone",
                CollectionInterval: TimeSpan.FromSeconds(3),
                SyncInterval: TimeSpan.FromMilliseconds(50),
                MaximumSyncBackoff: TimeSpan.FromMilliseconds(400),
                HeartbeatInterval: TimeSpan.FromSeconds(5),
                InterestPolicy: new LiveInterestPolicy(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), 16, 32));
            await using var phone = await MobilePeerRuntime.CreateAsync(options, identity, new ScriptedDeviceReader());
            var viewed = AndroidDeviceSnapshotCollector.BatteryLevel;

            phone.Start();
            // Status is published only after a whole collection (records and LAN) is stored, so the store is quiescent here.
            using var firstRun = new CancellationTokenSource(Deadline);
            while (phone.GetStatus().Collection.LastObservedAt is null)
            {
                await Task.Delay(10, firstRun.Token);
            }

            var baseline = (await WaitForRunsAsync(phone, runs => runs.Count >= 1, "the first full collection"))[0];
            Assert.True(baseline.Count > 1 && baseline.Contains(viewed),
                $"The first (no viewer) collection should be full, got: {Describe(baseline)}");

            using var viewing = phone.BeginViewing(null, [viewed]);
            // The newest run may still be being written (records land one by one), so only older runs are trusted.
            await WaitForRunsAsync(
                phone,
                all => all.Take(all.Count - 1).Skip(1).Any(run => run.SetEquals([viewed])) &&
                    all.Take(all.Count - 1).Skip(1).Any(run => run.IsSupersetOf(baseline)),
                "a narrowed fast collection and a later full normal-interval collection");
            await phone.StopAsync();
            var runs = await WaitForRunsAsync(phone, _ => true, "the final read");

            var timeline = string.Join(Environment.NewLine, runs.Select((run, index) => $"  #{index}: {Describe(run)}"));
            foreach (var run in runs.Skip(1))
            {
                Assert.True(
                    run.SetEquals([viewed]) || run.IsSupersetOf(baseline),
                    $"A collection while viewing {viewed} was neither narrowed to it nor full. Baseline: {Describe(baseline)}. Timeline:{Environment.NewLine}{timeline}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Describe(IReadOnlySet<string> run) => $"{run.Count} signals [{string.Join(", ", run.Order())}]";

    /// <summary>Stored signals of the phone's own device collection, one set per collection run, oldest first.</summary>
    private static async Task<IReadOnlyList<IReadOnlySet<string>>> WaitForRunsAsync(
        MobilePeerRuntime phone, Func<IReadOnlyList<IReadOnlySet<string>>, bool> done, string waitingFor)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        IReadOnlyList<IReadOnlySet<string>> runs = [];
        while (true)
        {
            var local = (await phone.Store.ReadLocalIdentityAsync())!;
            var records = await phone.Store.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, 1, 10_000);
            runs = [.. records
                .Where(record => !record.SignalId.StartsWith("lan.", StringComparison.Ordinal))
                .GroupBy(record => record.ObservedAt)
                .OrderBy(group => group.Key)
                .Select(group => (IReadOnlySet<string>)group.Select(record => record.SignalId).ToHashSet(StringComparer.Ordinal))];
            if (done(runs))
            {
                return runs;
            }

            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Timed out waiting for {waitingFor}. Runs so far:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, runs.Select((run, index) => $"  #{index}: {Describe(run)}")));
            }
        }
    }
}
