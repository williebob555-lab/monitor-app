using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>
/// Live (held-open) phone and desktop sessions over real loopback HTTP/2 gRPC and file-backed SQLite: records stream
/// both ways without reconnecting, survive a server restart without duplicates, and live interest switches the
/// desktop's real collection scheduler between its normal and fast intervals.
/// </summary>
public sealed class LiveSessionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Generous: CI runners are slow on cold start. Tests wait on conditions, never fixed sleeps.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static MobilePeerOptions LiveOptions(string directory, LiveInterestPolicy? policy = null) => new(
        directory,
        "live-phone",
        CollectionInterval: TimeSpan.FromHours(1),
        SyncInterval: TimeSpan.FromMilliseconds(50),
        MaximumSyncBackoff: TimeSpan.FromMilliseconds(400),
        HeartbeatInterval: TimeSpan.FromSeconds(5),
        InterestPolicy: policy);

    [Fact]
    public async Task PhoneAndDesktopStreamNewObservationsToEachOtherOverOneHeldOpenSession()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            await PairAsync(phone, desktop, "pairing-live-1");
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);
            await WaitAsync(async () => await Converged(phone, desktop));

            // The session is held open from here on: no SyncOnce, no reconnect, just new origin records.
            var phoneId = phone.Identity.PeerId;
            using var phoneLocalProbe = new CommitProbe(phone.Store, phoneId);
            using var desktopSeesPhone = new CommitProbe(desktop.Store, phoneId);
            var phoneTarget = await CollectPhoneAsync(phone);
            var toDesktop = await MeasureAsync(phoneLocalProbe, desktopSeesPhone, phoneTarget);
            output.WriteLine($"phone -> desktop live latency: {toDesktop.TotalMilliseconds:F1} ms");

            var desktopId = desktop.Identity.PeerId;
            using var desktopLocalProbe = new CommitProbe(desktop.Store, desktopId);
            using var phoneSeesDesktop = new CommitProbe(phone.Store, desktopId);
            await desktop.CollectAsync();
            var desktopTarget = (await desktop.Store.ReadLocalIdentityAsync())!.NextSequence - 1;
            var toPhone = await MeasureAsync(desktopLocalProbe, phoneSeesDesktop, desktopTarget);
            output.WriteLine($"desktop -> phone live latency: {toPhone.TotalMilliseconds:F1} ms");

            await WaitAsync(async () => await Converged(phone, desktop));
            await AssertSameHistoryAsync(phone.Store, desktop.Store, phoneId);
            await AssertSameHistoryAsync(phone.Store, desktop.Store, desktopId);
            Assert.Equal(MobileSyncState.Synced, Assert.Single(phone.GetStatus().Peers).State);

            // Each side can say when the other's newest reading was taken (the "last reading" line on a device's card).
            Assert.True((await phone.Store.ReadLatestAcceptedByOriginAsync()).ContainsKey(desktopId));
            Assert.True((await desktop.Store.ReadLatestAcceptedByOriginAsync()).ContainsKey(phoneId));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task DesktopAlertReachesTheConnectedPhoneInboxWithinTwoSecondsAndIsAcknowledged()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            await PairAsync(phone, desktop, "pairing-live-alert");
            var phoneId = phone.Identity.PeerId;
            var received = new ConcurrentQueue<(string Id, uint Revision, long At)>();
            var arrival = new SemaphoreSlim(0);
            phone.NewAlerts += items =>
            {
                foreach (var item in items)
                {
                    received.Enqueue((item.Alert.AlertId, item.Alert.Revision, Stopwatch.GetTimestamp()));
                }

                arrival.Release();
            };

            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);

            // The phone's first pull on the held-open session subscribes it; after that only push (not the slow
            // safety-net pull) can deliver within the budget below.
            await WaitAsync(async () => (await desktop.Alerts.ReadRecipientsAsync()).Contains(phoneId));

            var owner = desktop.Identity.PeerId;
            var alert = new Alert(
                Alert.CreateId(owner, "threshold.disk_free", "peer:disk", DateTimeOffset.UtcNow), 1, "threshold.disk_free",
                "peer:disk", owner, AlertSeverity.Critical, AlertState.Raised, "Low free disk space", "5.0% free",
                DateTimeOffset.UtcNow, null);
            var raisedAt = Stopwatch.GetTimestamp();
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phoneId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            Assert.True(await arrival.WaitAsync(Deadline), "The alert never reached the phone.");
            Assert.True(received.TryPeek(out var first));
            var latency = Stopwatch.GetElapsedTime(raisedAt, first.At);
            output.WriteLine($"desktop alert -> phone inbox latency: {latency.TotalMilliseconds:F1} ms");
            Assert.Equal(alert.AlertId, first.Id);
            Assert.True(latency < TimeSpan.FromSeconds(2), $"Alert took {latency.TotalMilliseconds:F0} ms to reach the phone.");

            var stored = Assert.Single(await phone.ReadAlertsAsync());
            Assert.Equal(alert.AlertId, stored.Alert.AlertId);

            // The acknowledgement still follows the phone's durable write and clears the desktop outbox row.
            await WaitAsync(async () => (await desktop.Alerts.ReadOutboxAsync(phoneId)).All(row => row.AckedAt is not null));

            // The resolve travels the same way as revision 2.
            var resolveStarted = Stopwatch.GetTimestamp();
            await desktop.Alerts.ResolveAsync(owner, "threshold.disk_free", "peer:disk", DateTimeOffset.UtcNow, [phoneId],
                TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            await WaitAsync(() => received.Any(item => item.Revision == 2));
            var resolveLatency = Stopwatch.GetElapsedTime(resolveStarted, received.First(item => item.Revision == 2).At);
            output.WriteLine($"desktop resolve -> phone inbox latency: {resolveLatency.TotalMilliseconds:F1} ms");
            Assert.True(resolveLatency < TimeSpan.FromSeconds(2));
            Assert.Equal(AlertState.Resolved, Assert.Single(await phone.ReadAlertsAsync()).Alert.State);
            await WaitAsync(async () => (await desktop.Alerts.ReadOutboxAsync(phoneId)).All(row => row.AckedAt is not null));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task KillingTheServerMidSessionReconnectsAndConvergesWithoutDuplicatesThenGoesLiveAgain()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            await PairAsync(phone, desktop, "pairing-live-2");
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);
            await desktop.CollectAsync();
            await CollectPhoneAsync(phone);
            await WaitAsync(async () => await Converged(phone, desktop));

            await desktop.StopHostAsync();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Offline }]);
            await CollectPhoneAsync(phone);
            await CollectPhoneAsync(phone);
            await desktop.CollectAsync();

            await desktop.StartHostAsync();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);
            await WaitAsync(async () => await Converged(phone, desktop));
            await AssertSameHistoryAsync(phone.Store, desktop.Store, phone.Identity.PeerId);
            await AssertSameHistoryAsync(phone.Store, desktop.Store, desktop.Identity.PeerId);

            // After resuming from cursors the session is live again, not merely caught up.
            using var desktopSeesPhone = new CommitProbe(desktop.Store, phone.Identity.PeerId);
            using var phoneLocalProbe = new CommitProbe(phone.Store, phone.Identity.PeerId);
            var target = await CollectPhoneAsync(phone);
            var latency = await MeasureAsync(phoneLocalProbe, desktopSeesPhone, target);
            output.WriteLine($"phone -> desktop latency after reconnect: {latency.TotalMilliseconds:F1} ms");
            // The collection tick also appends the phone's LAN link records after the measured sequence; let them land.
            await WaitAsync(async () => await Converged(phone, desktop));
            await AssertSameHistoryAsync(phone.Store, desktop.Store, phone.Identity.PeerId);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task InterestSwitchesTheRealDesktopSchedulerToTheFastIntervalAndLeaseExpiryOrWithdrawalRevertsIt()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            var normal = TimeSpan.FromSeconds(10);
            var fast = TimeSpan.FromSeconds(1);
            await using var node = await PeerNodeRuntime.StartAsync(
                new NodeOptions(desktopRoot, IPAddress.Loopback, FreePort(), 0),
                new NodeLiveOptions(CollectionInterval: normal));
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot, new LiveInterestPolicy(fast, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 16, 32)),
                phoneIdentity,
                new ScriptedDeviceReader());
            await PairWithNodeAsync(phone, node);
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);

            Assert.Equal(normal, node.CurrentCollectionInterval);
            Assert.Equal(0, node.ActiveLiveInterestCount);

            // Withdrawal path: viewing the desktop from the phone makes it collect about once a second.
            var viewing = phone.BeginViewing(node.Identity.PeerId);
            await WaitAsync(() => node.CurrentCollectionInterval == fast && node.ActiveLiveInterestCount == 1);
            var collections = await CountCollectionsAsync(node, TimeSpan.FromSeconds(4.5));
            output.WriteLine($"collections while viewed over 4.5 s: {collections}");
            // At the normal 10 s interval a 4.5 s window holds at most one collection, so two prove the fast interval.
            // Requiring more measured the collector's own speed: a real Windows collection on a CI runner can take
            // longer than the 1 s interval. The time of one collection is still reported to tell the two apart.
            var timer = Stopwatch.StartNew();
            await node.CollectOnceAsync();
            output.WriteLine($"one collection took {timer.ElapsedMilliseconds} ms");
            Assert.True(collections >= 2, $"Expected fast sampling while viewed, saw {collections} collections; one collection took {timer.ElapsedMilliseconds} ms.");
            viewing.Dispose();
            await WaitAsync(() => node.CurrentCollectionInterval == normal && node.ActiveLiveInterestCount == 0);

            // Expiry path: the phone vanishes without withdrawing; the short lease lapses on its own.
            using var again = phone.BeginViewing(node.Identity.PeerId);
            await WaitAsync(() => node.CurrentCollectionInterval == fast && node.ActiveLiveInterestCount == 1);
            await phone.StopAsync();
            await WaitAsync(() => node.CurrentCollectionInterval == normal && node.ActiveLiveInterestCount == 0);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task RevokedPeerInterestIsWithdrawnAndRefusedAndUnknownPeersCannotDeclareInterest()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            var fast = TimeSpan.FromSeconds(1);
            await using var node = await PeerNodeRuntime.StartAsync(
                new NodeOptions(desktopRoot, IPAddress.Loopback, FreePort(), 0),
                new NodeLiveOptions(CollectionInterval: TimeSpan.FromSeconds(10)));
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot, new LiveInterestPolicy(fast, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 16, 32)),
                phoneIdentity,
                new ScriptedDeviceReader());
            await PairWithNodeAsync(phone, node);
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);
            using var viewing = phone.BeginViewing(node.Identity.PeerId);
            await WaitAsync(() => node.CurrentCollectionInterval == fast);

            await node.Store.RevokeTrustedPeerAsync(phone.Identity.PeerId, DateTimeOffset.UtcNow);
            await WaitAsync(() => node.ActiveLiveInterestCount == 0 &&
                node.CurrentCollectionInterval == TimeSpan.FromSeconds(10));
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Refused }]);

            // The phone keeps trying to view, but every renewal is refused: interest never comes back.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Assert.Equal(0, node.ActiveLiveInterestCount);
                await Task.Delay(100);
            }
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task ProductionCoordinatorRefusesLiveInterestFromUnknownAndRevokedPeers()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            var interest = new LiveInterest { SubjectPeerId = desktop.Identity.PeerId, LeaseSeconds = 30 };
            using var stranger = desktop.SessionCoordinator.OpenLiveSession("peer-never-paired")!;
            var refused = await Assert.ThrowsAsync<PeerSessionOperationException>(
                async () => await stranger.AcceptRemoteInterestAsync(interest, default));
            Assert.Equal(ProtocolErrorCode.Unauthorized, refused.Code);
            Assert.Equal(0, desktop.Interests.ActiveInterestCount);

            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                LiveOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            await PairAsync(phone, desktop, "pairing-live-4");
            using var trusted = desktop.SessionCoordinator.OpenLiveSession(phone.Identity.PeerId)!;
            var ack = await trusted.AcceptRemoteInterestAsync(interest, default);
            Assert.True(ack.Accepted);
            Assert.Equal(1, desktop.Interests.ActiveInterestCount);
            var foreign = await trusted.AcceptRemoteInterestAsync(
                new LiveInterest { SubjectPeerId = "someone-else", LeaseSeconds = 30 }, default);
            Assert.False(foreign.Accepted);

            await desktop.Store.RevokeTrustedPeerAsync(phone.Identity.PeerId, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<PeerSessionOperationException>(
                async () => await trusted.AcceptRemoteInterestAsync(interest, default));
            Assert.Equal(0, desktop.Interests.ActiveInterestCount);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    /// <summary>The started loop's first collection may still be running; retry until this run actually appends.</summary>
    private static async Task<ulong> CollectPhoneAsync(MobilePeerRuntime phone)
    {
        ulong last = 0;
        await WaitAsync(async () =>
        {
            var result = await phone.CollectOnceAsync();
            last = result.LastSequence ?? 0;
            return result.Status == MonitorApp.Peer.Collection.PeerCollectionRunStatus.Completed && last > 0;
        });
        return last;
    }

    private static async Task PairAsync(MobilePeerRuntime phone, DesktopPeer desktop, string pairingId)
    {
        var pairTask = phone.PairAsync(desktop.Invitation(), pairingId);
        await desktop.ApproveWhenPendingAsync(pairingId);
        await pairTask.WaitAsync(Deadline);
    }

    private static async Task PairWithNodeAsync(MobilePeerRuntime phone, PeerNodeRuntime node)
    {
        var admin = node.AdminAddress!;
        var invitation = new PeerPairingInvitation(
            IPAddress.Loopback, node.PeerPort, node.Identity.PeerId, node.Identity.PublicKeyFingerprint, null);
        var pairTask = phone.PairAsync(invitation, "pairing-live-node-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true });
        string? pairingId = null;
        using var timeout = new CancellationTokenSource(Deadline);
        while (pairingId is null)
        {
            var json = await client.GetStringAsync(new Uri(admin, "/api/pairings"), timeout.Token);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetArrayLength() > 0)
            {
                pairingId = document.RootElement[0].GetProperty("pairingId").GetString();
            }
            else
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        using var csrfDocument = JsonDocument.Parse(await client.GetStringAsync(new Uri(admin, "/api/csrf"), timeout.Token));
        using var approval = new HttpRequestMessage(
            HttpMethod.Post, new Uri(admin, $"/api/pairings/{Uri.EscapeDataString(pairingId)}/approve"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        approval.Headers.Add("X-CSRF-TOKEN", csrfDocument.RootElement.GetProperty("requestToken").GetString());
        using var response = await client.SendAsync(approval, timeout.Token);
        response.EnsureSuccessStatusCode();
        await pairTask.WaitAsync(Deadline);
    }

    /// <summary>Distinct collection runs observed on the node's own origin stream over the window.</summary>
    private static async Task<int> CountCollectionsAsync(PeerNodeRuntime node, TimeSpan window)
    {
        var observed = new ConcurrentDictionary<DateTimeOffset, byte>();
        var local = (await node.Store.ReadLocalIdentityAsync())!;
        using var subscription = node.Store.Live.Subscribe(record =>
        {
            if (record.OriginPeerId == local.PeerId)
            {
                observed.TryAdd(record.ObservedAt, 0);
            }
        });
        await Task.Delay(window);
        return observed.Count;
    }

    private static async Task<TimeSpan> MeasureAsync(CommitProbe source, CommitProbe destination, ulong sequence)
    {
        await WaitAsync(() => destination.Has(sequence));
        var committed = source.At(sequence) ?? throw new InvalidOperationException("Origin commit was not observed.");
        var arrived = destination.At(sequence)!.Value;
        var latency = Stopwatch.GetElapsedTime(committed, arrived);
        Assert.True(latency < TimeSpan.FromSeconds(10), $"Live delivery took {latency}.");
        return latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
    }

    private static async Task<bool> Converged(MobilePeerRuntime phone, DesktopPeer desktop)
    {
        var phoneLocal = (await phone.Store.ReadLocalIdentityAsync())!;
        var desktopLocal = (await desktop.Store.ReadLocalIdentityAsync())!;
        var phoneOnDesktop = await desktop.Store.ReadOriginRangeAsync(phoneLocal.PeerId, phoneLocal.OriginStreamId, 1, 10_000);
        var desktopOnPhone = await phone.Store.ReadOriginRangeAsync(desktopLocal.PeerId, desktopLocal.OriginStreamId, 1, 10_000);
        return phoneOnDesktop.Count == (int)(phoneLocal.NextSequence - 1) &&
            desktopOnPhone.Count == (int)(desktopLocal.NextSequence - 1);
    }

    private static async Task AssertSameHistoryAsync(PeerStore first, PeerStore second, string originPeerId)
    {
        var streamId = (await first.ReadLocalIdentityAsync())!.PeerId == originPeerId
            ? (await first.ReadLocalIdentityAsync())!.OriginStreamId
            : (await second.ReadLocalIdentityAsync())!.OriginStreamId;
        var left = await first.ReadOriginRangeAsync(originPeerId, streamId, 1, 10_000);
        var right = await second.ReadOriginRangeAsync(originPeerId, streamId, 1, 10_000);
        Assert.NotEmpty(left);
        Assert.Equal(left.Count, left.Select(item => item.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, left.Count).Select(value => (ulong)value), left.Select(item => item.Sequence));
        Assert.Equal(
            left.Select(item => (item.Sequence, Convert.ToHexString(item.ContentHash), Convert.ToHexString(item.Signature))),
            right.Select(item => (item.Sequence, Convert.ToHexString(item.ContentHash), Convert.ToHexString(item.Signature))));
    }

    private static async Task WaitAsync(Func<bool> condition) =>
        await WaitAsync(() => Task.FromResult(condition()));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!await condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Condition was not met before the deadline.");
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Timestamps when each origin sequence commits to one store, via the in-process live feed.</summary>
    private sealed class CommitProbe : IDisposable
    {
        private readonly ConcurrentDictionary<ulong, long> committed = new();
        private readonly IDisposable subscription;

        public CommitProbe(PeerStore store, string originPeerId)
        {
            subscription = store.Live.Subscribe(record =>
            {
                if (record.OriginPeerId == originPeerId)
                {
                    committed.TryAdd(record.Sequence, Stopwatch.GetTimestamp());
                }
            });
        }

        public bool Has(ulong sequence) => committed.ContainsKey(sequence);

        public long? At(ulong sequence) => committed.TryGetValue(sequence, out var value) ? value : null;

        public void Dispose() => subscription.Dispose();
    }
}
