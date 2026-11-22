using Microsoft.Data.Sqlite;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

public sealed class AlertStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static Alert NewAlert(string subject = "s1", DateTimeOffset? at = null) => new(
        Alert.CreateId("peer-owner", "rule.x", subject, at ?? T0), 1, "rule.x", subject, "peer-owner",
        AlertSeverity.Warning, AlertState.Raised, "Title", "Detail", at ?? T0, null);

    private static async Task<AlertStore> StoreAsync(string path)
    {
        var store = new AlertStore(AlertStoreOptions.Create(path));
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task OutboxSurvivesRestartAndIsClearedOnlyByAcknowledgement()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db");
        var alert = NewAlert();
        var first = await StoreAsync(path);
        await first.RegisterRecipientAsync("phone-1", T0, Ttl);
        Assert.True(await first.RaiseAsync(alert, ["phone-1"], Ttl, T0));

        var reopened = await StoreAsync(path);
        var (pending, hasMore) = await reopened.ReadPendingAsync("phone-1", T0.AddMinutes(1), 10);
        Assert.Equal(alert.AlertId, Assert.Single(pending).AlertId);
        Assert.False(hasMore);
        await reopened.RecordAttemptsAsync("phone-1", [alert.AlertId], T0.AddMinutes(1));
        await reopened.RecordAttemptsAsync("phone-1", [alert.AlertId], T0.AddMinutes(2));

        var third = await StoreAsync(path);
        Assert.Equal(2, Assert.Single(await third.ReadOutboxAsync("phone-1")).Attempts);
        Assert.Single((await third.ReadPendingAsync("phone-1", T0.AddMinutes(3), 10)).Alerts);
        Assert.Equal(1, await third.AcknowledgeAsync("phone-1", [(alert.AlertId, 1u)], T0.AddMinutes(3)));
        var fourth = await StoreAsync(path);
        Assert.Empty((await fourth.ReadPendingAsync("phone-1", T0.AddMinutes(4), 10)).Alerts);
    }

    [Fact]
    public async Task ExpiredOutboxRowsAreNotDeliveredButTheAlertEventIsRetained()
    {
        var store = await StoreAsync(Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db"));
        var alert = NewAlert();
        await store.RaiseAsync(alert, ["phone-1"], TimeSpan.FromHours(1), T0);
        Assert.Empty((await store.ReadPendingAsync("phone-1", T0.AddHours(2), 10)).Alerts);
        Assert.Equal(1, await store.PurgeOutboxAsync(T0.AddHours(3)));
        Assert.Equal(alert.AlertId, (await store.ReadAsync(alert.AlertId))!.AlertId);
    }

    [Fact]
    public async Task EachRecipientSeesOnlyItsOwnRowsAndAcknowledgementsCannotClearAnotherRecipient()
    {
        var store = await StoreAsync(Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db"));
        var forA = NewAlert("a");
        await store.RaiseAsync(forA, ["phone-a"], Ttl, T0);
        var forB = NewAlert("b", T0.AddMinutes(1));
        await store.RaiseAsync(forB, ["phone-b"], Ttl, T0);

        Assert.Equal(forA.AlertId, Assert.Single((await store.ReadPendingAsync("phone-a", T0, 10)).Alerts).AlertId);
        Assert.Equal(forB.AlertId, Assert.Single((await store.ReadPendingAsync("phone-b", T0, 10)).Alerts).AlertId);
        Assert.Equal(0, await store.AcknowledgeAsync("phone-a", [(forB.AlertId, 1u)], T0));
        Assert.Single((await store.ReadPendingAsync("phone-b", T0, 10)).Alerts);
        Assert.Equal(0, await store.AcknowledgeAsync("phone-a", [(forA.AlertId, 9u)], T0));
        Assert.Single((await store.ReadPendingAsync("phone-a", T0, 10)).Alerts);
    }

    [Fact]
    public async Task ResolvedAlertIsRedeliveredAtRevisionTwoAndDuplicateRaiseIsRefused()
    {
        var store = await StoreAsync(Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db"));
        var alert = NewAlert();
        Assert.True(await store.RaiseAsync(alert, ["p"], Ttl, T0));
        Assert.False(await store.RaiseAsync(alert, ["p"], Ttl, T0));
        Assert.False(await store.RaiseAsync(alert with { AlertId = "other-id" }, ["p"], Ttl, T0));
        await store.AcknowledgeAsync("p", [(alert.AlertId, 1u)], T0);
        Assert.Empty((await store.ReadPendingAsync("p", T0, 10)).Alerts);

        await store.ResolveAsync("peer-owner", "rule.x", "s1", T0.AddMinutes(5), ["p"], Ttl, T0.AddMinutes(5));
        var pending = Assert.Single((await store.ReadPendingAsync("p", T0.AddMinutes(5), 10)).Alerts);
        Assert.Equal(2u, pending.Revision);
        Assert.Equal(AlertState.Resolved, pending.State);
        Assert.Null(await store.ResolveAsync("peer-owner", "rule.x", "s1", T0.AddMinutes(6), ["p"], Ttl, T0.AddMinutes(6)));
    }

    [Fact]
    public async Task NewRecipientIsQueuedEveryActiveAlertExactlyOnce()
    {
        var store = await StoreAsync(Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db"));
        await store.RaiseAsync(NewAlert("a"), [], Ttl, T0);
        Assert.True(await store.RegisterRecipientAsync("phone", T0, Ttl));
        Assert.False(await store.RegisterRecipientAsync("phone", T0, Ttl));
        Assert.Single(await store.ReadOutboxAsync("phone"));
    }

    [Fact]
    public async Task InboxUpsertIsIdempotentAndNeverRegressesRevision()
    {
        var store = await StoreAsync(Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db"));
        var raised = NewAlert();
        var resolved = raised with { Revision = 2, State = AlertState.Resolved, ResolvedAt = T0.AddMinutes(1) };
        Assert.True(await store.StoreInboundAsync(raised, T0));
        Assert.False(await store.StoreInboundAsync(raised, T0));
        Assert.Single(await store.ReadInboxAsync(10, onlyUnnotified: true));
        await store.MarkNotifiedAsync(raised.AlertId, 1);
        Assert.Empty(await store.ReadInboxAsync(10, onlyUnnotified: true));
        Assert.True(await store.StoreInboundAsync(resolved, T0.AddMinutes(1)));
        Assert.False(await store.StoreInboundAsync(raised, T0.AddMinutes(2)));
        var stored = Assert.Single(await store.ReadInboxAsync(10));
        Assert.Equal(2u, stored.Alert.Revision);
        Assert.True(stored.NeedsNotification);
    }

    [Fact]
    public async Task SchemaVersionIsTrackedSeparatelyAndNewerSchemasAreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db");
        var store = await StoreAsync(path);
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version FROM alert_schema_version;";
            Assert.Equal(6L, await command.ExecuteScalarAsync());
            command.CommandText = "UPDATE alert_schema_version SET version = 99;";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AlertStore(AlertStoreOptions.Create(path)).InitializeAsync());
    }

    [Fact]
    public async Task SchemaV3MigratesToPreferencesWithoutLosingData()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db");
        var store = await StoreAsync(path);
        await store.InitializeAsync();
        await store.RegisterRecipientAsync("phone", T0, TimeSpan.FromDays(1));
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE alert_preferences;
                DROP TABLE alert_escalations;
                DROP TABLE alert_rule_overrides;
                ALTER TABLE alert_inbox DROP COLUMN escalated;
                CREATE TABLE alert_outbox_v4(
                    recipient_peer_id TEXT NOT NULL, alert_id TEXT NOT NULL, revision INTEGER NOT NULL,
                    enqueued_at TEXT NOT NULL, expires_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at TEXT NULL, acked_at TEXT NULL, PRIMARY KEY(recipient_peer_id, alert_id, revision));
                DROP TABLE alert_outbox;
                ALTER TABLE alert_outbox_v4 RENAME TO alert_outbox;
                UPDATE alert_schema_version SET version = 3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new AlertStore(AlertStoreOptions.Create(path));
        await reopened.InitializeAsync();
        Assert.Equal("phone", Assert.Single(await reopened.ReadRecipientsAsync()));
        Assert.Empty(await reopened.ReadPreferencesAsync());
        await reopened.SetPreferencesAsync("phone", AlertSeverity.Warning, ["b", "a", "a", " "], T0);
        var prefs = Assert.Single(await reopened.ReadPreferencesAsync());
        Assert.Equal(AlertSeverity.Warning, prefs.MinimumSeverity);
        Assert.Equal(["a", "b"], prefs.MutedRuleKinds);
    }

    [Fact]
    public async Task DeliveryRefusesUntrustedAndRevokedPeersAndWakesLiveSessionsOnRaise()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        await using var phone = await TestPeer.CreateAsync(T0);
        await using var stranger = await TestPeer.CreateAsync(T0);
        var delivery = new AlertDeliveryService(desktop.Store, desktop.Alerts, AlertRulesConfig.Default, desktop.Clock);
        var pull = new AlertPull { RequestId = "r", MaximumCount = 10 };

        await Assert.ThrowsAsync<AlertDeliveryUnauthorizedException>(() => delivery.PullAsync(stranger.PublicIdentity.PeerId, pull));
        await desktop.TrustAsync(phone.PublicIdentity, T0);
        Assert.Empty((await delivery.PullAsync(phone.PublicIdentity.PeerId, pull)).Alerts);

        var waiting = delivery.WaitForPushAsync(phone.PublicIdentity.PeerId, TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await desktop.Alerts.RaiseAsync(NewAlert(), [phone.PublicIdentity.PeerId], Ttl, T0);
        var pushed = await waiting;
        Assert.Single(pushed!.Alerts);
        Assert.Equal(string.Empty, pushed.RequestId);

        var ack = new AlertAck();
        ack.Entries.Add(new AlertAckEntry { AlertId = pushed.Alerts[0].AlertId, Revision = 1 });
        await delivery.AcknowledgeAsync(phone.PublicIdentity.PeerId, ack);
        Assert.Null(await delivery.WaitForPushAsync(phone.PublicIdentity.PeerId, TimeSpan.FromMilliseconds(200)));

        await desktop.Store.RevokeTrustedPeerAsync(phone.PublicIdentity.PeerId, T0);
        await Assert.ThrowsAsync<AlertDeliveryUnauthorizedException>(() => delivery.PullAsync(phone.PublicIdentity.PeerId, pull));
        await Assert.ThrowsAsync<AlertDeliveryUnauthorizedException>(() => delivery.AcknowledgeAsync(phone.PublicIdentity.PeerId, ack));
    }
}
