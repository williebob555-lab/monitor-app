using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

public sealed partial class PeerStore
{
    private const int SchemaVersion = 4;
    private const int PublicKeyFingerprintSize = 32;
    private const int P256SignatureSize = 64;
    private readonly string connectionString;
    private readonly int busyTimeoutMilliseconds;

    /// <summary>Notifies in-process subscribers of origin records newly committed through this store instance.</summary>
    public PeerLiveFeed Live { get; } = new();

    /// <summary>
    /// Serializes local-origin writers sharing this store (the system-metrics bridge and the LAN link bridge each
    /// allocate the next local sequence and sign it): hold it from reading <c>NextSequence</c> until the last append.
    /// </summary>
    public SemaphoreSlim LocalAppendGate { get; } = new(1, 1);

    public PeerStore(PeerStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.BusyTimeout <= TimeSpan.Zero || options.BusyTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Busy timeout must be positive and supported by SQLite.");
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS local_peer_identity (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                peer_id TEXT NOT NULL UNIQUE,
                public_key_spki BLOB NOT NULL,
                public_key_fingerprint BLOB NOT NULL,
                key_provider_reference TEXT NOT NULL,
                origin_stream_id TEXT NOT NULL UNIQUE,
                next_sequence INTEGER NOT NULL CHECK (next_sequence > 0),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS trusted_peers (
                peer_id TEXT PRIMARY KEY,
                public_key_spki BLOB NOT NULL,
                public_key_fingerprint BLOB NOT NULL,
                display_name TEXT NULL,
                trust_state INTEGER NOT NULL,
                paired_at TEXT NOT NULL,
                revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS peer_locators (
                peer_id TEXT NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                updated_at TEXT NOT NULL,
                last_connected_at TEXT NULL,
                PRIMARY KEY(peer_id, host, port)
            );
            CREATE TABLE IF NOT EXISTS pairing_sessions (
                pairing_session_id TEXT PRIMARY KEY,
                remote_peer_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                completed INTEGER NOT NULL CHECK (completed IN (0, 1))
            );
            CREATE TABLE IF NOT EXISTS observed_entities (
                entity_id TEXT PRIMARY KEY,
                entity_kind TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS identity_claims (
                entity_id TEXT NOT NULL,
                claim_type TEXT NOT NULL,
                normalized_value TEXT NOT NULL,
                strength INTEGER NOT NULL,
                observer_peer_id TEXT NOT NULL,
                evidence_kind INTEGER NOT NULL,
                confidence REAL NOT NULL CHECK (confidence BETWEEN 0 AND 1),
                observed_at TEXT NOT NULL,
                PRIMARY KEY(entity_id, claim_type, normalized_value, observer_peer_id),
                FOREIGN KEY(entity_id) REFERENCES observed_entities(entity_id)
            );
            CREATE TABLE IF NOT EXISTS relationship_observations (
                relationship_id TEXT PRIMARY KEY,
                source_entity_id TEXT NOT NULL,
                target_entity_id TEXT NOT NULL,
                relationship_kind INTEGER NOT NULL,
                evidence_kind INTEGER NOT NULL,
                observer_peer_id TEXT NOT NULL,
                confidence REAL NOT NULL CHECK (confidence BETWEEN 0 AND 1),
                valid_from TEXT NOT NULL,
                valid_to TEXT NULL,
                FOREIGN KEY(source_entity_id) REFERENCES observed_entities(entity_id),
                FOREIGN KEY(target_entity_id) REFERENCES observed_entities(entity_id)
            );
            CREATE TABLE IF NOT EXISTS adapter_instances (
                adapter_instance_id TEXT PRIMARY KEY,
                adapter_id TEXT NOT NULL,
                adapter_version TEXT NOT NULL,
                collector_peer_id TEXT NOT NULL,
                observed_entity_id TEXT NULL,
                credential_reference TEXT NULL,
                enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                FOREIGN KEY(observed_entity_id) REFERENCES observed_entities(entity_id)
            );
            CREATE TABLE IF NOT EXISTS adapter_status (
                adapter_instance_id TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                state TEXT NOT NULL,
                reason TEXT NULL,
                PRIMARY KEY(adapter_instance_id, observed_at),
                FOREIGN KEY(adapter_instance_id) REFERENCES adapter_instances(adapter_instance_id)
            );
            CREATE TABLE IF NOT EXISTS origin_records (
                origin_peer_id TEXT NOT NULL,
                origin_stream_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                subject_entity_id TEXT NOT NULL,
                adapter_instance_id TEXT NOT NULL,
                signal_id TEXT NOT NULL,
                record_bytes BLOB NOT NULL,
                content_hash BLOB NOT NULL,
                signature BLOB NOT NULL,
                observed_at TEXT NOT NULL,
                received_at TEXT NOT NULL,
                received_from_peer_id TEXT NULL,
                provenance INTEGER NOT NULL,
                quality TEXT NOT NULL,
                PRIMARY KEY(origin_peer_id, origin_stream_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_origin_records_subject_signal
                ON origin_records(subject_entity_id, signal_id, observed_at DESC, received_at DESC);
            CREATE INDEX IF NOT EXISTS ix_origin_records_observed_at
                ON origin_records(observed_at, origin_peer_id, origin_stream_id, sequence);
            CREATE TABLE IF NOT EXISTS replication_cursors (
                remote_peer_id TEXT NOT NULL,
                origin_peer_id TEXT NOT NULL,
                origin_stream_id TEXT NOT NULL,
                accepted_through INTEGER NOT NULL CHECK (accepted_through >= 0),
                fidelity TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(remote_peer_id, origin_peer_id, origin_stream_id)
            );
            CREATE TABLE IF NOT EXISTS replication_gaps (
                remote_peer_id TEXT NOT NULL,
                origin_peer_id TEXT NOT NULL,
                origin_stream_id TEXT NOT NULL,
                first_sequence INTEGER NOT NULL CHECK (first_sequence > 0),
                last_sequence INTEGER NOT NULL CHECK (last_sequence >= first_sequence),
                PRIMARY KEY(remote_peer_id, origin_peer_id, origin_stream_id, first_sequence),
                FOREIGN KEY(remote_peer_id, origin_peer_id, origin_stream_id)
                    REFERENCES replication_cursors(remote_peer_id, origin_peer_id, origin_stream_id)
                    ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS capability_catalog (
                capability_id TEXT PRIMARY KEY,
                namespace TEXT NOT NULL,
                version TEXT NOT NULL,
                descriptor BLOB NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS collector_assignments (
                observed_entity_id TEXT PRIMARY KEY,
                adapter_instance_id TEXT NOT NULL UNIQUE,
                collector_peer_id TEXT NOT NULL,
                assigned_at TEXT NOT NULL,
                FOREIGN KEY(observed_entity_id) REFERENCES observed_entities(entity_id),
                FOREIGN KEY(adapter_instance_id) REFERENCES adapter_instances(adapter_instance_id)
            );
            CREATE TABLE IF NOT EXISTS collector_assignment_history (
                observed_entity_id TEXT NOT NULL,
                adapter_instance_id TEXT NOT NULL,
                collector_peer_id TEXT NOT NULL,
                assigned_at TEXT NOT NULL,
                released_at TEXT NULL,
                PRIMARY KEY(observed_entity_id, assigned_at),
                FOREIGN KEY(observed_entity_id) REFERENCES observed_entities(entity_id),
                FOREIGN KEY(adapter_instance_id) REFERENCES adapter_instances(adapter_instance_id)
            );
            CREATE TABLE IF NOT EXISTS observation_rollups_hourly (
                origin_peer_id TEXT NOT NULL,
                subject_entity_id TEXT NOT NULL,
                signal_id TEXT NOT NULL,
                hour_start TEXT NOT NULL,
                sample_count INTEGER NOT NULL CHECK (sample_count > 0),
                numeric_count INTEGER NOT NULL CHECK (numeric_count >= 0),
                min_value REAL NULL,
                max_value REAL NULL,
                sum_value REAL NULL,
                unit TEXT NULL,
                first_observed_at TEXT NOT NULL,
                last_observed_at TEXT NOT NULL,
                worst_quality TEXT NOT NULL,
                valid_count INTEGER NOT NULL,
                estimated_count INTEGER NOT NULL,
                unavailable_count INTEGER NOT NULL,
                denied_count INTEGER NOT NULL,
                error_count INTEGER NOT NULL,
                other_quality_count INTEGER NOT NULL,
                PRIMARY KEY(origin_peer_id, subject_entity_id, signal_id, hour_start)
            );
            CREATE INDEX IF NOT EXISTS ix_rollups_hour ON observation_rollups_hourly(hour_start);
            CREATE TABLE IF NOT EXISTS pinned_incidents (
                incident_id TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                from_at TEXT NOT NULL,
                to_at TEXT NOT NULL CHECK (to_at > from_at),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pruned_origin_ranges (
                origin_peer_id TEXT NOT NULL,
                origin_stream_id TEXT NOT NULL,
                first_sequence INTEGER NOT NULL CHECK (first_sequence > 0),
                last_sequence INTEGER NOT NULL CHECK (last_sequence >= first_sequence),
                pruned_at TEXT NOT NULL,
                PRIMARY KEY(origin_peer_id, origin_stream_id, first_sequence)
            );
            INSERT OR IGNORE INTO collector_assignment_history(
                observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at, released_at)
            SELECT observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at, NULL
            FROM collector_assignments;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var migration = connection.CreateCommand();
        migration.CommandText = "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES ($version, $now);";
        migration.Parameters.AddWithValue("$version", SchemaVersion);
        migration.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLocalIdentityAsync(LocalPeerIdentity identity, CancellationToken cancellationToken = default)
    {
        Validate(identity);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT peer_id, public_key_spki, public_key_fingerprint, key_provider_reference, origin_stream_id, next_sequence, created_at FROM local_peer_identity WHERE singleton_id = 1;";
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stored = ReadLocalIdentity(reader);
            if (!Equal(stored, identity))
            {
                throw new PeerPersistenceConflictException("Local peer identity is immutable once created.");
            }

            return;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO local_peer_identity(singleton_id, peer_id, public_key_spki, public_key_fingerprint,
                key_provider_reference, origin_stream_id, next_sequence, created_at)
            VALUES (1, $peer, $key, $fingerprint, $provider, $stream, $next, $created);
            """;
        insert.Parameters.AddWithValue("$peer", identity.PeerId);
        insert.Parameters.Add("$key", SqliteType.Blob).Value = identity.PublicKeySpki;
        insert.Parameters.Add("$fingerprint", SqliteType.Blob).Value = identity.PublicKeyFingerprint;
        insert.Parameters.AddWithValue("$provider", identity.KeyProviderReference);
        insert.Parameters.AddWithValue("$stream", identity.OriginStreamId);
        insert.Parameters.AddWithValue("$next", ToInt64(identity.NextSequence));
        insert.Parameters.AddWithValue("$created", Format(identity.CreatedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalPeerIdentity?> ReadLocalIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, public_key_spki, public_key_fingerprint, key_provider_reference, origin_stream_id, next_sequence, created_at FROM local_peer_identity WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLocalIdentity(reader) : null;
    }

    public async Task UpsertTrustedPeerAsync(TrustedPeer peer, CancellationToken cancellationToken = default)
    {
        Validate(peer);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trusted_peers(peer_id, public_key_spki, public_key_fingerprint, display_name, trust_state, paired_at, revoked_at)
            VALUES ($peer, $key, $fingerprint, $name, $state, $paired, $revoked)
            ON CONFLICT(peer_id) DO UPDATE SET
                display_name = excluded.display_name,
                trust_state = excluded.trust_state,
                revoked_at = excluded.revoked_at
            WHERE trusted_peers.public_key_spki = excluded.public_key_spki
              AND trusted_peers.public_key_fingerprint = excluded.public_key_fingerprint
              AND trusted_peers.paired_at = excluded.paired_at;
            """;
        Bind(command, peer);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PeerPersistenceConflictException("A trusted peer id cannot be rebound to another application identity.");
        }
    }

    public async Task RevokeTrustedPeerAsync(string peerId, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(peerId, nameof(peerId));
        EnsureUtc(revokedAt, nameof(revokedAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trusted_peers SET trust_state = $state, revoked_at = $at WHERE peer_id = $peer AND trust_state = $active;";
        command.Parameters.AddWithValue("$state", (int)PeerTrustState.Revoked);
        command.Parameters.AddWithValue("$active", (int)PeerTrustState.Active);
        command.Parameters.AddWithValue("$at", Format(revokedAt));
        command.Parameters.AddWithValue("$peer", peerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PeerPersistenceConflictException("Peer is missing or already revoked.");
        }
    }

    public async Task<TrustedPeer?> ReadTrustedPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(peerId, nameof(peerId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, public_key_spki, public_key_fingerprint, display_name, trust_state, paired_at, revoked_at FROM trusted_peers WHERE peer_id = $peer;";
        command.Parameters.AddWithValue("$peer", peerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrustedPeer(reader.GetString(0), Bytes(reader, 1), Bytes(reader, 2), NullableString(reader, 3),
            (PeerTrustState)reader.GetInt32(4), Parse(reader.GetString(5)), NullableDate(reader, 6));
    }

    public async Task<IReadOnlyList<TrustedPeer>> ReadTrustedPeersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT peer_id, public_key_spki, public_key_fingerprint, display_name, trust_state, paired_at, revoked_at
            FROM trusted_peers
            ORDER BY peer_id;
            """;
        var result = new List<TrustedPeer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TrustedPeer(
                reader.GetString(0),
                Bytes(reader, 1),
                Bytes(reader, 2),
                NullableString(reader, 3),
                (PeerTrustState)reader.GetInt32(4),
                Parse(reader.GetString(5)),
                NullableDate(reader, 6)));
        }

        return result;
    }

    public async Task UpsertPeerLocatorAsync(PeerLocator locator, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(locator.PeerId, nameof(locator.PeerId));
        EnsureIdentifier(locator.Host, nameof(locator.Host));
        if (locator.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(locator));
        EnsureUtc(locator.UpdatedAt, nameof(locator.UpdatedAt));
        EnsureUtc(locator.LastConnectedAt, nameof(locator.LastConnectedAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO peer_locators(peer_id, host, port, updated_at, last_connected_at)
            VALUES ($peer, $host, $port, $updated, $connected)
            ON CONFLICT(peer_id, host, port) DO UPDATE SET updated_at = excluded.updated_at, last_connected_at = excluded.last_connected_at;
            """;
        command.Parameters.AddWithValue("$peer", locator.PeerId);
        command.Parameters.AddWithValue("$host", locator.Host);
        command.Parameters.AddWithValue("$port", locator.Port);
        command.Parameters.AddWithValue("$updated", Format(locator.UpdatedAt));
        command.Parameters.AddWithValue("$connected", Db(locator.LastConnectedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PeerLocator>> ReadPeerLocatorsAsync(string peerId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(peerId, nameof(peerId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, host, port, updated_at, last_connected_at FROM peer_locators WHERE peer_id = $peer ORDER BY updated_at DESC, host, port;";
        command.Parameters.AddWithValue("$peer", peerId);
        var result = new List<PeerLocator>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PeerLocator(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Parse(reader.GetString(3)), NullableDate(reader, 4)));
        }

        return result;
    }

    public async Task CreatePairingSessionAsync(PairingSession session, CancellationToken cancellationToken = default)
    {
        Validate(session);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO pairing_sessions(pairing_session_id, remote_peer_id, created_at, expires_at, completed) VALUES ($id, $peer, $created, $expires, $completed);";
        command.Parameters.AddWithValue("$id", session.PairingSessionId);
        command.Parameters.AddWithValue("$peer", session.RemotePeerId);
        command.Parameters.AddWithValue("$created", Format(session.CreatedAt));
        command.Parameters.AddWithValue("$expires", Format(session.ExpiresAt));
        command.Parameters.AddWithValue("$completed", session.Completed ? 1 : 0);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new PeerPersistenceConflictException("Pairing session already exists.");
        }
    }

    public async Task CompletePairingSessionAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(sessionId, nameof(sessionId));
        EnsureUtc(now, nameof(now));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE pairing_sessions SET completed = 1 WHERE pairing_session_id = $id AND completed = 0 AND expires_at > $now;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$now", Format(now));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PeerPersistenceConflictException("Pairing session is missing, expired, or already completed.");
        }
    }

    public async Task<RecordInsertOutcome> AppendOriginRecordAsync(OriginRecordWrite write, CancellationToken cancellationToken = default)
    {
        Validate(write);
        var hash = SHA256.HashData(write.RecordBytes);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var existing = await ReadStoredOriginRecordAsync(connection, transaction, write.OriginPeerId, write.OriginStreamId, write.Sequence, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!Equal(existing, write, hash))
            {
                throw new PeerPersistenceConflictException("Origin sequence already contains different content.");
            }

            return RecordInsertOutcome.IdenticalRetry;
        }

        if (write.Provenance == RecordProvenance.LocalOrigin)
        {
            await EnsureNextLocalSequenceAsync(connection, transaction, write, cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO origin_records(origin_peer_id, origin_stream_id, sequence, subject_entity_id, adapter_instance_id,
                signal_id, record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality)
            VALUES ($origin, $stream, $sequence, $subject, $adapter, $signal, $bytes, $hash, $signature,
                $observed, $received, $from, $provenance, $quality);
            """;
        Bind(insert, write, hash);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (write.Provenance == RecordProvenance.LocalOrigin)
        {
            await using var advance = connection.CreateCommand();
            advance.Transaction = transaction;
            advance.CommandText = "UPDATE local_peer_identity SET next_sequence = $next WHERE singleton_id = 1;";
            advance.Parameters.AddWithValue("$next", ToInt64(write.Sequence + 1));
            await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Live.Publish(new StoredOriginRecord(
            write.OriginPeerId, write.OriginStreamId, write.Sequence, write.SubjectEntityId, write.AdapterInstanceId,
            write.SignalId, write.RecordBytes, hash, write.Signature, write.ObservedAt, write.ReceivedAt,
            write.ReceivedFromPeerId, write.Provenance, write.Quality));
        return RecordInsertOutcome.Inserted;
    }

    public async Task<IReadOnlyList<StoredOriginRecord>> ReadOriginRangeAsync(string originPeerId, string originStreamId,
        ulong firstSequence, int limit, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(originPeerId, nameof(originPeerId));
        EnsureIdentifier(originStreamId, nameof(originStreamId));
        ArgumentOutOfRangeException.ThrowIfZero(firstSequence);
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT origin_peer_id, origin_stream_id, sequence, subject_entity_id, adapter_instance_id, signal_id,
                record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality
            FROM origin_records WHERE origin_peer_id = $origin AND origin_stream_id = $stream AND sequence >= $first
            ORDER BY sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$origin", originPeerId);
        command.Parameters.AddWithValue("$stream", originStreamId);
        command.Parameters.AddWithValue("$first", ToInt64(firstSequence));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<StoredOriginRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadStoredOriginRecord(reader));
        return result;
    }

    public async Task<OriginRecordWindowPage> ReadOriginWindowAsync(OriginRecordWindowQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureUtc(query.From, nameof(query.From));
        EnsureUtc(query.ToExclusive, nameof(query.ToExclusive));
        if (query.ToExclusive <= query.From) throw new ArgumentException("Window end must follow its start.", nameof(query));
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.After is { } after) EnsureUtc(after.ObservedAt, nameof(query.After));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT origin_peer_id, origin_stream_id, sequence, subject_entity_id, adapter_instance_id, signal_id,
                record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality
            FROM origin_records
            WHERE observed_at >= $from AND observed_at < $to
                AND ($subject IS NULL OR subject_entity_id = $subject)
                AND ($signal IS NULL OR signal_id = $signal)
                AND ($origin IS NULL OR origin_peer_id = $origin)
                AND ($afterObserved IS NULL OR (observed_at, origin_peer_id, origin_stream_id, sequence) >
                    ($afterObserved, $afterOrigin, $afterStream, $afterSequence))
            ORDER BY observed_at, origin_peer_id, origin_stream_id, sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", Format(query.From));
        command.Parameters.AddWithValue("$to", Format(query.ToExclusive));
        command.Parameters.AddWithValue("$subject", Db(query.SubjectEntityId));
        command.Parameters.AddWithValue("$signal", Db(query.SignalId));
        command.Parameters.AddWithValue("$origin", Db(query.OriginPeerId));
        command.Parameters.AddWithValue("$afterObserved", query.After is { } a ? Format(a.ObservedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$afterOrigin", query.After?.OriginPeerId ?? string.Empty);
        command.Parameters.AddWithValue("$afterStream", query.After?.OriginStreamId ?? string.Empty);
        command.Parameters.AddWithValue("$afterSequence", query.After is { } b ? ToInt64(b.Sequence) : 0L);
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var records = new List<StoredOriginRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadStoredOriginRecord(reader));
        var truncated = records.Count > query.Limit;
        if (truncated) records.RemoveAt(records.Count - 1);
        return new OriginRecordWindowPage(records, truncated);
    }

    public async Task<OriginRecordWindowPage> ReadOriginStateAsOfAsync(DateTimeOffset asOf, DateTimeOffset notBefore,
        string? subjectEntityId, string? signalId, int limit, CancellationToken cancellationToken = default)
    {
        EnsureUtc(asOf, nameof(asOf));
        EnsureUtc(notBefore, nameof(notBefore));
        if (notBefore > asOf) throw new ArgumentException("Lookback start cannot follow the as-of time.", nameof(notBefore));
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY subject_entity_id, signal_id
                    ORDER BY observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id, sequence DESC) AS rank
                FROM origin_records
                WHERE observed_at <= $asOf AND observed_at >= $notBefore
                    AND ($subject IS NULL OR subject_entity_id = $subject)
                    AND ($signal IS NULL OR signal_id = $signal)
            )
            SELECT origin_peer_id, origin_stream_id, sequence, subject_entity_id, adapter_instance_id, signal_id,
                record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality
            FROM ranked WHERE rank = 1 ORDER BY subject_entity_id, signal_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$asOf", Format(asOf));
        command.Parameters.AddWithValue("$notBefore", Format(notBefore));
        command.Parameters.AddWithValue("$subject", Db(subjectEntityId));
        command.Parameters.AddWithValue("$signal", Db(signalId));
        command.Parameters.AddWithValue("$limit", limit + 1);
        var records = new List<StoredOriginRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadStoredOriginRecord(reader));
        var truncated = records.Count > limit;
        if (truncated) records.RemoveAt(records.Count - 1);
        return new OriginRecordWindowPage(records, truncated);
    }

    public async Task<IReadOnlyList<CurrentObservation>> ReadCurrentObservationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY subject_entity_id, signal_id
                    ORDER BY observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id, sequence DESC) AS rank
                FROM origin_records
            )
            SELECT subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence, record_bytes,
                observed_at, received_at, provenance, quality
            FROM ranked WHERE rank = 1 ORDER BY subject_entity_id, signal_id;
            """;
        var result = new List<CurrentObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CurrentObservation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ToUInt64(reader.GetInt64(4)), Bytes(reader, 5), Parse(reader.GetString(6)), Parse(reader.GetString(7)),
                (RecordProvenance)reader.GetInt32(8), reader.GetString(9)));
        }

        return result;
    }

    public async Task UpdateReplicationCursorAsync(ReplicationCursor cursor, IReadOnlyCollection<ReplicationGap> gaps,
        CancellationToken cancellationToken = default)
    {
        Validate(cursor, gaps);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO replication_cursors(remote_peer_id, origin_peer_id, origin_stream_id, accepted_through, fidelity, updated_at)
                VALUES ($remote, $origin, $stream, $accepted, $fidelity, $updated)
                ON CONFLICT(remote_peer_id, origin_peer_id, origin_stream_id) DO UPDATE SET
                    accepted_through = excluded.accepted_through,
                    fidelity = excluded.fidelity,
                    updated_at = excluded.updated_at
                WHERE excluded.accepted_through >= replication_cursors.accepted_through;
                """;
            Bind(upsert, cursor);
            if (await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new PeerPersistenceConflictException("Replication cursor cannot move backwards.");
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM replication_gaps WHERE remote_peer_id = $remote AND origin_peer_id = $origin AND origin_stream_id = $stream;";
            BindKey(delete, cursor.RemotePeerId, cursor.OriginPeerId, cursor.OriginStreamId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var gap in gaps.OrderBy(value => value.FirstSequence))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO replication_gaps(remote_peer_id, origin_peer_id, origin_stream_id, first_sequence, last_sequence) VALUES ($remote, $origin, $stream, $first, $last);";
            BindKey(insert, cursor.RemotePeerId, cursor.OriginPeerId, cursor.OriginStreamId);
            insert.Parameters.AddWithValue("$first", ToInt64(gap.FirstSequence));
            insert.Parameters.AddWithValue("$last", ToInt64(gap.LastSequence));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(ReplicationCursor? Cursor, IReadOnlyList<ReplicationGap> Gaps)> ReadReplicationCursorAsync(
        string remotePeerId, string originPeerId, string originStreamId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ReplicationCursor? cursor;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT remote_peer_id, origin_peer_id, origin_stream_id, accepted_through, fidelity, updated_at FROM replication_cursors WHERE remote_peer_id = $remote AND origin_peer_id = $origin AND origin_stream_id = $stream;";
            BindKey(command, remotePeerId, originPeerId, originStreamId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            cursor = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new ReplicationCursor(reader.GetString(0), reader.GetString(1), reader.GetString(2), ToUInt64(reader.GetInt64(3)), reader.GetString(4), Parse(reader.GetString(5)))
                : null;
        }

        if (cursor is null) return (null, Array.Empty<ReplicationGap>());
        var gaps = new List<ReplicationGap>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT first_sequence, last_sequence FROM replication_gaps WHERE remote_peer_id = $remote AND origin_peer_id = $origin AND origin_stream_id = $stream ORDER BY first_sequence;";
            BindKey(command, remotePeerId, originPeerId, originStreamId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) gaps.Add(new ReplicationGap(ToUInt64(reader.GetInt64(0)), ToUInt64(reader.GetInt64(1))));
        }

        return (cursor, gaps);
    }

    public async Task UpsertObservedEntityAsync(ObservedEntity entity, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(entity.EntityId, nameof(entity.EntityId)); EnsureIdentifier(entity.EntityKind, nameof(entity.EntityKind)); EnsureUtc(entity.CreatedAt, nameof(entity.CreatedAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO observed_entities(entity_id, entity_kind, created_at) VALUES ($id, $kind, $created) ON CONFLICT(entity_id) DO UPDATE SET entity_kind = excluded.entity_kind WHERE observed_entities.created_at = excluded.created_at;";
        command.Parameters.AddWithValue("$id", entity.EntityId); command.Parameters.AddWithValue("$kind", entity.EntityKind); command.Parameters.AddWithValue("$created", Format(entity.CreatedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new PeerPersistenceConflictException("Observed entity creation identity is immutable.");
    }

    public async Task AddIdentityClaimAsync(IdentityClaim claim, CancellationToken cancellationToken = default)
    {
        Validate(claim);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identity_claims(entity_id, claim_type, normalized_value, strength, observer_peer_id, evidence_kind, confidence, observed_at)
            VALUES ($entity, $type, $value, $strength, $observer, $evidence, $confidence, $observed)
            ON CONFLICT(entity_id, claim_type, normalized_value, observer_peer_id) DO UPDATE SET
                strength = excluded.strength, evidence_kind = excluded.evidence_kind,
                confidence = excluded.confidence, observed_at = excluded.observed_at;
            """;
        command.Parameters.AddWithValue("$entity", claim.EntityId); command.Parameters.AddWithValue("$type", claim.ClaimType);
        command.Parameters.AddWithValue("$value", claim.NormalizedValue); command.Parameters.AddWithValue("$strength", (int)claim.Strength);
        command.Parameters.AddWithValue("$observer", claim.ObserverPeerId); command.Parameters.AddWithValue("$evidence", (int)claim.EvidenceKind);
        command.Parameters.AddWithValue("$confidence", claim.Confidence); command.Parameters.AddWithValue("$observed", Format(claim.ObservedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IdentityClaim>> ReadIdentityClaimsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(entityId, nameof(entityId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_id, claim_type, normalized_value, strength, observer_peer_id, evidence_kind, confidence, observed_at FROM identity_claims WHERE entity_id = $entity ORDER BY strength DESC, confidence DESC, claim_type, normalized_value;";
        command.Parameters.AddWithValue("$entity", entityId);
        var result = new List<IdentityClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new IdentityClaim(reader.GetString(0), reader.GetString(1), reader.GetString(2), (IdentityStrength)reader.GetInt32(3), reader.GetString(4), (RelationshipEvidenceKind)reader.GetInt32(5), reader.GetDouble(6), Parse(reader.GetString(7))));
        return result;
    }

    public async Task UpsertRelationshipAsync(RelationshipObservation relationship, CancellationToken cancellationToken = default)
    {
        Validate(relationship);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO relationship_observations(relationship_id, source_entity_id, target_entity_id, relationship_kind,
                evidence_kind, observer_peer_id, confidence, valid_from, valid_to)
            VALUES ($id, $source, $target, $kind, $evidence, $observer, $confidence, $from, $to)
            ON CONFLICT(relationship_id) DO UPDATE SET evidence_kind = excluded.evidence_kind,
                confidence = excluded.confidence, valid_to = excluded.valid_to
            WHERE relationship_observations.source_entity_id = excluded.source_entity_id
              AND relationship_observations.target_entity_id = excluded.target_entity_id
              AND relationship_observations.relationship_kind = excluded.relationship_kind
              AND relationship_observations.observer_peer_id = excluded.observer_peer_id
              AND relationship_observations.valid_from = excluded.valid_from;
            """;
        command.Parameters.AddWithValue("$id", relationship.RelationshipId); command.Parameters.AddWithValue("$source", relationship.SourceEntityId);
        command.Parameters.AddWithValue("$target", relationship.TargetEntityId); command.Parameters.AddWithValue("$kind", (int)relationship.Kind);
        command.Parameters.AddWithValue("$evidence", (int)relationship.EvidenceKind); command.Parameters.AddWithValue("$observer", relationship.ObserverPeerId);
        command.Parameters.AddWithValue("$confidence", relationship.Confidence); command.Parameters.AddWithValue("$from", Format(relationship.ValidFrom));
        command.Parameters.AddWithValue("$to", Db(relationship.ValidTo));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new PeerPersistenceConflictException("Relationship identity cannot be rebound.");
    }

    public async Task<IReadOnlyList<RelationshipObservation>> ReadRelationshipsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(entityId, nameof(entityId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relationship_id, source_entity_id, target_entity_id, relationship_kind, evidence_kind, observer_peer_id, confidence, valid_from, valid_to FROM relationship_observations WHERE source_entity_id = $entity OR target_entity_id = $entity ORDER BY valid_from, relationship_id;";
        command.Parameters.AddWithValue("$entity", entityId);
        var result = new List<RelationshipObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new RelationshipObservation(reader.GetString(0), reader.GetString(1), reader.GetString(2), (RelationshipKind)reader.GetInt32(3), (RelationshipEvidenceKind)reader.GetInt32(4), reader.GetString(5), reader.GetDouble(6), Parse(reader.GetString(7)), NullableDate(reader, 8)));
        return result;
    }

    /// <summary>Relationships observed by the peer that have not been ended (no ValidTo), oldest first.</summary>
    public async Task<IReadOnlyList<RelationshipObservation>> ReadOpenRelationshipsByObserverAsync(string observerPeerId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(observerPeerId, nameof(observerPeerId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relationship_id, source_entity_id, target_entity_id, relationship_kind, evidence_kind, observer_peer_id, confidence, valid_from, valid_to FROM relationship_observations WHERE observer_peer_id = $observer AND valid_to IS NULL ORDER BY valid_from, relationship_id;";
        command.Parameters.AddWithValue("$observer", observerPeerId);
        var result = new List<RelationshipObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new RelationshipObservation(reader.GetString(0), reader.GetString(1), reader.GetString(2), (RelationshipKind)reader.GetInt32(3), (RelationshipEvidenceKind)reader.GetInt32(4), reader.GetString(5), reader.GetDouble(6), Parse(reader.GetString(7)), NullableDate(reader, 8)));
        return result;
    }

    public async Task UpsertAdapterInstanceAsync(AdapterInstance adapter, CancellationToken cancellationToken = default)
    {
        Validate(adapter);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO adapter_instances(adapter_instance_id, adapter_id, adapter_version, collector_peer_id, observed_entity_id, credential_reference, enabled)
            VALUES ($instance, $adapter, $version, $collector, $entity, $credential, $enabled)
            ON CONFLICT(adapter_instance_id) DO UPDATE SET adapter_id = excluded.adapter_id, adapter_version = excluded.adapter_version,
                collector_peer_id = excluded.collector_peer_id, observed_entity_id = excluded.observed_entity_id,
                credential_reference = excluded.credential_reference, enabled = excluded.enabled;
            """;
        command.Parameters.AddWithValue("$instance", adapter.AdapterInstanceId); command.Parameters.AddWithValue("$adapter", adapter.AdapterId);
        command.Parameters.AddWithValue("$version", adapter.AdapterVersion); command.Parameters.AddWithValue("$collector", adapter.CollectorPeerId);
        command.Parameters.AddWithValue("$entity", Db(adapter.ObservedEntityId)); command.Parameters.AddWithValue("$credential", Db(adapter.CredentialReference));
        command.Parameters.AddWithValue("$enabled", adapter.Enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdapterReplicationDescriptor?> ReadAdapterReplicationDescriptorAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(instanceId, nameof(instanceId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT adapter_instance_id, adapter_id, adapter_version, collector_peer_id, observed_entity_id, enabled FROM adapter_instances WHERE adapter_instance_id = $id;";
        command.Parameters.AddWithValue("$id", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AdapterReplicationDescriptor(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4), reader.GetInt32(5) == 1)
            : null;
    }

    public async Task RecordAdapterStatusAsync(AdapterStatus status, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(status.AdapterInstanceId, nameof(status.AdapterInstanceId)); EnsureIdentifier(status.State, nameof(status.State)); EnsureUtc(status.ObservedAt, nameof(status.ObservedAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO adapter_status(adapter_instance_id, observed_at, state, reason) VALUES ($id, $at, $state, $reason);";
        command.Parameters.AddWithValue("$id", status.AdapterInstanceId); command.Parameters.AddWithValue("$at", Format(status.ObservedAt));
        command.Parameters.AddWithValue("$state", status.State); command.Parameters.AddWithValue("$reason", Db(status.Reason));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdapterStatus?> ReadLatestAdapterStatusAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(instanceId, nameof(instanceId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT adapter_instance_id, state, reason, observed_at FROM adapter_status WHERE adapter_instance_id = $id ORDER BY observed_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", instanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AdapterStatus(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), Parse(reader.GetString(3)))
            : null;
    }

    public async Task UpsertCapabilityAsync(CapabilityCatalogEntry capability, CancellationToken cancellationToken = default)
    {
        Validate(capability);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO capability_catalog(capability_id, namespace, version, descriptor, updated_at) VALUES ($id, $namespace, $version, $descriptor, $updated) ON CONFLICT(capability_id) DO UPDATE SET namespace = excluded.namespace, version = excluded.version, descriptor = excluded.descriptor, updated_at = excluded.updated_at;";
        command.Parameters.AddWithValue("$id", capability.CapabilityId); command.Parameters.AddWithValue("$namespace", capability.Namespace);
        command.Parameters.AddWithValue("$version", capability.Version); command.Parameters.Add("$descriptor", SqliteType.Blob).Value = capability.Descriptor;
        command.Parameters.AddWithValue("$updated", Format(capability.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CapabilityCatalogEntry>> ReadCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT capability_id, namespace, version, descriptor, updated_at FROM capability_catalog ORDER BY namespace, capability_id;";
        var result = new List<CapabilityCatalogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new CapabilityCatalogEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), Bytes(reader, 3), Parse(reader.GetString(4))));
        return result;
    }

    public async Task AssignCollectorAsync(CollectorAssignment assignment, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(assignment.ObservedEntityId, nameof(assignment.ObservedEntityId)); EnsureIdentifier(assignment.AdapterInstanceId, nameof(assignment.AdapterInstanceId));
        EnsureIdentifier(assignment.CollectorPeerId, nameof(assignment.CollectorPeerId)); EnsureUtc(assignment.AssignedAt, nameof(assignment.AssignedAt));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        await using (var adapter = connection.CreateCommand())
        {
            adapter.Transaction = transaction;
            adapter.CommandText = "SELECT collector_peer_id, observed_entity_id FROM adapter_instances WHERE adapter_instance_id = $id;";
            adapter.Parameters.AddWithValue("$id", assignment.AdapterInstanceId);
            await using var reader = await adapter.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new ArgumentException("Collector assignment requires an existing adapter instance.", nameof(assignment));
            }

            if (!string.Equals(reader.GetString(0), assignment.CollectorPeerId, StringComparison.Ordinal) ||
                reader.IsDBNull(1) ||
                !string.Equals(reader.GetString(1), assignment.ObservedEntityId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Collector assignment must match the adapter's collector peer and observed entity.",
                    nameof(assignment));
            }
        }

        CollectorAssignment? current = null;
        await using (var readCurrent = connection.CreateCommand())
        {
            readCurrent.Transaction = transaction;
            readCurrent.CommandText = "SELECT observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at FROM collector_assignments WHERE observed_entity_id = $entity;";
            readCurrent.Parameters.AddWithValue("$entity", assignment.ObservedEntityId);
            await using var reader = await readCurrent.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                current = new CollectorAssignment(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Parse(reader.GetString(3)));
            }
        }

        if (current == assignment)
        {
            return;
        }

        if (current is not null)
        {
            if (assignment.AssignedAt <= current.AssignedAt)
            {
                throw new PeerPersistenceConflictException(
                    "Collector reassignment must be later than the current assignment.");
            }

            await using var closeHistory = connection.CreateCommand();
            closeHistory.Transaction = transaction;
            closeHistory.CommandText = "UPDATE collector_assignment_history SET released_at = $released WHERE observed_entity_id = $entity AND assigned_at = $assigned AND released_at IS NULL;";
            closeHistory.Parameters.AddWithValue("$released", Format(assignment.AssignedAt));
            closeHistory.Parameters.AddWithValue("$entity", current.ObservedEntityId);
            closeHistory.Parameters.AddWithValue("$assigned", Format(current.AssignedAt));
            if (await closeHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new PeerPersistenceConflictException("Current collector assignment history is inconsistent.");
            }
        }

        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = "INSERT INTO collector_assignment_history(observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at, released_at) VALUES ($entity, $adapter, $collector, $at, NULL);";
            history.Parameters.AddWithValue("$entity", assignment.ObservedEntityId);
            history.Parameters.AddWithValue("$adapter", assignment.AdapterInstanceId);
            history.Parameters.AddWithValue("$collector", assignment.CollectorPeerId);
            history.Parameters.AddWithValue("$at", Format(assignment.AssignedAt));
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO collector_assignments(observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at) VALUES ($entity, $adapter, $collector, $at) ON CONFLICT(observed_entity_id) DO UPDATE SET adapter_instance_id = excluded.adapter_instance_id, collector_peer_id = excluded.collector_peer_id, assigned_at = excluded.assigned_at;";
        command.Parameters.AddWithValue("$entity", assignment.ObservedEntityId); command.Parameters.AddWithValue("$adapter", assignment.AdapterInstanceId);
        command.Parameters.AddWithValue("$collector", assignment.CollectorPeerId); command.Parameters.AddWithValue("$at", Format(assignment.AssignedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CollectorAssignment?> ReadCollectorAssignmentAsync(string entityId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(entityId, nameof(entityId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at FROM collector_assignments WHERE observed_entity_id = $entity;";
        command.Parameters.AddWithValue("$entity", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CollectorAssignment(reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)))
            : null;
    }

    public async Task<IReadOnlyList<CollectorAssignmentPeriod>> ReadCollectorAssignmentHistoryAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(entityId, nameof(entityId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT observed_entity_id, adapter_instance_id, collector_peer_id, assigned_at, released_at FROM collector_assignment_history WHERE observed_entity_id = $entity ORDER BY assigned_at;";
        command.Parameters.AddWithValue("$entity", entityId);
        var result = new List<CollectorAssignmentPeriod>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CollectorAssignmentPeriod(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Parse(reader.GetString(3)),
                NullableDate(reader, 4)));
        }

        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout={busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureNextLocalSequenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        OriginRecordWrite write, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT peer_id, origin_stream_id, next_sequence FROM local_peer_identity WHERE singleton_id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != write.OriginPeerId ||
            reader.GetString(1) != write.OriginStreamId || ToUInt64(reader.GetInt64(2)) != write.Sequence)
        {
            throw new PeerPersistenceConflictException("Local origin sequence is not the next durable sequence.");
        }
    }

    private static async Task<StoredOriginRecord?> ReadStoredOriginRecordAsync(SqliteConnection connection, SqliteTransaction transaction,
        string originPeerId, string originStreamId, ulong sequence, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT origin_peer_id, origin_stream_id, sequence, subject_entity_id, adapter_instance_id, signal_id, record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality FROM origin_records WHERE origin_peer_id = $origin AND origin_stream_id = $stream AND sequence = $sequence;";
        command.Parameters.AddWithValue("$origin", originPeerId); command.Parameters.AddWithValue("$stream", originStreamId);
        command.Parameters.AddWithValue("$sequence", ToInt64(sequence));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadStoredOriginRecord(reader) : null;
    }

    private static StoredOriginRecord ReadStoredOriginRecord(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), ToUInt64(reader.GetInt64(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        Bytes(reader, 6), Bytes(reader, 7), Bytes(reader, 8), Parse(reader.GetString(9)), Parse(reader.GetString(10)),
        NullableString(reader, 11), (RecordProvenance)reader.GetInt32(12), reader.GetString(13));

    private static LocalPeerIdentity ReadLocalIdentity(SqliteDataReader reader) => new(
        reader.GetString(0), Bytes(reader, 1), Bytes(reader, 2), reader.GetString(3), reader.GetString(4),
        ToUInt64(reader.GetInt64(5)), Parse(reader.GetString(6)));

    private static void Bind(SqliteCommand command, TrustedPeer peer)
    {
        command.Parameters.AddWithValue("$peer", peer.PeerId); command.Parameters.Add("$key", SqliteType.Blob).Value = peer.PublicKeySpki;
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = peer.PublicKeyFingerprint; command.Parameters.AddWithValue("$name", Db(peer.DisplayName));
        command.Parameters.AddWithValue("$state", (int)peer.TrustState); command.Parameters.AddWithValue("$paired", Format(peer.PairedAt));
        command.Parameters.AddWithValue("$revoked", Db(peer.RevokedAt));
    }

    private static void Bind(SqliteCommand command, OriginRecordWrite write, byte[] hash)
    {
        command.Parameters.AddWithValue("$origin", write.OriginPeerId); command.Parameters.AddWithValue("$stream", write.OriginStreamId);
        command.Parameters.AddWithValue("$sequence", ToInt64(write.Sequence)); command.Parameters.AddWithValue("$subject", write.SubjectEntityId);
        command.Parameters.AddWithValue("$adapter", write.AdapterInstanceId); command.Parameters.AddWithValue("$signal", write.SignalId);
        command.Parameters.Add("$bytes", SqliteType.Blob).Value = write.RecordBytes; command.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
        command.Parameters.Add("$signature", SqliteType.Blob).Value = write.Signature; command.Parameters.AddWithValue("$observed", Format(write.ObservedAt));
        command.Parameters.AddWithValue("$received", Format(write.ReceivedAt)); command.Parameters.AddWithValue("$from", Db(write.ReceivedFromPeerId));
        command.Parameters.AddWithValue("$provenance", (int)write.Provenance); command.Parameters.AddWithValue("$quality", write.Quality);
    }

    private static void Bind(SqliteCommand command, ReplicationCursor cursor)
    {
        BindKey(command, cursor.RemotePeerId, cursor.OriginPeerId, cursor.OriginStreamId);
        command.Parameters.AddWithValue("$accepted", ToInt64(cursor.AcceptedThrough)); command.Parameters.AddWithValue("$fidelity", cursor.Fidelity);
        command.Parameters.AddWithValue("$updated", Format(cursor.UpdatedAt));
    }

    private static void BindKey(SqliteCommand command, string remotePeerId, string originPeerId, string originStreamId)
    {
        EnsureIdentifier(remotePeerId, nameof(remotePeerId)); EnsureIdentifier(originPeerId, nameof(originPeerId)); EnsureIdentifier(originStreamId, nameof(originStreamId));
        command.Parameters.AddWithValue("$remote", remotePeerId); command.Parameters.AddWithValue("$origin", originPeerId); command.Parameters.AddWithValue("$stream", originStreamId);
    }

    private static bool Equal(LocalPeerIdentity left, LocalPeerIdentity right) =>
        left.PeerId == right.PeerId && left.PublicKeySpki.AsSpan().SequenceEqual(right.PublicKeySpki) &&
        left.PublicKeyFingerprint.AsSpan().SequenceEqual(right.PublicKeyFingerprint) && left.KeyProviderReference == right.KeyProviderReference &&
        left.OriginStreamId == right.OriginStreamId && left.NextSequence == right.NextSequence && left.CreatedAt == right.CreatedAt;

    // The receive time of a replicated record is local bookkeeping, not part of the origin's signed content: when two
    // sessions deliver the same record concurrently, whichever commits first fixes it and the other is an identical retry.
    // A locally originated record has one writer, so its timestamp stays part of the comparison.
    private static bool ReceivedAtMatches(StoredOriginRecord stored, OriginRecordWrite write) =>
        write.Provenance == RecordProvenance.Replicated || stored.ReceivedAt == write.ReceivedAt;

    private static bool Equal(StoredOriginRecord stored, OriginRecordWrite write, byte[] hash) =>
        stored.OriginPeerId == write.OriginPeerId && stored.OriginStreamId == write.OriginStreamId && stored.Sequence == write.Sequence &&
        stored.SubjectEntityId == write.SubjectEntityId && stored.AdapterInstanceId == write.AdapterInstanceId && stored.SignalId == write.SignalId &&
        stored.RecordBytes.AsSpan().SequenceEqual(write.RecordBytes) && stored.ContentHash.AsSpan().SequenceEqual(hash) && stored.Signature.AsSpan().SequenceEqual(write.Signature) &&
        stored.ObservedAt == write.ObservedAt && ReceivedAtMatches(stored, write) && stored.ReceivedFromPeerId == write.ReceivedFromPeerId &&
        stored.Provenance == write.Provenance && stored.Quality == write.Quality;

    private static void Validate(LocalPeerIdentity identity)
    {
        EnsureIdentifier(identity.PeerId, nameof(identity.PeerId)); ValidatePublicIdentity(identity.PublicKeySpki, identity.PublicKeyFingerprint, nameof(identity));
        EnsureIdentifier(identity.KeyProviderReference, nameof(identity.KeyProviderReference));
        EnsureIdentifier(identity.OriginStreamId, nameof(identity.OriginStreamId)); if (identity.NextSequence == 0) throw new ArgumentOutOfRangeException(nameof(identity));
        EnsureUtc(identity.CreatedAt, nameof(identity.CreatedAt));
    }

    private static void Validate(TrustedPeer peer)
    {
        EnsureIdentifier(peer.PeerId, nameof(peer.PeerId)); ValidatePublicIdentity(peer.PublicKeySpki, peer.PublicKeyFingerprint, nameof(peer));
        EnsureUtc(peer.PairedAt, nameof(peer.PairedAt)); EnsureUtc(peer.RevokedAt, nameof(peer.RevokedAt));
        if (!Enum.IsDefined(peer.TrustState)) throw new ArgumentOutOfRangeException(nameof(peer));
        if (peer.TrustState == PeerTrustState.Active && peer.RevokedAt is not null || peer.TrustState == PeerTrustState.Revoked && peer.RevokedAt is null) throw new ArgumentException("Trust state and revocation time disagree.", nameof(peer));
    }

    private static void Validate(PairingSession session)
    {
        EnsureIdentifier(session.PairingSessionId, nameof(session.PairingSessionId)); EnsureIdentifier(session.RemotePeerId, nameof(session.RemotePeerId));
        EnsureUtc(session.CreatedAt, nameof(session.CreatedAt)); EnsureUtc(session.ExpiresAt, nameof(session.ExpiresAt));
        if (session.ExpiresAt <= session.CreatedAt) throw new ArgumentException("Pairing session must expire after creation.", nameof(session));
    }

    private static void Validate(OriginRecordWrite write)
    {
        EnsureIdentifier(write.OriginPeerId, nameof(write.OriginPeerId)); EnsureIdentifier(write.OriginStreamId, nameof(write.OriginStreamId));
        if (write.Sequence == 0 || write.Sequence == ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(write));
        EnsureIdentifier(write.SubjectEntityId, nameof(write.SubjectEntityId)); EnsureIdentifier(write.AdapterInstanceId, nameof(write.AdapterInstanceId));
        EnsureIdentifier(write.SignalId, nameof(write.SignalId)); EnsureBytes(write.RecordBytes, nameof(write.RecordBytes));
        if (write.Signature is null || write.Signature.Length != P256SignatureSize) throw new ArgumentException($"Signature must be exactly {P256SignatureSize} bytes in P1363 encoding.", nameof(write));
        EnsureUtc(write.ObservedAt, nameof(write.ObservedAt)); EnsureUtc(write.ReceivedAt, nameof(write.ReceivedAt)); EnsureIdentifier(write.Quality, nameof(write.Quality));
        if (!Enum.IsDefined(write.Provenance)) throw new ArgumentOutOfRangeException(nameof(write));
        if (write.Provenance == RecordProvenance.LocalOrigin && write.ReceivedFromPeerId is not null || write.Provenance == RecordProvenance.Replicated && string.IsNullOrWhiteSpace(write.ReceivedFromPeerId)) throw new ArgumentException("Record provenance and receiving peer disagree.", nameof(write));
    }

    private static void Validate(ReplicationCursor cursor, IReadOnlyCollection<ReplicationGap> gaps)
    {
        EnsureIdentifier(cursor.RemotePeerId, nameof(cursor.RemotePeerId)); EnsureIdentifier(cursor.OriginPeerId, nameof(cursor.OriginPeerId)); EnsureIdentifier(cursor.OriginStreamId, nameof(cursor.OriginStreamId)); EnsureIdentifier(cursor.Fidelity, nameof(cursor.Fidelity)); EnsureUtc(cursor.UpdatedAt, nameof(cursor.UpdatedAt));
        ulong previousLast = 0;
        foreach (var gap in gaps.OrderBy(value => value.FirstSequence))
        {
            if (gap.FirstSequence == 0 || gap.LastSequence < gap.FirstSequence || gap.FirstSequence <= cursor.AcceptedThrough || gap.FirstSequence <= previousLast) throw new ArgumentException("Replication gaps must be ordered, non-overlapping, and beyond the contiguous cursor.", nameof(gaps));
            previousLast = gap.LastSequence;
        }
    }

    private static void Validate(IdentityClaim claim)
    {
        EnsureIdentifier(claim.EntityId, nameof(claim.EntityId)); EnsureIdentifier(claim.ClaimType, nameof(claim.ClaimType)); EnsureIdentifier(claim.NormalizedValue, nameof(claim.NormalizedValue)); EnsureIdentifier(claim.ObserverPeerId, nameof(claim.ObserverPeerId)); EnsureConfidence(claim.Confidence); EnsureUtc(claim.ObservedAt, nameof(claim.ObservedAt));
        if (!Enum.IsDefined(claim.Strength) || !Enum.IsDefined(claim.EvidenceKind)) throw new ArgumentOutOfRangeException(nameof(claim));
        if (claim.Strength == IdentityStrength.Strong && ObservedIdentityMatcher.IsNetworkAddressClaim(claim.ClaimType))
        {
            throw new ArgumentException("An IP or network address cannot be a strong observed-device identity.", nameof(claim));
        }
    }

    private static void Validate(RelationshipObservation relationship)
    {
        EnsureIdentifier(relationship.RelationshipId, nameof(relationship.RelationshipId)); EnsureIdentifier(relationship.SourceEntityId, nameof(relationship.SourceEntityId)); EnsureIdentifier(relationship.TargetEntityId, nameof(relationship.TargetEntityId)); EnsureIdentifier(relationship.ObserverPeerId, nameof(relationship.ObserverPeerId)); EnsureConfidence(relationship.Confidence); EnsureUtc(relationship.ValidFrom, nameof(relationship.ValidFrom)); EnsureUtc(relationship.ValidTo, nameof(relationship.ValidTo));
        if (relationship.ValidTo <= relationship.ValidFrom) throw new ArgumentException("Relationship end must follow its start.", nameof(relationship));
        if (!Enum.IsDefined(relationship.Kind) || !Enum.IsDefined(relationship.EvidenceKind)) throw new ArgumentOutOfRangeException(nameof(relationship));
    }

    private static void Validate(AdapterInstance adapter)
    {
        EnsureIdentifier(adapter.AdapterInstanceId, nameof(adapter.AdapterInstanceId)); EnsureIdentifier(adapter.AdapterId, nameof(adapter.AdapterId)); EnsureIdentifier(adapter.AdapterVersion, nameof(adapter.AdapterVersion)); EnsureIdentifier(adapter.CollectorPeerId, nameof(adapter.CollectorPeerId));
    }

    private static void Validate(CapabilityCatalogEntry capability)
    {
        EnsureIdentifier(capability.CapabilityId, nameof(capability.CapabilityId)); EnsureIdentifier(capability.Namespace, nameof(capability.Namespace)); EnsureIdentifier(capability.Version, nameof(capability.Version)); EnsureBytes(capability.Descriptor, nameof(capability.Descriptor)); EnsureUtc(capability.UpdatedAt, nameof(capability.UpdatedAt));
    }

    private static void EnsureIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("Value is required and must not exceed 512 characters.", name);
    }

    private static void EnsureBytes(byte[] value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0) throw new ArgumentException("Value must not be empty.", name);
    }

    private static void ValidatePublicIdentity(byte[] publicKeySpki, byte[] fingerprint, string name)
    {
        EnsureBytes(publicKeySpki, nameof(publicKeySpki));
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length != PublicKeyFingerprintSize ||
            !CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(publicKeySpki)))
        {
            throw new ArgumentException("Public-key fingerprint must be the exact SHA-256 digest of the key.", name);
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out var bytesRead);
            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (bytesRead != publicKeySpki.Length ||
                parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new ArgumentException("Public key must be a canonical ECDSA P-256 SubjectPublicKeyInfo value.", name);
            }
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("Public key must be a valid ECDSA P-256 SubjectPublicKeyInfo value.", name, exception);
        }
    }

    private static void EnsureConfidence(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static void EnsureUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must use UTC offset zero.", name);
    }

    private static void EnsureUtc(DateTimeOffset? value, string name)
    {
        if (value is not null) EnsureUtc(value.Value, name);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static object Db(DateTimeOffset? value) => value is null ? DBNull.Value : Format(value.Value);
    private static byte[] Bytes(SqliteDataReader reader, int ordinal) => (byte[])reader.GetValue(ordinal);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static long ToInt64(ulong value) => checked((long)value);
    private static ulong ToUInt64(long value) => checked((ulong)value);
}
