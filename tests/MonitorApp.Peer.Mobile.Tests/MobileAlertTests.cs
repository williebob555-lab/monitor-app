using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Mobile;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>Desktop alert outbox to phone inbox over the real loopback PeerSession gRPC stream and real SQLite files.</summary>
public sealed class MobileAlertTests
{
    private static MobilePeerOptions Options(string dir) => new(
        dir, "test-phone", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private static async Task<(MobilePeerRuntime Phone, SoftwareIdentity Identity)> PairAsync(DesktopPeer desktop, string phoneRoot, string pairingId)
    {
        var identity = new SoftwareIdentity();
        var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
        var pairTask = phone.PairAsync(desktop.Invitation(), pairingId);
        await desktop.ApproveWhenPendingAsync(pairingId);
        await pairTask.WaitAsync(TimeSpan.FromSeconds(30));
        return (phone, identity);
    }

    private static Alert Raised(string owner, string subject = "peer:disk") => new(
        Alert.CreateId(owner, "threshold.disk_free", subject, DateTimeOffset.UtcNow), 1, "threshold.disk_free", subject, owner,
        AlertSeverity.Critical, AlertState.Raised, "Low free disk space", "5.0% free", DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task RaisedAlertIsPersistedOnThePhoneAcknowledgedAndThenResolvedOverLaterSessions()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            var (phone, identity) = await PairAsync(desktop, phoneRoot, "alert-pairing-1");
            using var _ = identity;
            await using var phoneRuntime = phone;
            var phoneId = phone.Identity.PeerId;
            var notified = new List<InboxAlert>();
            phone.NewAlerts += items => notified.AddRange(items);

            // First session subscribes the phone; nothing is pending yet.
            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);
            Assert.Empty(await phone.ReadAlertsAsync());

            // The desktop raises while no session exists: it is queued durably for the phone.
            var alert = Raised(desktop.Identity.PeerId);
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phoneId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            Assert.Null((await desktop.Alerts.ReadOutboxAsync(phoneId)).Single().AckedAt);

            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);
            var stored = Assert.Single(await phone.ReadAlertsAsync());
            Assert.Equal(alert.AlertId, stored.Alert.AlertId);
            Assert.Equal(desktop.Identity.PeerId, stored.Alert.OriginPeerId);
            Assert.Equal("Low free disk space", stored.Alert.Title);
            Assert.Equal(alert.AlertId, Assert.Single(notified).Alert.AlertId);

            // Ack after the durable write cleared the desktop outbox; a repeat session delivers nothing new.
            Assert.NotNull((await desktop.Alerts.ReadOutboxAsync(phoneId)).Single().AckedAt);
            Assert.Empty((await desktop.Alerts.ReadPendingAsync(phoneId, DateTimeOffset.UtcNow, 10)).Alerts);
            await phone.SyncOnceAsync();
            Assert.Equal(2, notified.Count); // nothing was marked notified, so it is offered again (never lost)
            Assert.Single(await phone.ReadAlertsAsync());

            // Marking notified suppresses re-notification; the resolve arrives as revision 2 on the same alert.
            await phone.MarkAlertNotifiedAsync(stored);
            Assert.Empty(await phone.ReadPendingNotificationsAsync());
            await phone.SyncOnceAsync();
            Assert.Equal(2, notified.Count);
            await desktop.Alerts.ResolveAsync(desktop.Identity.PeerId, "threshold.disk_free", "peer:disk",
                DateTimeOffset.UtcNow, [phoneId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            await phone.SyncOnceAsync();
            var resolved = Assert.Single(await phone.ReadAlertsAsync());
            Assert.Equal(AlertState.Resolved, resolved.Alert.State);
            Assert.Equal(2u, resolved.Alert.Revision);
            Assert.True(resolved.NeedsNotification);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task UnacknowledgedAlertSurvivesAPhoneRestartAndPendingNotificationsAreRaisedAgain()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            var (phone, identity) = await PairAsync(desktop, phoneRoot, "alert-pairing-2");
            using var _ = identity;
            var phoneId = phone.Identity.PeerId;
            await phone.SyncOnceAsync();
            await desktop.Alerts.RaiseAsync(Raised(desktop.Identity.PeerId), [phoneId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            await phone.SyncOnceAsync();
            await phone.DisposeAsync();

            // The process died before the notification was posted: a fresh runtime on the same files still owes it.
            await using var restarted = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            Assert.Single(await restarted.ReadPendingNotificationsAsync());
            var raisedAgain = new List<InboxAlert>();
            restarted.NewAlerts += items => raisedAgain.AddRange(items);
            await restarted.SyncOnceAsync();
            Assert.Single(raisedAgain);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task RevokedPhoneReceivesNothingAndAnotherPhonesAlertsAreNeverDelivered()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        var otherRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            var (phone, identity) = await PairAsync(desktop, phoneRoot, "alert-pairing-3");
            using var _ = identity;
            await using var phoneRuntime = phone;
            var (other, otherIdentity) = await PairAsync(desktop, otherRoot, "alert-pairing-4");
            using var __ = otherIdentity;
            await using var otherRuntime = other;
            await phone.SyncOnceAsync();
            await other.SyncOnceAsync();

            // Addressed to the other phone only.
            await desktop.Alerts.RaiseAsync(Raised(desktop.Identity.PeerId), [other.Identity.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            await phone.SyncOnceAsync();
            Assert.Empty(await phone.ReadAlertsAsync());
            await other.SyncOnceAsync();
            Assert.Single(await other.ReadAlertsAsync());

            // Addressed to the first phone, then trust is revoked before delivery.
            await desktop.Alerts.RaiseAsync(Raised(desktop.Identity.PeerId, "peer:cpu"), [phone.Identity.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            await desktop.Store.RevokeTrustedPeerAsync(phone.Identity.PeerId, DateTimeOffset.UtcNow);
            var refused = Assert.Single(await phone.SyncOnceAsync());
            Assert.Equal(MobileSyncState.Refused, refused.State);
            Assert.Empty(await phone.ReadAlertsAsync());
            Assert.Null((await desktop.Alerts.ReadOutboxAsync(phone.Identity.PeerId)).Single().AckedAt);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot, otherRoot);
        }
    }

    private static void Cleanup(params string[] roots)
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
