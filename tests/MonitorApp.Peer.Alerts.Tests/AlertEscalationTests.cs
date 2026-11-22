using Microsoft.Data.Sqlite;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

/// <summary>T051: operator acknowledgement stops escalation; a Critical alert unacknowledged for too long is re-delivered once.</summary>
public sealed class AlertEscalationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(15);

    private static Alert NewAlert(string subject, AlertSeverity severity = AlertSeverity.Critical) => new(
        Alert.CreateId("peer-owner", "rule.x", subject, T0), 1, "rule.x", subject, "peer-owner",
        severity, AlertState.Raised, "Title " + subject, "Detail", T0, null);

    private static string NewPath() => Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db");

    private static async Task<AlertStore> StoreAsync(string path)
    {
        var store = new AlertStore(AlertStoreOptions.Create(path));
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public void DefaultEscalationIsFifteenMinutesAndNegativeIsRejected()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), AlertRulesConfig.Default.EscalationAfter);
        Assert.Throws<ArgumentOutOfRangeException>(() => (AlertRulesConfig.Default with { EscalationAfter = TimeSpan.FromSeconds(-1) }).Validate());
    }

    [Fact]
    public async Task CriticalAlertEscalatesOnceAfterTheWindowAndSurvivesARestart()
    {
        var path = NewPath();
        var store = await StoreAsync(path);
        var alert = NewAlert("a");
        await store.RaiseAsync(alert, ["phone"], Ttl, T0);
        await store.AcknowledgeAsync("phone", [(alert.AlertId, 1u)], T0.AddSeconds(5));

        Assert.Empty(await store.EscalateDueAsync(T0.AddMinutes(14), After, ["phone"], Ttl));
        Assert.Empty((await store.ReadPendingAsync("phone", T0.AddMinutes(14), 10)).Alerts);

        var escalated = Assert.Single(await store.EscalateDueAsync(T0.AddMinutes(15), After, ["phone"], Ttl));
        Assert.True(escalated.Escalated);
        var pending = Assert.Single((await store.ReadPendingAsync("phone", T0.AddMinutes(15), 10)).Alerts);
        Assert.True(pending.Escalated);
        Assert.Equal(1u, pending.Revision);
        Assert.True(pending.ToContract().Escalated);

        // A restarted node does not escalate the same alert again, even after the escalation is delivered.
        await store.AcknowledgeEscalationAsync("phone", [(alert.AlertId, 1u)], T0.AddMinutes(16));
        var reopened = await StoreAsync(path);
        Assert.Empty(await reopened.EscalateDueAsync(T0.AddHours(3), After, ["phone"], Ttl));
        Assert.Empty((await reopened.ReadPendingAsync("phone", T0.AddHours(3), 10)).Alerts);
        Assert.True((await reopened.ReadEscalationsAsync()).ContainsKey(alert.AlertId));
        Assert.Equal(2, (await reopened.ReadOutboxAsync("phone")).Count);
    }

    [Fact]
    public async Task OnlyCriticalUnacknowledgedRaisedAlertsEscalateAndPreferencesAreHonoured()
    {
        var store = await StoreAsync(NewPath());
        var warning = NewAlert("warn", AlertSeverity.Warning);
        var acked = NewAlert("acked");
        var open = NewAlert("open");
        var resolved = NewAlert("resolved");
        foreach (var alert in new[] { warning, acked, open, resolved })
        {
            await store.RaiseAsync(alert, [], Ttl, T0);
        }

        await store.AcknowledgeByOperatorAsync(acked.AlertId, "phone-a", T0.AddMinutes(1));
        await store.ResolveAsync("peer-owner", "rule.x", "resolved", T0.AddMinutes(2), [], Ttl, T0.AddMinutes(2));
        await store.SetPreferencesAsync("quiet", AlertSeverity.Unspecified, ["rule.x"], T0);

        var due = await store.EscalateDueAsync(T0.AddMinutes(20), After, ["loud", "quiet"], Ttl);
        Assert.Equal(open.AlertId, Assert.Single(due).AlertId);
        Assert.Single((await store.ReadPendingAsync("loud", T0.AddMinutes(20), 10)).Alerts);
        Assert.Empty((await store.ReadPendingAsync("quiet", T0.AddMinutes(20), 10)).Alerts);
        Assert.Empty(await store.EscalateDueAsync(T0.AddMinutes(30), TimeSpan.Zero, ["loud"], Ttl));
    }

    [Fact]
    public async Task AcknowledgingOrResolvingDropsAnUndeliveredEscalation()
    {
        var store = await StoreAsync(NewPath());
        var first = NewAlert("first");
        var second = NewAlert("second");
        await store.RaiseAsync(first, ["phone"], Ttl, T0);
        await store.RaiseAsync(second, ["phone"], Ttl, T0);
        await store.AcknowledgeAsync("phone", [(first.AlertId, 1u), (second.AlertId, 1u)], T0.AddSeconds(1));
        await store.EscalateDueAsync(T0.AddMinutes(16), After, ["phone"], Ttl);
        Assert.Equal(2, (await store.ReadPendingAsync("phone", T0.AddMinutes(16), 10)).Alerts.Count(item => item.Escalated));

        Assert.True(await store.AcknowledgeByOperatorAsync(first.AlertId, "phone-b", T0.AddMinutes(17)));
        await store.ResolveAsync("peer-owner", "rule.x", "second", T0.AddMinutes(17), ["phone"], Ttl, T0.AddMinutes(17));

        var (pending, _) = await store.ReadPendingAsync("phone", T0.AddMinutes(18), 10);
        var only = Assert.Single(pending);
        Assert.Equal(second.AlertId, only.AlertId);
        Assert.Equal(AlertState.Resolved, only.State);
        Assert.False(only.Escalated);
    }

    [Fact]
    public async Task OperatorAcknowledgementKeepsTheFirstAcknowledger()
    {
        var store = await StoreAsync(NewPath());
        var alert = NewAlert("a");
        await store.RaiseAsync(alert, [], Ttl, T0);
        Assert.Null(await store.ReadOperatorAcknowledgementAsync(alert.AlertId));
        Assert.True(await store.AcknowledgeByOperatorAsync(alert.AlertId, "phone-a", T0.AddMinutes(1)));
        Assert.True(await store.AcknowledgeByOperatorAsync(alert.AlertId, "local", T0.AddMinutes(2)));
        Assert.False(await store.AcknowledgeByOperatorAsync("unknown", "local", T0));
        Assert.Equal(("phone-a", T0.AddMinutes(1)), await store.ReadOperatorAcknowledgementAsync(alert.AlertId));
    }

    [Fact]
    public async Task PhoneInboxRenotifiesOnceForAnEscalationAndKeepsTheFlag()
    {
        var store = await StoreAsync(NewPath());
        var alert = NewAlert("a");
        Assert.True(await store.StoreInboundAsync(alert, T0));
        await store.MarkNotifiedAsync(alert.AlertId, 1);
        Assert.Empty(await store.ReadInboxAsync(10, onlyUnnotified: true));

        Assert.False(await store.StoreInboundAsync(alert, T0.AddMinutes(1)));
        Assert.True(await store.StoreInboundAsync(alert with { Escalated = true }, T0.AddMinutes(16)));
        var stored = Assert.Single(await store.ReadInboxAsync(10, onlyUnnotified: true));
        Assert.True(stored.Alert.Escalated);
        await store.MarkNotifiedAsync(alert.AlertId, 1);
        Assert.False(await store.StoreInboundAsync(alert with { Escalated = true }, T0.AddMinutes(17)));
        Assert.Empty(await store.ReadInboxAsync(10, onlyUnnotified: true));
    }

    [Fact]
    public async Task SchemaV4MigratesToEscalationWithoutLosingQueuedDeliveries()
    {
        var path = NewPath();
        var store = await StoreAsync(path);
        var alert = NewAlert("a");
        await store.RaiseAsync(alert, ["phone"], Ttl, T0);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE alert_escalations;
                DROP TABLE alert_rule_overrides;
                ALTER TABLE alert_inbox DROP COLUMN escalated;
                CREATE TABLE alert_outbox_v4(
                    recipient_peer_id TEXT NOT NULL, alert_id TEXT NOT NULL, revision INTEGER NOT NULL,
                    enqueued_at TEXT NOT NULL, expires_at TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at TEXT NULL, acked_at TEXT NULL, PRIMARY KEY(recipient_peer_id, alert_id, revision));
                INSERT INTO alert_outbox_v4 SELECT recipient_peer_id, alert_id, revision, enqueued_at, expires_at, attempts, last_attempt_at, acked_at FROM alert_outbox;
                DROP TABLE alert_outbox;
                ALTER TABLE alert_outbox_v4 RENAME TO alert_outbox;
                UPDATE alert_schema_version SET version = 4;
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var migrated = await StoreAsync(path);
        Assert.Equal(alert.AlertId, Assert.Single((await migrated.ReadPendingAsync("phone", T0, 10)).Alerts).AlertId);
        Assert.False(Assert.Single(await migrated.ReadOutboxAsync("phone")).Escalation);
        Assert.Single(await migrated.EscalateDueAsync(T0.AddMinutes(20), After, ["phone"], Ttl));
        Assert.True(await migrated.StoreInboundAsync(alert, T0));
    }

    [Fact]
    public async Task EvaluatorEscalatesACriticalDiskAlertOnceOnAControllableClock()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        await using var phone = await TestPeer.CreateAsync(T0);
        await desktop.TrustAsync(phone.PublicIdentity, T0);
        await desktop.Alerts.RegisterRecipientAsync(phone.PublicIdentity.PeerId, T0, Ttl);
        var config = AlertRulesConfig.Default with
        {
            RefireCooldown = TimeSpan.Zero,
            EscalationAfter = TimeSpan.FromMinutes(10),
            Thresholds = [AlertRulesConfig.Default.Thresholds[0] with { Sustained = TimeSpan.Zero }],
        };
        var evaluator = desktop.Evaluator(config);
        await desktop.CollectAsync(T0, freeBytes: 5);
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Escalated);

        await desktop.CollectAsync(T0.AddMinutes(9), freeBytes: 5);
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Escalated);
        await desktop.CollectAsync(T0.AddMinutes(11), freeBytes: 5);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Escalated);
        await desktop.CollectAsync(T0.AddMinutes(40), freeBytes: 5);
        Assert.Equal(0, (await desktop.Evaluator(config).EvaluateOnceAsync()).Escalated);
        Assert.Contains(await desktop.Alerts.ReadOutboxAsync(phone.PublicIdentity.PeerId), entry => entry.Escalation);
    }
}
