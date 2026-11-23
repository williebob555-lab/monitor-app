using System.Net;
using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Mobile;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Mobile.Tests;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Client.Mobile.Tests;

/// <summary>
/// The phone data source over a real <see cref="MobilePeerRuntime"/> and a real desktop peer on loopback gRPC and
/// file-backed SQLite: it must serve the phone's own and the replicated desktop data through the shared client contract.
/// </summary>
public sealed class InProcessPeerDataSourceTests
{
    // Generous: CI runners are slow on cold start. Tests wait on conditions, never fixed sleeps.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static MobilePeerOptions Options(string directory) => new(
        directory,
        "test-phone",
        CollectionInterval: TimeSpan.FromHours(1),
        SyncInterval: TimeSpan.FromMilliseconds(50),
        MaximumSyncBackoff: TimeSpan.FromMilliseconds(400),
        HeartbeatInterval: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task StateHistoryCatalogAndDevicesCoverThePhoneAndTheReplicatedDesktop()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            Assert.True(source.SupportsLiveUpdates);

            await PairAsync(phone, desktop, "pairing-source-1");
            await desktop.CollectAsync();
            await phone.CollectOnceAsync();
            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);

            var phoneId = phone.Identity.PeerId;
            var desktopId = desktop.Identity.PeerId;
            var runtime = await source.GetRuntimeAsync();
            Assert.Equal(phoneId, runtime.LocalPeer!.PeerId);
            Assert.Equal(1, runtime.TrustedPeerCount);
            var peer = Assert.Single(await source.GetPeersAsync());
            Assert.Equal(desktopId, peer.PeerId);
            Assert.Equal("Active", peer.TrustState);

            var state = await source.GetStateAsync();
            Assert.Contains(state.Points, point => point.OriginPeerId == desktopId && point.SignalId == "system.memory.total");
            Assert.Contains(state.Points, point => point.OriginPeerId == phoneId && point.SignalId == "android.battery.level");

            // Things on the phone: its own model, bridged from what it monitors and what it synced.
            var things = await source.GetThingsAsync();
            Assert.Contains(things!.Things, t => t.Name == "test-phone" && t.KindId == "device.phone" && t.ReadingSubjects.Contains("peer:" + phoneId));
            Assert.Contains(things.Things, t => t.ReadingSubjects.Contains("peer:" + desktopId));
            await source.AddThingAsync("Spare SSD\nin the desk drawer", "part");
            Assert.Contains((await source.GetThingsAsync())!.Things, t => t.Name == "Spare SSD" && t.Facts.Single().Evidence == "Entered");
            await Assert.ThrowsAsync<ApiValidationException>(() => source.AddThingAsync("  ", null));

            var catalog = await source.GetCatalogAsync();
            Assert.Contains(desktopId, catalog.OriginPeers);
            Assert.Contains(phoneId, catalog.OriginPeers);

            // Paging: a tiny page size must still return every reading, following the cursor.
            var now = DateTimeOffset.UtcNow;
            var query = new HistoryQuery(now.AddHours(-1), now.AddMinutes(1));
            var all = await source.GetHistoryAsync(query);
            Assert.False(all.MoreAvailable);
            var paged = await source.GetHistoryAsync(query, maxPages: 1000, pageSize: 3);
            Assert.True(paged.PagesFetched > 1);
            Assert.Equal(all.Points.Count, paged.Points.Count);
            Assert.Contains(paged.Points, point => point.OriginPeerId == desktopId);
            var capped = await source.GetHistoryAsync(query, maxPages: 1, pageSize: 3);
            Assert.True(capped.MoreAvailable);

            // Point-in-time reconstruction answers from the same store.
            var asOf = await source.GetStateAsync(asOf: DateTimeOffset.UtcNow, lookbackSeconds: 3600, signalId: "system.memory.total");
            Assert.All(asOf.Points, point => Assert.Equal("system.memory.total", point.SignalId));
            Assert.NotEmpty(asOf.Points);

            // Caller errors surface as the client's validation exception, not a store exception.
            await Assert.ThrowsAsync<ApiValidationException>(() => source.GetHistoryAsync(new HistoryQuery(now, now.AddDays(-1))));

            // The command-center home lists both devices from this source, online, with headline metrics.
            var devices = DevicesViewModel.Build(
                runtime.LocalPeer.PeerId, await source.GetPeersAsync(), state.Points);
            Assert.Equal(2, devices.Count);
            var self = devices.Single(device => device.PeerId == phoneId);
            Assert.Equal("This device", self.Name);
            Assert.True(self.IsOnline);
            Assert.Contains(self.Headline, metric => metric.Label == "Battery" && metric.Value == "80%");
            var remote = devices.Single(device => device.PeerId == desktopId);
            Assert.Equal("Paired peer", remote.Role);
            Assert.True(remote.IsOnline);
            Assert.Contains(remote.Headline, metric => metric.Label == "Memory");

            var model = new MainViewModel(source);
            await model.Devices.RefreshAsync();
            Assert.Null(model.Devices.ErrorMessage);
            Assert.Equal(2, model.Devices.Devices.Count);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task NewDesktopReadingsRaiseStateChangedPromptlyWhileTheRuntimeSyncs()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            await PairAsync(phone, desktop, "pairing-source-2");
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);

            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.StateChanged += (_, _) => changed.TrySetResult();
            await desktop.CollectAsync();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The pushed change is real: the new desktop reading is readable through the source.
            await WaitAsync(async () =>
                (await source.GetStateAsync()).Points.Any(point => point.OriginPeerId == desktop.Identity.PeerId));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task ViewingADeviceDeclaresInterestThatTheDesktopHonoursUntilDisposed()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            await PairAsync(phone, desktop, "pairing-source-3");
            phone.Start();
            await WaitAsync(() => phone.GetStatus().Peers is [{ State: MobileSyncState.Synced }]);

            var desktopId = desktop.Identity.PeerId;
            var normal = TimeSpan.FromSeconds(10);
            Assert.Equal(normal, desktop.Interests.EffectiveInterval(desktopId, normal));

            // The shared live view model declares viewing for every trusted peer while it runs, and withdraws on stop.
            var state = new StateViewModel(new NodeConnection(source));
            using var cancellation = new CancellationTokenSource();
            var live = state.RunLiveAsync(TimeSpan.FromHours(1), cancellation.Token);
            await WaitAsync(() => desktop.Interests.EffectiveInterval(desktopId, normal) < normal);
            Assert.True(desktop.Interests.HasActiveInterest(desktopId));
            await cancellation.CancelAsync();
            await live.WaitAsync(Deadline);
            await WaitAsync(() => desktop.Interests.EffectiveInterval(desktopId, normal) == normal);

            using var direct = ((IViewingSource)source).BeginViewing(desktopId);
            await WaitAsync(() => desktop.Interests.HasActiveInterest(desktopId));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task AlertsFromTheDesktopAreListedThroughTheAlertSource()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            await PairAsync(phone, desktop, "pairing-source-4");
            Assert.Empty(await source.GetAlertsAsync());

            var alert = new Alert(
                Alert.CreateId(desktop.Identity.PeerId, "threshold.disk_free", "peer:disk", DateTimeOffset.UtcNow), 1,
                "threshold.disk_free", "peer:disk", desktop.Identity.PeerId, AlertSeverity.Critical, AlertState.Raised,
                "Low free disk space", "5.0% free", DateTimeOffset.UtcNow, null);
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phone.Identity.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            await phone.SyncOnceAsync();

            var listed = Assert.Single(await source.GetAlertsAsync());
            Assert.Equal(alert.AlertId, listed.AlertId);
            Assert.Equal("Critical", listed.Severity);
            Assert.Equal("Raised", listed.State);

            var model = new AlertsViewModel(new NodeConnection(source));
            await model.RefreshAsync();
            var row = Assert.Single(model.Alerts);
            Assert.True(row.IsActive);
            Assert.Equal("1 active, 0 resolved.", model.Summary);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task PairingByTypedCodeAndPastedInvitationGoThroughThePhoneRuntimeAndBadInputIsExplained()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);

            await Assert.ThrowsAsync<ApiValidationException>(() => source.PairWithCodeAsync("not an address", "123"));
            await Assert.ThrowsAsync<ApiValidationException>(() => source.PairWithCodeAsync($"127.0.0.1:{desktop.Port}", " "));
            await Assert.ThrowsAsync<ApiValidationException>(() => source.PairWithInvitationAsync("garbage"));
            await Assert.ThrowsAsync<ApiValidationException>(() => source.ApprovePairingAsync("x"));
            var wrong = await Assert.ThrowsAsync<ApiValidationException>(
                () => source.PairWithCodeAsync($"127.0.0.1:{desktop.Port}", "00000-00000"));
            Assert.Contains("code", wrong.Message, StringComparison.OrdinalIgnoreCase);

            var code = desktop.Codes.Issue(desktop.Identity.PeerId, desktop.Identity.PublicKeyFingerprint, TimeSpan.FromMinutes(10)).Code;
            var viaCode = source.PairWithCodeAsync($"127.0.0.1:{desktop.Port}", code);
            await WaitForPendingAndApproveAsync(phone, desktop);
            Assert.Equal(desktop.Identity.PeerId, await viaCode.WaitAsync(Deadline));
            Assert.Equal("Active", Assert.Single(await source.GetPeersAsync()).TrustState);

            // Invitation path (a second phone identity pairs with the same desktop via the pasted text).
            var secondRoot = TestDirectories.Create();
            try
            {
                using var secondIdentity = new SoftwareIdentity();
                await using var second = await MobilePeerRuntime.CreateAsync(Options(secondRoot), secondIdentity, new ScriptedDeviceReader());
                using var secondSource = new InProcessPeerDataSource(second);
                var text = desktop.Invitation().Encode();
                var viaInvitation = secondSource.PairWithInvitationAsync(text);
                await WaitForPendingAndApproveAsync(second, desktop);
                Assert.Equal(desktop.Identity.PeerId, await viaInvitation.WaitAsync(Deadline));
            }
            finally
            {
                Cleanup(secondRoot);
            }

            // Revocation goes through the same source.
            await source.RevokePeerAsync(desktop.Identity.PeerId);
            Assert.Equal("Revoked", Assert.Single(await source.GetPeersAsync()).TrustState);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task HistoryFeedRecordsTheDesktopGoingOfflineAndBackAndHasNoUpdateEvents()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            Assert.IsAssignableFrom<IHistorySource>(source);
            var from = DateTimeOffset.UtcNow.AddMinutes(-1);

            await PairAsync(phone, desktop, "pairing-history-1");
            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);
            await desktop.StopHostAsync();
            Assert.Equal(MobileSyncState.Offline, Assert.Single(await phone.SyncOnceAsync()).State);
            await desktop.StartHostAsync();
            await WaitAsync(async () =>
            {
                await phone.SyncOnceAsync();
                var rows = await source.GetPeerStateHistoryAsync(from, DateTimeOffset.UtcNow.AddMinutes(1));
                return rows.Count(row => row.State == "Online") >= 2;
            });

            var history = await source.GetPeerStateHistoryAsync(from, DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.All(history, row => Assert.Equal(desktop.Identity.PeerId, row.PeerId));
            Assert.Equal(["Paired", "Online", "Offline", "Online"], history.Select(row => row.State));
            Assert.Equal(history.OrderBy(row => row.At).Select(row => row.At), history.Select(row => row.At));
            Assert.Empty(await source.GetUpdateHistoryAsync(from, DateTimeOffset.UtcNow));
            Assert.Empty(await source.GetPeerStateHistoryAsync(from.AddDays(-9), from.AddDays(-8)));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    private static async Task PairAsync(MobilePeerRuntime phone, DesktopPeer desktop, string pairingId)
    {
        var pairTask = phone.PairAsync(desktop.Invitation(), pairingId);
        await desktop.ApproveWhenPendingAsync(pairingId);
        await pairTask.WaitAsync(Deadline);
    }

    /// <summary>The source picks its own pairing id; the phone status exposes it while the request is in flight.</summary>
    private static async Task WaitForPendingAndApproveAsync(MobilePeerRuntime phone, DesktopPeer desktop)
    {
        string? id = null;
        await WaitAsync(() => (id = phone.GetStatus().PendingPairingId) is not null);
        await desktop.ApproveWhenPendingAsync(id!);
    }

    private static async Task WaitAsync(Func<bool> condition) => await WaitAsync(() => Task.FromResult(condition()));

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
}
