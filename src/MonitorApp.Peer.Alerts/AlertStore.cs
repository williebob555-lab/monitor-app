using System.Globalization;
using Microsoft.Data.Sqlite;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Alerts;

public sealed record AlertStoreOptions(string DatabasePath, TimeSpan BusyTimeout)
{
    public static AlertStoreOptions Create(string databasePath) => new(databasePath, TimeSpan.FromSeconds(5));
}

/// <summary>
/// Durable alert state in the peer database, in its own tables with its own <c>alert_schema_version</c> so it
/// never shares PeerStore's migration chain. Desktop side: alert episodes (retained as events), recipients, the
/// per-recipient outbox, and peer last-seen. Phone side: the inbox with notification bookkeeping.
/// </summary>
public sealed class AlertStore
{
    private const int SchemaVersion = 4;
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    /// <summary>Raised in-process after outbox rows for these recipients are committed (wakes live-session pushes).</summary>
    public event Action<IReadOnlyCollection<string>>? OutboxEnqueued;

    public AlertStore(AlertStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        busyTimeoutMilliseconds = checked((int)options.BusyTimeout.TotalMilliseconds);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "CREATE TABLE IF NOT EXISTS alert_schema_version(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1), version INTEGER NOT NULL);",
            [], cancellationToken).ConfigureAwait(false);
        var current = 0L;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version FROM alert_schema_version WHERE singleton_id = 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                current = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (current > SchemaVersion)
        {
            throw new InvalidOperationException("The alert schema is newer than this application supports.");
        }

        if (current < 1)
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE alerts(
                    alert_id TEXT PRIMARY KEY,
                    revision INTEGER NOT NULL,
                    rule_id TEXT NOT NULL,
                    subject_id TEXT NOT NULL,
                    origin_peer_id TEXT NOT NULL,
                    severity INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    raised_at TEXT NOT NULL,
                    resolved_at TEXT NULL);
                CREATE UNIQUE INDEX alerts_one_active_episode ON alerts(origin_peer_id, rule_id, subject_id) WHERE state = 1;
                CREATE INDEX alerts_recent ON alerts(state, raised_at);
                CREATE TABLE alert_recipients(peer_id TEXT PRIMARY KEY, registered_at TEXT NOT NULL);
                CREATE TABLE alert_outbox(
                    recipient_peer_id TEXT NOT NULL,
                    alert_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    enqueued_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at TEXT NULL,
                    acked_at TEXT NULL,
                    PRIMARY KEY(recipient_peer_id, alert_id, revision));
                CREATE TABLE alert_peer_last_seen(peer_id TEXT PRIMARY KEY, seen_at TEXT NOT NULL);
                CREATE TABLE alert_inbox(
                    alert_id TEXT PRIMARY KEY,
                    revision INTEGER NOT NULL,
                    rule_id TEXT NOT NULL,
                    subject_id TEXT NOT NULL,
                    origin_peer_id TEXT NOT NULL,
                    severity INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    raised_at TEXT NOT NULL,
                    resolved_at TEXT NULL,
                    received_at TEXT NOT NULL,
                    notified_revision INTEGER NOT NULL DEFAULT 0);
                INSERT INTO alert_schema_version(singleton_id, version) VALUES (1, 1);
                """, [], cancellationToken).ConfigureAwait(false);
        }

        if (current < 2)
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE alert_mutes(
                    scope TEXT PRIMARY KEY,
                    muted_until TEXT NOT NULL,
                    muted_by TEXT NOT NULL,
                    muted_at TEXT NOT NULL);
                CREATE TABLE alert_operator_acks(
                    alert_id TEXT PRIMARY KEY,
                    acknowledged_by TEXT NOT NULL,
                    acknowledged_at TEXT NOT NULL);
                UPDATE alert_schema_version SET version = 2 WHERE singleton_id = 1;
                """, [], cancellationToken).ConfigureAwait(false);
        }

        if (current < 3)
        {
            // T047: mutes are per recipient. Existing (global) mutes become the issuing phone's own mute.
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE alert_mutes_v3(
                    recipient_peer_id TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    muted_until TEXT NOT NULL,
                    muted_at TEXT NOT NULL,
                    PRIMARY KEY(recipient_peer_id, scope));
                INSERT INTO alert_mutes_v3(recipient_peer_id, scope, muted_until, muted_at)
                    SELECT muted_by, scope, muted_until, muted_at FROM alert_mutes;
                DROP TABLE alert_mutes;
                ALTER TABLE alert_mutes_v3 RENAME TO alert_mutes;
                UPDATE alert_schema_version SET version = 3 WHERE singleton_id = 1;
                """, [], cancellationToken).ConfigureAwait(false);
        }

        if (current < 4)
        {
            // T049: durable per-recipient delivery preferences (minimum severity, opted-out rule kinds).
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE alert_preferences(
                    recipient_peer_id TEXT PRIMARY KEY,
                    min_severity INTEGER NOT NULL,
                    muted_rule_kinds TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                UPDATE alert_schema_version SET version = 4 WHERE singleton_id = 1;
                """, [], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a new episode and queues it for every recipient in one transaction. Returns false (and changes
    /// nothing) when the owner already has an active episode for the rule and subject, or the id already exists.
    /// </summary>
    public async Task<bool> RaiseAsync(
        Alert alert,
        IReadOnlyCollection<string> recipients,
        TimeSpan outboxTtl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(recipients);
        if (alert.State != AlertState.Raised || alert.Revision != 1)
        {
            throw new ArgumentException("A new episode must be raised at revision 1.", nameof(alert));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = await ExecuteAsync(connection, transaction, """
            INSERT OR IGNORE INTO alerts(alert_id, revision, rule_id, subject_id, origin_peer_id, severity, state, title, detail, raised_at, resolved_at)
            SELECT $id, 1, $rule, $subject, $origin, $severity, 1, $title, $detail, $raised, NULL
            WHERE NOT EXISTS (SELECT 1 FROM alerts WHERE origin_peer_id = $origin AND rule_id = $rule AND subject_id = $subject AND state = 1);
            """,
            [
                ("$id", alert.AlertId), ("$rule", alert.RuleId), ("$subject", alert.SubjectId),
                ("$origin", alert.OriginPeerId), ("$severity", (int)alert.Severity), ("$title", alert.Title),
                ("$detail", alert.Detail), ("$raised", Format(alert.RaisedAt)),
            ], cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            return false;
        }

        foreach (var recipient in recipients)
        {
            if (await WantsAsync(connection, transaction, recipient, alert, cancellationToken).ConfigureAwait(false))
            {
                await EnqueueAsync(connection, transaction, recipient, alert.AlertId, 1, now, outboxTtl, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        OutboxEnqueued?.Invoke(recipients);
        return true;
    }

    /// <summary>Resolves the active episode (revision 2) and queues the resolution. Null when nothing was active.</summary>
    public async Task<Alert?> ResolveAsync(
        string originPeerId,
        string ruleId,
        string subjectId,
        DateTimeOffset resolvedAt,
        IReadOnlyCollection<string> recipients,
        TimeSpan outboxTtl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, transaction, """
            UPDATE alerts SET state = 2, revision = 2, resolved_at = MAX($resolved, raised_at)
            WHERE origin_peer_id = $origin AND rule_id = $rule AND subject_id = $subject AND state = 1;
            """,
            [("$resolved", Format(resolvedAt)), ("$origin", originPeerId), ("$rule", ruleId), ("$subject", subjectId)],
            cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            return null;
        }

        Alert resolved;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = AlertSelect + " WHERE origin_peer_id = $origin AND rule_id = $rule AND subject_id = $subject AND state = 2 ORDER BY resolved_at DESC LIMIT 1;";
            command.Parameters.AddWithValue("$origin", originPeerId);
            command.Parameters.AddWithValue("$rule", ruleId);
            command.Parameters.AddWithValue("$subject", subjectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            resolved = ReadAlert(reader);
        }

        foreach (var recipient in recipients)
        {
            if (await WantsAsync(connection, transaction, recipient, resolved, cancellationToken).ConfigureAwait(false))
            {
                await EnqueueAsync(connection, transaction, recipient, resolved.AlertId, 2, now, outboxTtl, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        OutboxEnqueued?.Invoke(recipients);
        return resolved;
    }

    public async Task<IReadOnlyList<Alert>> ReadActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAlertsAsync(connection, AlertSelect + " WHERE state = 1 ORDER BY raised_at DESC, alert_id;",
            [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Active alerts first, then alerts resolved since <paramref name="resolvedSince"/>; bounded by limit.</summary>
    public async Task<IReadOnlyList<Alert>> ReadActiveAndRecentAsync(
        DateTimeOffset resolvedSince,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAlertsAsync(connection,
            AlertSelect + " WHERE state = 1 OR resolved_at >= $since ORDER BY state ASC, raised_at DESC, alert_id LIMIT $limit;",
            [("$since", Format(resolvedSince)), ("$limit", limit)], cancellationToken).ConfigureAwait(false);
    }

    public async Task<Alert?> ReadAsync(string alertId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await QueryAlertsAsync(connection, AlertSelect + " WHERE alert_id = $id;", [("$id", alertId)],
            cancellationToken).ConfigureAwait(false);
        return found.SingleOrDefault();
    }

    /// <summary>
    /// Subscribes a recipient (idempotent). A new recipient is queued every currently active alert so a phone that
    /// pairs after an incident still learns of it.
    /// </summary>
    public async Task<bool> RegisterRecipientAsync(
        string peerId,
        DateTimeOffset now,
        TimeSpan outboxTtl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO alert_recipients(peer_id, registered_at) VALUES ($peer, $at);",
            [("$peer", peerId), ("$at", Format(now))], cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            return false;
        }

        var active = await QueryAlertsAsync(connection, AlertSelect + " WHERE state = 1;", [], cancellationToken,
            transaction).ConfigureAwait(false);
        foreach (var alert in active)
        {
            if (await WantsAsync(connection, transaction, peerId, alert, cancellationToken).ConfigureAwait(false))
            {
                await EnqueueAsync(connection, transaction, peerId, alert.AlertId, alert.Revision, now, outboxTtl, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        OutboxEnqueued?.Invoke([peerId]);
        return true;
    }

    public async Task<IReadOnlyList<string>> ReadRecipientsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id FROM alert_recipients ORDER BY peer_id;";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// Latest state of every alert with an unacknowledged, unexpired outbox row for the recipient. Only rows
    /// addressed to this recipient are considered.
    /// </summary>
    public async Task<(IReadOnlyList<Alert> Alerts, bool HasMore)> ReadPendingAsync(
        string recipientPeerId,
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPeerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var alerts = await QueryAlertsAsync(connection, AlertSelect + """
             WHERE EXISTS (SELECT 1 FROM alert_outbox o WHERE o.recipient_peer_id = $recipient AND o.alert_id = alerts.alert_id
                AND o.acked_at IS NULL AND o.expires_at > $now)
                AND NOT EXISTS (SELECT 1 FROM alert_mutes m WHERE m.recipient_peer_id = $recipient AND m.muted_until > $now AND (m.scope = '*' OR m.scope = alerts.rule_id))
             ORDER BY raised_at, alert_id LIMIT $limit;
            """,
            [("$recipient", recipientPeerId), ("$now", Format(now)), ("$limit", maximum + 1)], cancellationToken)
            .ConfigureAwait(false);
        return (alerts.Take(maximum).ToArray(), alerts.Count > maximum);
    }

    /// <summary>
    /// Mutes delivery of alerts to one recipient for a rule (or every rule with scope "*") until <paramref name="until"/>.
    /// Other recipients are unaffected. Muted alerts stay queued in the outbox (until their own expiry) and are delivered
    /// after the mute is lifted or lapses.
    /// </summary>
    public async Task MuteAsync(
        string recipientPeerId, string scope, DateTimeOffset until, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            """
            INSERT INTO alert_mutes(recipient_peer_id, scope, muted_until, muted_at) VALUES ($recipient, $scope, $until, $at)
            ON CONFLICT(recipient_peer_id, scope) DO UPDATE SET muted_until = excluded.muted_until, muted_at = excluded.muted_at;
            """,
            [("$recipient", recipientPeerId), ("$scope", scope), ("$until", Format(until)), ("$at", Format(now))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lifts one recipient's mute and wakes that recipient's live session so queued alerts are delivered promptly. True when one existed.</summary>
    public async Task<bool> UnmuteAsync(string recipientPeerId, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteAsync(connection, null,
            "DELETE FROM alert_mutes WHERE recipient_peer_id = $recipient AND scope = $scope;",
            [("$recipient", recipientPeerId), ("$scope", scope)], cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            OutboxEnqueued?.Invoke([recipientPeerId]);
        }

        return removed > 0;
    }

    /// <summary>Mutes still in force at <paramref name="now"/> as (recipient, scope, until).</summary>
    public async Task<IReadOnlyList<(string Recipient, string Scope, DateTimeOffset Until)>> ReadActiveMutesAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT recipient_peer_id, scope, muted_until FROM alert_mutes WHERE muted_until > $now ORDER BY recipient_peer_id, scope;";
        command.Parameters.AddWithValue("$now", Format(now));
        var result = new List<(string, string, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2))));
        }

        return result;
    }

    /// <summary>
    /// Replaces one recipient's delivery preferences. Only alerts raised or resolved afterwards are filtered; rows already
    /// queued are unaffected. Filtered alerts are still recorded as events, they are just not queued for this recipient.
    /// </summary>
    public async Task SetPreferencesAsync(
        string recipientPeerId,
        AlertSeverity minimumSeverity,
        IReadOnlyCollection<string> mutedRuleKinds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPeerId);
        ArgumentNullException.ThrowIfNull(mutedRuleKinds);
        if (minimumSeverity is < AlertSeverity.Unspecified or > AlertSeverity.Critical)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSeverity));
        }

        var kinds = mutedRuleKinds.Select(kind => kind?.Trim() ?? string.Empty).Where(kind => kind.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            """
            INSERT INTO alert_preferences(recipient_peer_id, min_severity, muted_rule_kinds, updated_at) VALUES ($recipient, $min, $kinds, $at)
            ON CONFLICT(recipient_peer_id) DO UPDATE SET min_severity = excluded.min_severity, muted_rule_kinds = excluded.muted_rule_kinds, updated_at = excluded.updated_at;
            """,
            [("$recipient", recipientPeerId), ("$min", (int)minimumSeverity), ("$kinds", string.Join('\n', kinds)), ("$at", Format(now))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stored preferences (recipients without a row use the default: everything).</summary>
    public async Task<IReadOnlyList<AlertPreferences>> ReadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT recipient_peer_id, min_severity, muted_rule_kinds, updated_at FROM alert_preferences ORDER BY recipient_peer_id;";
        var result = new List<AlertPreferences>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AlertPreferences(
                reader.GetString(0), (AlertSeverity)reader.GetInt32(1), SplitKinds(reader.GetString(2)), Parse(reader.GetString(3))));
        }

        return result;
    }

    private static string[] SplitKinds(string text) => text.Length == 0 ? [] : text.Split('\n');

    private static async Task<bool> WantsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string recipient, Alert alert, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT min_severity, muted_rule_kinds FROM alert_preferences WHERE recipient_peer_id = $r;";
        command.Parameters.AddWithValue("$r", recipient);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return (int)alert.Severity >= reader.GetInt32(0) && !SplitKinds(reader.GetString(1)).Contains(alert.RuleId, StringComparer.Ordinal);
    }

    /// <summary>Records an operator acknowledgement of a known alert. False when the alert does not exist; idempotent.</summary>
    public async Task<bool> AcknowledgeByOperatorAsync(
        string alertId, string acknowledgedBy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgedBy);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM alerts WHERE alert_id = $id;";
        exists.Parameters.AddWithValue("$id", alertId);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO alert_operator_acks(alert_id, acknowledged_by, acknowledged_at) VALUES ($id, $by, $at);",
            [("$id", alertId), ("$by", acknowledgedBy), ("$at", Format(now))], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Operator acknowledgements as (alert id, acknowledged by, acknowledged at).</summary>
    public async Task<IReadOnlyList<(string AlertId, string By, DateTimeOffset At)>> ReadOperatorAcknowledgementsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT alert_id, acknowledged_by, acknowledged_at FROM alert_operator_acks ORDER BY acknowledged_at, alert_id;";
        var result = new List<(string, string, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2))));
        }

        return result;
    }

    public async Task RecordAttemptsAsync(
        string recipientPeerId,
        IEnumerable<string> alertIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var id in alertIds)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE alert_outbox SET attempts = attempts + 1, last_attempt_at = $now WHERE recipient_peer_id = $r AND alert_id = $id AND acked_at IS NULL;",
                [("$now", Format(now)), ("$r", recipientPeerId), ("$id", id)], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the recipient's outbox rows up to the acknowledged revision as delivered. Acknowledging a revision the
    /// owner never issued is ignored, so a recipient cannot clear rows for alerts it has not seen.
    /// </summary>
    public async Task<int> AcknowledgeAsync(
        string recipientPeerId,
        IEnumerable<(string AlertId, uint Revision)> entries,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var total = 0;
        foreach (var (alertId, revision) in entries)
        {
            total += await ExecuteAsync(connection, transaction, """
                UPDATE alert_outbox SET acked_at = $now
                WHERE recipient_peer_id = $r AND alert_id = $id AND revision <= $rev AND acked_at IS NULL
                  AND $rev <= (SELECT revision FROM alerts WHERE alert_id = $id);
                """,
                [("$now", Format(now)), ("$r", recipientPeerId), ("$id", alertId), ("$rev", (long)revision)],
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    public async Task<IReadOnlyList<OutboxEntry>> ReadOutboxAsync(
        string recipientPeerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recipient_peer_id, alert_id, revision, enqueued_at, expires_at, attempts, last_attempt_at, acked_at
            FROM alert_outbox WHERE recipient_peer_id = $r ORDER BY enqueued_at, alert_id, revision;
            """;
        command.Parameters.AddWithValue("$r", recipientPeerId);
        var result = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OutboxEntry(
                reader.GetString(0), reader.GetString(1), checked((uint)reader.GetInt64(2)), Parse(reader.GetString(3)),
                Parse(reader.GetString(4)), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7))));
        }

        return result;
    }

    /// <summary>Delivery bookkeeping for the given alerts across all recipients (empty when none were queued or all were purged).</summary>
    public async Task<IReadOnlyList<OutboxEntry>> ReadOutboxForAlertsAsync(
        IReadOnlyCollection<string> alertIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertIds);
        if (alertIds.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var names = alertIds.Select((_, index) => "$a" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        command.CommandText = $"""
            SELECT recipient_peer_id, alert_id, revision, enqueued_at, expires_at, attempts, last_attempt_at, acked_at
            FROM alert_outbox WHERE alert_id IN ({string.Join(',', names)}) ORDER BY alert_id, recipient_peer_id, revision;
            """;
        var position = 0;
        foreach (var id in alertIds)
        {
            command.Parameters.AddWithValue(names[position++], id);
        }

        var result = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OutboxEntry(
                reader.GetString(0), reader.GetString(1), checked((uint)reader.GetInt64(2)), Parse(reader.GetString(3)),
                Parse(reader.GetString(4)), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7))));
        }

        return result;
    }

    /// <summary>When the owner's most recent episode for the rule and subject resolved, or null if none has.</summary>
    public async Task<DateTimeOffset?> ReadLastResolvedAtAsync(
        string originPeerId,
        string ruleId,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(resolved_at) FROM alerts
            WHERE origin_peer_id = $origin AND rule_id = $rule AND subject_id = $subject AND state = 2;
            """;
        command.Parameters.AddWithValue("$origin", originPeerId);
        command.Parameters.AddWithValue("$rule", ruleId);
        command.Parameters.AddWithValue("$subject", subjectId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text ? Parse(text) : null;
    }

    /// <summary>Deletes delivery bookkeeping only (acknowledged or expired before the cutoff); alert events are kept.</summary>
    public async Task<int> PurgeOutboxAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(connection, null,
            "DELETE FROM alert_outbox WHERE (acked_at IS NOT NULL AND acked_at < $before) OR expires_at < $before;",
            [("$before", Format(before))], cancellationToken).ConfigureAwait(false);
    }

    public async Task TouchPeerAsync(string peerId, DateTimeOffset seenAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "INSERT INTO alert_peer_last_seen(peer_id, seen_at) VALUES ($peer, $at) ON CONFLICT(peer_id) DO UPDATE SET seen_at = MAX(seen_at, excluded.seen_at);",
            [("$peer", peerId), ("$at", Format(seenAt))], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadPeerLastSeenAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, seen_at FROM alert_peer_last_seen;";
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = Parse(reader.GetString(1));
        }

        return result;
    }

    /// <summary>
    /// Recipient side: idempotent upsert by alert id; a revision no newer than the stored one changes nothing.
    /// Returns true when the stored row changed.
    /// </summary>
    public async Task<bool> StoreInboundAsync(Alert alert, DateTimeOffset receivedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, null, """
            INSERT INTO alert_inbox(alert_id, revision, rule_id, subject_id, origin_peer_id, severity, state, title, detail,
                raised_at, resolved_at, received_at, notified_revision)
            VALUES ($id, $rev, $rule, $subject, $origin, $severity, $state, $title, $detail, $raised, $resolved, $received, 0)
            ON CONFLICT(alert_id) DO UPDATE SET revision = excluded.revision, severity = excluded.severity, state = excluded.state,
                title = excluded.title, detail = excluded.detail, resolved_at = excluded.resolved_at, received_at = excluded.received_at
            WHERE excluded.revision > alert_inbox.revision;
            """,
            [
                ("$id", alert.AlertId), ("$rev", (long)alert.Revision), ("$rule", alert.RuleId),
                ("$subject", alert.SubjectId), ("$origin", alert.OriginPeerId), ("$severity", (int)alert.Severity),
                ("$state", (int)alert.State), ("$title", alert.Title), ("$detail", alert.Detail),
                ("$raised", Format(alert.RaisedAt)), ("$resolved", alert.ResolvedAt is { } r ? Format(r) : null),
                ("$received", Format(receivedAt)),
            ], cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<IReadOnlyList<InboxAlert>> ReadInboxAsync(int limit, bool onlyUnnotified = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alert_id, revision, rule_id, subject_id, origin_peer_id, severity, state, title, detail, raised_at,
                resolved_at, received_at, notified_revision
            FROM alert_inbox
            """ + (onlyUnnotified ? " WHERE notified_revision < revision" : string.Empty) +
            " ORDER BY state ASC, raised_at DESC, alert_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<InboxAlert>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new InboxAlert(ReadAlert(reader), Parse(reader.GetString(11)), checked((uint)reader.GetInt64(12))));
        }

        return result;
    }

    public async Task MarkNotifiedAsync(string alertId, uint revision, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "UPDATE alert_inbox SET notified_revision = MAX(notified_revision, $rev) WHERE alert_id = $id;",
            [("$rev", (long)revision), ("$id", alertId)], cancellationToken).ConfigureAwait(false);
    }

    private const string AlertSelect =
        "SELECT alert_id, revision, rule_id, subject_id, origin_peer_id, severity, state, title, detail, raised_at, resolved_at FROM alerts";

    private static Alert ReadAlert(SqliteDataReader reader) => new(
        reader.GetString(0),
        checked((uint)reader.GetInt64(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        (AlertSeverity)reader.GetInt32(5),
        (AlertState)reader.GetInt32(6),
        reader.GetString(7),
        reader.GetString(8),
        Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : Parse(reader.GetString(10)));

    private static async Task<IReadOnlyList<Alert>> QueryAlertsAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = new List<Alert>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadAlert(reader));
        }

        return result;
    }

    private static Task<int> EnqueueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recipient,
        string alertId,
        uint revision,
        DateTimeOffset now,
        TimeSpan ttl,
        CancellationToken cancellationToken) => ExecuteAsync(connection, transaction,
        "INSERT OR IGNORE INTO alert_outbox(recipient_peer_id, alert_id, revision, enqueued_at, expires_at) VALUES ($r, $id, $rev, $at, $exp);",
        [
            ("$r", recipient), ("$id", alertId), ("$rev", (long)revision), ("$at", Format(now)),
            ("$exp", Format(now + ttl)),
        ], cancellationToken);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string Format(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must use UTC offset zero.", nameof(value));
        }

        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
