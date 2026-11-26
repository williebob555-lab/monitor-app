using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Application;

public sealed record PairingApplicationStoreOptions(string DatabasePath, TimeSpan BusyTimeout)
{
    public static PairingApplicationStoreOptions Create(string databasePath) =>
        new(databasePath, TimeSpan.FromSeconds(5));
}

public sealed class PairingApplicationStore
{
    private readonly PairingApplicationStoreOptions options;
    private readonly string connectionString;

    public PairingApplicationStore(PairingApplicationStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.BusyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        this.options = options;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: under WAL a reader sees the last commit while a writer works. Shared cache instead locks
            // whole tables and fails readers with SQLITE_LOCKED after the 30 s command timeout (busy_timeout does not
            // apply), so every read of origin_records stalled behind each sync batch.
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pairing_approvals(
                pairing_id TEXT PRIMARY KEY,
                remote_peer_id TEXT NOT NULL,
                remote_public_key_spki BLOB NOT NULL,
                remote_public_key_fingerprint BLOB NOT NULL,
                display_name TEXT NULL,
                local_role INTEGER NOT NULL,
                approval_state INTEGER NOT NULL,
                protocol_major INTEGER NOT NULL,
                protocol_minor INTEGER NOT NULL,
                remote_capabilities_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS peer_challenges(
                challenge_id TEXT PRIMARY KEY,
                pairing_id TEXT NULL,
                remote_peer_id TEXT NOT NULL,
                purpose INTEGER NOT NULL,
                local_role INTEGER NOT NULL,
                session_id TEXT NOT NULL,
                protocol_major INTEGER NOT NULL,
                protocol_minor INTEGER NOT NULL,
                challenge_bytes BLOB NOT NULL,
                remote_capabilities_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                local_proof_created INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS consumed_peer_proofs(
                replay_digest TEXT PRIMARY KEY,
                expires_at TEXT NOT NULL,
                consumed_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreatePairingAsync(
        PairingSnapshot pairing,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(pairing.CreatedAt, nameof(pairing));
        ValidateUtc(pairing.ExpiresAt, nameof(pairing));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pairing_approvals(pairing_id, remote_peer_id, remote_public_key_spki,
                remote_public_key_fingerprint, display_name, local_role, approval_state,
                protocol_major, protocol_minor, remote_capabilities_json, created_at, expires_at)
            VALUES ($id, $remote, $key, $fingerprint, $display, $role, $state,
                $major, $minor, $capabilities, $created, $expires);
            """;
        BindPairing(command, pairing);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> TryCreatePairingAsync(
        PairingSnapshot pairing,
        CancellationToken cancellationToken = default)
    {
        ValidateUtc(pairing.CreatedAt, nameof(pairing));
        ValidateUtc(pairing.ExpiresAt, nameof(pairing));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO pairing_approvals(pairing_id, remote_peer_id, remote_public_key_spki,
                remote_public_key_fingerprint, display_name, local_role, approval_state,
                protocol_major, protocol_minor, remote_capabilities_json, created_at, expires_at)
            VALUES ($id, $remote, $key, $fingerprint, $display, $role, $state,
                $major, $minor, $capabilities, $created, $expires);
            """;
        BindPairing(command, pairing);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<PairingSnapshot?> ReadPairingAsync(
        string pairingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pairing_id, remote_peer_id, remote_public_key_spki, remote_public_key_fingerprint,
                display_name, local_role, approval_state, protocol_major, protocol_minor,
                remote_capabilities_json, created_at, expires_at
            FROM pairing_approvals WHERE pairing_id = $id;
            """;
        command.Parameters.AddWithValue("$id", pairingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPairing(reader)
            : null;
    }

    /// <summary>Pairing requests awaiting local approval that have not expired, oldest first.</summary>
    public async Task<IReadOnlyList<PairingSnapshot>> ReadAwaitingApprovalAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pairing_id, remote_peer_id, remote_public_key_spki, remote_public_key_fingerprint,
                display_name, local_role, approval_state, protocol_major, protocol_minor,
                remote_capabilities_json, created_at, expires_at
            FROM pairing_approvals WHERE approval_state = $pending
            ORDER BY created_at, pairing_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pending", (int)PairingApprovalState.PendingApproval);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<PairingSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pairing = ReadPairing(reader);
            if (pairing.ExpiresAt > now)
            {
                result.Add(pairing);
            }
        }

        return result;
    }

    public async Task<bool> TransitionPairingAsync(
        string pairingId,
        PairingApprovalState expected,
        PairingApprovalState next,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pairing_approvals SET approval_state = $next
            WHERE pairing_id = $id AND approval_state = $expected;
            """;
        command.Parameters.AddWithValue("$id", pairingId);
        command.Parameters.AddWithValue("$expected", (int)expected);
        command.Parameters.AddWithValue("$next", (int)next);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task CreateChallengeAsync(
        StoredChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO peer_challenges(challenge_id, pairing_id, remote_peer_id, purpose,
                local_role, session_id, protocol_major, protocol_minor, challenge_bytes,
                remote_capabilities_json, created_at, expires_at, local_proof_created)
            VALUES ($challenge, $pairing, $remote, $purpose, $role, $session, $major, $minor,
                $bytes, $capabilities, $created, $expires, 0);
            """;
        command.Parameters.AddWithValue("$challenge", challenge.ChallengeId);
        command.Parameters.AddWithValue("$pairing", (object?)challenge.PairingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$remote", challenge.RemotePeerId);
        command.Parameters.AddWithValue("$purpose", (int)challenge.Purpose);
        command.Parameters.AddWithValue("$role", (int)challenge.LocalRole);
        command.Parameters.AddWithValue("$session", challenge.SessionId);
        command.Parameters.AddWithValue("$major", challenge.ProtocolVersion.Major);
        command.Parameters.AddWithValue("$minor", challenge.ProtocolVersion.Minor);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = challenge.ChallengeBytes;
        command.Parameters.AddWithValue("$capabilities", SerializeCapabilities(challenge.RemoteCapabilities));
        command.Parameters.AddWithValue("$created", Format(challenge.CreatedAt));
        command.Parameters.AddWithValue("$expires", Format(challenge.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> TryCreatePairingChallengeAsync(
        StoredChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        if (challenge.PairingId is null)
        {
            throw new ArgumentException("A pairing challenge requires a pairing id.", nameof(challenge));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO peer_challenges(challenge_id, pairing_id, remote_peer_id, purpose,
                local_role, session_id, protocol_major, protocol_minor, challenge_bytes,
                remote_capabilities_json, created_at, expires_at, local_proof_created)
            SELECT $challenge, $pairing, $remote, $purpose, $role, $session, $major, $minor,
                $bytes, $capabilities, $created, $expires, 0
            WHERE NOT EXISTS (
                SELECT 1 FROM peer_challenges WHERE pairing_id = $pairing
            );
            """;
        command.Parameters.AddWithValue("$challenge", challenge.ChallengeId);
        command.Parameters.AddWithValue("$pairing", challenge.PairingId);
        command.Parameters.AddWithValue("$remote", challenge.RemotePeerId);
        command.Parameters.AddWithValue("$purpose", (int)challenge.Purpose);
        command.Parameters.AddWithValue("$role", (int)challenge.LocalRole);
        command.Parameters.AddWithValue("$session", challenge.SessionId);
        command.Parameters.AddWithValue("$major", challenge.ProtocolVersion.Major);
        command.Parameters.AddWithValue("$minor", challenge.ProtocolVersion.Minor);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = challenge.ChallengeBytes;
        command.Parameters.AddWithValue("$capabilities", SerializeCapabilities(challenge.RemoteCapabilities));
        command.Parameters.AddWithValue("$created", Format(challenge.CreatedAt));
        command.Parameters.AddWithValue("$expires", Format(challenge.ExpiresAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task<StoredChallenge?> ReadChallengeAsync(
        string challengeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT challenge_id, pairing_id, remote_peer_id, purpose, local_role, session_id,
                protocol_major, protocol_minor, challenge_bytes, remote_capabilities_json,
                created_at, expires_at, local_proof_created
            FROM peer_challenges WHERE challenge_id = $id;
            """;
        command.Parameters.AddWithValue("$id", challengeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadChallenge(reader)
            : null;
    }

    internal async Task<StoredChallenge?> ReadPairingChallengeAsync(
        string pairingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT challenge_id, pairing_id, remote_peer_id, purpose, local_role, session_id,
                protocol_major, protocol_minor, challenge_bytes, remote_capabilities_json,
                created_at, expires_at, local_proof_created
            FROM peer_challenges WHERE pairing_id = $id
            ORDER BY created_at, challenge_id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", pairingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadChallenge(reader)
            : null;
    }

    internal async Task MarkLocalProofCreatedAsync(
        string challengeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE peer_challenges SET local_proof_created = 1 WHERE challenge_id = $id;";
        command.Parameters.AddWithValue("$id", challengeId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Peer challenge was not found.");
        }
    }

    internal bool TryConsumeProof(string replayDigest, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        ValidateUtc(expiresAt, nameof(expiresAt));
        ValidateUtc(now, nameof(now));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = "DELETE FROM consumed_peer_proofs WHERE expires_at < $now;";
        cleanup.Parameters.AddWithValue("$now", Format(now));
        cleanup.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO consumed_peer_proofs(replay_digest, expires_at, consumed_at)
            VALUES ($digest, $expires, $now);
            """;
        insert.Parameters.AddWithValue("$digest", replayDigest);
        insert.Parameters.AddWithValue("$expires", Format(expiresAt));
        insert.Parameters.AddWithValue("$now", Format(now));
        var inserted = insert.ExecuteNonQuery() == 1;
        transaction.Commit();
        return inserted;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            MonitorApp.Peer.Persistence.SqliteConnectionTracker.Track(connection);
            await ConfigureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // A cancelled or failed configuration must not leave an open handle on the database file behind.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            MonitorApp.Peer.Persistence.SqliteConnectionTracker.Track(connection);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {checked((int)options.BusyTimeout.TotalMilliseconds)}; PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = {checked((int)options.BusyTimeout.TotalMilliseconds)}; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindPairing(SqliteCommand command, PairingSnapshot pairing)
    {
        command.Parameters.AddWithValue("$id", pairing.PairingId);
        command.Parameters.AddWithValue("$remote", pairing.RemoteIdentity.PeerId);
        command.Parameters.Add("$key", SqliteType.Blob).Value = pairing.RemoteIdentity.PublicKeySpki;
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = pairing.RemoteIdentity.PublicKeyFingerprint;
        command.Parameters.AddWithValue("$display", (object?)pairing.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", (int)pairing.LocalRole);
        command.Parameters.AddWithValue("$state", (int)pairing.State);
        command.Parameters.AddWithValue("$major", pairing.SelectedVersion.Major);
        command.Parameters.AddWithValue("$minor", pairing.SelectedVersion.Minor);
        command.Parameters.AddWithValue("$capabilities", SerializeCapabilities(pairing.RemoteCapabilities));
        command.Parameters.AddWithValue("$created", Format(pairing.CreatedAt));
        command.Parameters.AddWithValue("$expires", Format(pairing.ExpiresAt));
    }

    private static PairingSnapshot ReadPairing(SqliteDataReader reader) => new(
        reader.GetString(0),
        new PeerPublicIdentity(reader.GetString(1), (byte[])reader[2], (byte[])reader[3]),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        (PeerProofRole)reader.GetInt32(5),
        (PairingApprovalState)reader.GetInt32(6),
        new PeerProtocolVersion(checked((uint)reader.GetInt64(7)), checked((uint)reader.GetInt64(8))),
        DeserializeCapabilities(reader.GetString(9)),
        Parse(reader.GetString(10)),
        Parse(reader.GetString(11)));

    private static StoredChallenge ReadChallenge(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        (PeerProofPurpose)reader.GetInt32(3),
        (PeerProofRole)reader.GetInt32(4),
        reader.GetString(5),
        new PeerProtocolVersion(checked((uint)reader.GetInt64(6)), checked((uint)reader.GetInt64(7))),
        (byte[])reader[8],
        DeserializeCapabilities(reader.GetString(9)),
        Parse(reader.GetString(10)),
        Parse(reader.GetString(11)),
        reader.GetBoolean(12));

    private static string SerializeCapabilities(IReadOnlyList<PeerCapability> capabilities) =>
        JsonSerializer.Serialize(capabilities);

    private static List<PeerCapability> DeserializeCapabilities(string json) =>
        JsonSerializer.Deserialize<List<PeerCapability>>(json) ?? [];

    private static string Format(DateTimeOffset value)
    {
        ValidateUtc(value, nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}

internal sealed class SqlitePeerChallengeReplayGuard(PairingApplicationStore store) : IPeerChallengeReplayGuard
{
    public bool TryConsume(string replayKey, DateTimeOffset expiresAt, DateTimeOffset now) =>
        store.TryConsumeProof(replayKey, expiresAt, now);
}
