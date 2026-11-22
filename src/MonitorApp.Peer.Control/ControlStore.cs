using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Control;

public sealed record ControlStoreOptions(string DatabasePath, TimeSpan BusyTimeout)
{
    public static ControlStoreOptions Create(string databasePath) => new(databasePath, TimeSpan.FromSeconds(5));
}

/// <summary>What a trusted peer may ask this desktop to do. Presence requirements are policy, never grantable away.</summary>
public sealed record ControlGrant(
    string PeerId, bool MonitoringControls, bool PairingApproval, DateTimeOffset? UpdatedAt, bool UpdateApproval = false)
{
    /// <summary>
    /// Owner decision (2026-11-22): deny by default. A newly paired device can read replicated data but may not issue any control
    /// command (including presence-key enrollment) until the administrator grants it on this computer.
    /// </summary>
    public static ControlGrant Default(string peerId) => new(peerId, false, false, null);

    public bool AllowsAnything => MonitoringControls || PairingApproval || UpdateApproval;
}

public enum PresenceEnrollmentState
{
    Pending = 1,
    Active = 2,
    Rejected = 3,
    Superseded = 4,
}

public sealed record PresenceEnrollment(
    string EnrollmentId,
    string PeerId,
    byte[] PublicKeySpki,
    byte[] Fingerprint,
    PresenceEnrollmentState State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt);

public sealed record StoredControlResult(
    string IssuerPeerId,
    string CommandId,
    byte[] Nonce,
    byte[] PayloadHash,
    string Action,
    ControlStatus Status,
    string ReasonCode,
    string Message,
    DateTimeOffset DecidedAt);

public sealed record ControlAuditEntry(
    long Id,
    DateTimeOffset At,
    string PeerId,
    string CommandId,
    string Action,
    string Tier,
    string Outcome,
    string ReasonCode,
    string SessionId,
    bool PresenceVerified,
    string Detail);

/// <summary>
/// Durable control state on the desktop, in the peer database: per-peer grants, enrolled presence keys, every accepted
/// command with its result (the replay and idempotency record), and an append-only audit trail of every attempt.
/// </summary>
public sealed class ControlStore
{
    private const int SchemaVersion = 2;
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    public ControlStore(ControlStoreOptions options)
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
            "CREATE TABLE IF NOT EXISTS control_schema_version(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1), version INTEGER NOT NULL);",
            [], cancellationToken).ConfigureAwait(false);
        var current = 0L;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version FROM control_schema_version WHERE singleton_id = 1;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                current = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (current > SchemaVersion)
        {
            throw new InvalidOperationException("The control schema is newer than this application supports.");
        }

        if (current < 1)
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE control_grants(
                    peer_id TEXT PRIMARY KEY,
                    monitoring_controls INTEGER NOT NULL,
                    pairing_approval INTEGER NOT NULL,
                    updated_at TEXT NOT NULL);
                CREATE TABLE control_presence_enrollments(
                    enrollment_id TEXT PRIMARY KEY,
                    peer_id TEXT NOT NULL,
                    public_key_spki BLOB NOT NULL,
                    fingerprint BLOB NOT NULL,
                    state INTEGER NOT NULL,
                    requested_at TEXT NOT NULL,
                    decided_at TEXT NULL);
                CREATE UNIQUE INDEX control_presence_one_active ON control_presence_enrollments(peer_id) WHERE state = 2;
                CREATE UNIQUE INDEX control_presence_one_pending ON control_presence_enrollments(peer_id) WHERE state = 1;
                CREATE TABLE control_commands(
                    issuer_peer_id TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    nonce BLOB NOT NULL,
                    payload_sha256 BLOB NOT NULL,
                    action TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    reason_code TEXT NOT NULL,
                    message TEXT NOT NULL,
                    decided_at TEXT NOT NULL,
                    PRIMARY KEY(issuer_peer_id, command_id));
                CREATE UNIQUE INDEX control_commands_nonce ON control_commands(issuer_peer_id, nonce);
                CREATE TABLE control_audit(
                    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at TEXT NOT NULL,
                    peer_id TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    action TEXT NOT NULL,
                    tier TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    reason_code TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    presence_verified INTEGER NOT NULL,
                    detail TEXT NOT NULL);
                CREATE INDEX control_audit_peer ON control_audit(peer_id, audit_id);
                CREATE TRIGGER control_audit_no_update BEFORE UPDATE ON control_audit
                BEGIN SELECT RAISE(ABORT, 'control audit is append-only'); END;
                CREATE TRIGGER control_audit_no_delete BEFORE DELETE ON control_audit
                BEGIN SELECT RAISE(ABORT, 'control audit is append-only'); END;
                INSERT INTO control_schema_version(singleton_id, version) VALUES (1, 1);
                """, [], cancellationToken).ConfigureAwait(false);
        }

        if (current < 2)
        {
            // Additive: remote update approval is a separate, deny-by-default grant flag.
            await ExecuteAsync(connection, transaction, """
                ALTER TABLE control_grants ADD COLUMN update_approval INTEGER NOT NULL DEFAULT 0;
                UPDATE control_schema_version SET version = 2 WHERE singleton_id = 1;
                """, [], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlGrant?> ReadGrantAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, monitoring_controls, pairing_approval, updated_at, update_approval FROM control_grants WHERE peer_id = $p;";
        command.Parameters.AddWithValue("$p", peerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGrant(reader) : null;
    }

    public async Task<IReadOnlyList<ControlGrant>> ReadGrantsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, monitoring_controls, pairing_approval, updated_at, update_approval FROM control_grants ORDER BY peer_id;";
        var result = new List<ControlGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadGrant(reader));
        }

        return result;
    }

    public async Task SetGrantAsync(ControlGrant grant, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.PeerId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO control_grants(peer_id, monitoring_controls, pairing_approval, updated_at, update_approval) VALUES ($p, $m, $a, $at, $u)
            ON CONFLICT(peer_id) DO UPDATE SET monitoring_controls = excluded.monitoring_controls,
                pairing_approval = excluded.pairing_approval, updated_at = excluded.updated_at,
                update_approval = excluded.update_approval;
            """,
            [("$p", grant.PeerId), ("$m", grant.MonitoringControls ? 1 : 0), ("$a", grant.PairingApproval ? 1 : 0), ("$at", Format(now)),
                ("$u", grant.UpdateApproval ? 1 : 0)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a presence-key enrollment awaiting local approval; an identical pending request is returned as is.</summary>
    public async Task<PresenceEnrollment> RequestEnrollmentAsync(
        string peerId, byte[] publicKeySpki, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var fingerprint = SHA256.HashData(publicKeySpki);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var pending = await QueryEnrollmentsAsync(connection, transaction,
            "WHERE peer_id = $p AND state = 1", [("$p", peerId)], cancellationToken).ConfigureAwait(false);
        if (FirstOrNull(pending) is { } existing)
        {
            if (existing.Fingerprint.AsSpan().SequenceEqual(fingerprint))
            {
                return existing;
            }

            // A newer request replaces an older unapproved one; nothing was ever trusted.
            await ExecuteAsync(connection, transaction,
                "UPDATE control_presence_enrollments SET state = 3, decided_at = $at WHERE enrollment_id = $id;",
                [("$at", Format(now)), ("$id", existing.EnrollmentId)], cancellationToken).ConfigureAwait(false);
        }

        var id = Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, transaction,
            "INSERT INTO control_presence_enrollments(enrollment_id, peer_id, public_key_spki, fingerprint, state, requested_at) VALUES ($id, $p, $k, $f, 1, $at);",
            [("$id", id), ("$p", peerId), ("$k", publicKeySpki), ("$f", fingerprint), ("$at", Format(now))],
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PresenceEnrollment(id, peerId, publicKeySpki, fingerprint, PresenceEnrollmentState.Pending, now, null);
    }

    public async Task<PresenceEnrollment?> ReadActivePresenceKeyAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await QueryEnrollmentsAsync(connection, null, "WHERE peer_id = $p AND state = 2", [("$p", peerId)], cancellationToken)
            .ConfigureAwait(false);
        return FirstOrNull(found);
    }

    public async Task<PresenceEnrollment?> ReadEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await QueryEnrollmentsAsync(connection, null, "WHERE enrollment_id = $id", [("$id", enrollmentId)], cancellationToken)
            .ConfigureAwait(false);
        return FirstOrNull(found);
    }

    public async Task<IReadOnlyList<PresenceEnrollment>> ReadEnrollmentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryEnrollmentsAsync(connection, null, "ORDER BY requested_at DESC, enrollment_id LIMIT 200", [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Approves (activating the key and superseding the previous active one) or rejects a pending enrollment.</summary>
    public async Task<PresenceEnrollment?> DecideEnrollmentAsync(
        string enrollmentId, bool approve, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var found = FirstOrNull(await QueryEnrollmentsAsync(connection, transaction, "WHERE enrollment_id = $id AND state = 1",
            [("$id", enrollmentId)], cancellationToken).ConfigureAwait(false));
        if (found is null)
        {
            return null;
        }

        if (approve)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE control_presence_enrollments SET state = 4, decided_at = $at WHERE peer_id = $p AND state = 2;",
                [("$at", Format(now)), ("$p", found.PeerId)], cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            "UPDATE control_presence_enrollments SET state = $s, decided_at = $at WHERE enrollment_id = $id;",
            [("$s", approve ? 2 : 3), ("$at", Format(now)), ("$id", enrollmentId)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return found with
        {
            State = approve ? PresenceEnrollmentState.Active : PresenceEnrollmentState.Rejected,
            DecidedAt = now,
        };
    }

    public async Task<StoredControlResult?> ReadCommandAsync(
        string issuerPeerId, string commandId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT issuer_peer_id, command_id, nonce, payload_sha256, action, status, reason_code, message, decided_at
            FROM control_commands WHERE issuer_peer_id = $i AND command_id = $c;
            """;
        command.Parameters.AddWithValue("$i", issuerPeerId);
        command.Parameters.AddWithValue("$c", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredControlResult(
            reader.GetString(0), reader.GetString(1), (byte[])reader[2], (byte[])reader[3], reader.GetString(4),
            (ControlStatus)reader.GetInt32(5), reader.GetString(6), reader.GetString(7), Parse(reader.GetString(8)));
    }

    public async Task<bool> NonceSeenAsync(string issuerPeerId, byte[] nonce, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM control_commands WHERE issuer_peer_id = $i AND nonce = $n;";
        command.Parameters.AddWithValue("$i", issuerPeerId);
        command.Parameters.AddWithValue("$n", nonce);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Durably records a result; false when the (issuer, command id) or (issuer, nonce) already exists.</summary>
    public async Task<bool> TryRecordCommandAsync(StoredControlResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, """
                INSERT INTO control_commands(issuer_peer_id, command_id, nonce, payload_sha256, action, status, reason_code, message, decided_at)
                VALUES ($i, $c, $n, $h, $a, $s, $r, $m, $at);
                """,
                [
                    ("$i", result.IssuerPeerId), ("$c", result.CommandId), ("$n", result.Nonce), ("$h", result.PayloadHash),
                    ("$a", result.Action), ("$s", (int)result.Status), ("$r", result.ReasonCode), ("$m", result.Message),
                    ("$at", Format(result.DecidedAt)),
                ], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task AppendAuditAsync(ControlAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO control_audit(at, peer_id, command_id, action, tier, outcome, reason_code, session_id, presence_verified, detail)
            VALUES ($at, $p, $c, $a, $t, $o, $r, $s, $v, $d);
            """,
            [
                ("$at", Format(entry.At)), ("$p", entry.PeerId), ("$c", entry.CommandId), ("$a", entry.Action),
                ("$t", entry.Tier), ("$o", entry.Outcome), ("$r", entry.ReasonCode), ("$s", entry.SessionId),
                ("$v", entry.PresenceVerified ? 1 : 0), ("$d", entry.Detail),
            ], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Newest first. <paramref name="beforeId"/> pages backwards; <paramref name="peerId"/> filters.</summary>
    public async Task<IReadOnlyList<ControlAuditEntry>> ReadAuditAsync(
        int limit, long? beforeId = null, string? peerId = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT audit_id, at, peer_id, command_id, action, tier, outcome, reason_code, session_id, presence_verified, detail
            FROM control_audit
            WHERE ($before IS NULL OR audit_id < $before) AND ($peer IS NULL OR peer_id = $peer)
            ORDER BY audit_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", (object?)beforeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$peer", (object?)peerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<ControlAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ControlAuditEntry(
                reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9) == 1, reader.GetString(10)));
        }

        return result;
    }

    private static PresenceEnrollment? FirstOrNull(IReadOnlyList<PresenceEnrollment> items) => items.Count > 0 ? items[0] : null;

    private static ControlGrant ReadGrant(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt32(2) == 1, Parse(reader.GetString(3)), reader.GetInt32(4) == 1);

    private static async Task<IReadOnlyList<PresenceEnrollment>> QueryEnrollmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tail,
        (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT enrollment_id, peer_id, public_key_spki, fingerprint, state, requested_at, decided_at " +
            "FROM control_presence_enrollments " + tail + ";";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = new List<PresenceEnrollment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PresenceEnrollment(
                reader.GetString(0), reader.GetString(1), (byte[])reader[2], (byte[])reader[3],
                (PresenceEnrollmentState)reader.GetInt32(4), Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6))));
        }

        return result;
    }

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
