using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

// The newest record per (subject, signal), kept up to date on every write so "what is the value now" never scans
// history. Before schema v7 every screen refresh and alert pass ranked the whole origin_records table: 1.6 s at 500k
// rows on a 4-core server and up to 27 s on the Pixel (foundation review, milestone 0).
public sealed partial class PeerStore
{
    private const int LatestObservationsSchemaVersion = 7;

    private const string LatestObservationsSchema = """
        CREATE TABLE IF NOT EXISTS latest_observations (
            subject_entity_id TEXT NOT NULL,
            signal_id TEXT NOT NULL,
            origin_peer_id TEXT NOT NULL,
            origin_stream_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            record_bytes BLOB NOT NULL,
            observed_at TEXT NOT NULL,
            received_at TEXT NOT NULL,
            provenance INTEGER NOT NULL,
            quality TEXT NOT NULL,
            PRIMARY KEY(subject_entity_id, signal_id)
        ) WITHOUT ROWID;
        """;

    // Same order as the ranking it replaces: observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id,
    // sequence DESC. Timestamps are fixed-width UTC "O" strings, so text comparison is time order.
    private const string NewerThanLatest = """
        excluded.observed_at > latest_observations.observed_at
        OR (excluded.observed_at = latest_observations.observed_at AND (
            excluded.received_at > latest_observations.received_at
            OR (excluded.received_at = latest_observations.received_at AND (
                excluded.origin_peer_id < latest_observations.origin_peer_id
                OR (excluded.origin_peer_id = latest_observations.origin_peer_id AND (
                    excluded.origin_stream_id < latest_observations.origin_stream_id
                    OR (excluded.origin_stream_id = latest_observations.origin_stream_id
                        AND excluded.sequence > latest_observations.sequence)))))))
        """;

    private const string UpsertLatestObservationSql = $"""
        INSERT INTO latest_observations(subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence,
            record_bytes, observed_at, received_at, provenance, quality)
        VALUES ($subject, $signal, $origin, $stream, $sequence, $bytes, $observed, $received, $provenance, $quality)
        ON CONFLICT(subject_entity_id, signal_id) DO UPDATE SET
            origin_peer_id = excluded.origin_peer_id, origin_stream_id = excluded.origin_stream_id,
            sequence = excluded.sequence, record_bytes = excluded.record_bytes, observed_at = excluded.observed_at,
            received_at = excluded.received_at, provenance = excluded.provenance, quality = excluded.quality
        WHERE {NewerThanLatest};
        """;

    // The one-time v7 backfill: the newest record of every (subject, signal) that has no row yet.
    private const string FillMissingLatestSql = """
        INSERT INTO latest_observations(subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence,
            record_bytes, observed_at, received_at, provenance, quality)
        SELECT subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence, record_bytes, observed_at,
            received_at, provenance, quality
        FROM (
            SELECT *, ROW_NUMBER() OVER (
                PARTITION BY subject_entity_id, signal_id
                ORDER BY observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id, sequence DESC) AS rank
            FROM origin_records r
            WHERE NOT EXISTS (SELECT 1 FROM latest_observations l
                WHERE l.subject_entity_id = r.subject_entity_id AND l.signal_id = r.signal_id))
        WHERE rank = 1;
        """;

    // After retention: rows whose record was deleted are recomputed from what is left, exactly as the old full ranking
    // would have answered. Only the affected keys are touched, each through ix_origin_records_subject_signal.
    private const string RepairLatestAfterPruneSql = """
        CREATE TEMP TABLE IF NOT EXISTS latest_repair(subject_entity_id TEXT NOT NULL, signal_id TEXT NOT NULL,
            PRIMARY KEY(subject_entity_id, signal_id)) WITHOUT ROWID;
        DELETE FROM latest_repair;
        INSERT INTO latest_repair SELECT subject_entity_id, signal_id FROM latest_observations l WHERE NOT EXISTS (
            SELECT 1 FROM origin_records r WHERE r.origin_peer_id = l.origin_peer_id
                AND r.origin_stream_id = l.origin_stream_id AND r.sequence = l.sequence);
        DELETE FROM latest_observations WHERE EXISTS (SELECT 1 FROM latest_repair k
            WHERE k.subject_entity_id = latest_observations.subject_entity_id AND k.signal_id = latest_observations.signal_id);
        INSERT INTO latest_observations(subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence,
            record_bytes, observed_at, received_at, provenance, quality)
        SELECT r.subject_entity_id, r.signal_id, r.origin_peer_id, r.origin_stream_id, r.sequence, r.record_bytes,
            r.observed_at, r.received_at, r.provenance, r.quality
        FROM latest_repair k JOIN origin_records r ON r.rowid = (
            SELECT n.rowid FROM origin_records n
            WHERE n.subject_entity_id = k.subject_entity_id AND n.signal_id = k.signal_id
            ORDER BY n.observed_at DESC, n.received_at DESC, n.origin_peer_id, n.origin_stream_id, n.sequence DESC
            LIMIT 1);
        DROP TABLE latest_repair;
        """;

    /// <summary>
    /// Set when this instance's <see cref="InitializeAsync"/> ran the one-time v7 backfill: how many latest-value rows it
    /// built and how long it took. Null when the database was already on v7. Hosts log it so the phone cost is visible.
    /// </summary>
    public LatestObservationsBackfill? LatestBackfill { get; private set; }

    private async Task MigrateLatestObservationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = LatestObservationsSchema;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", LatestObservationsSchemaVersion);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            return;
        }

        // One pass over existing history (about 1.2 s at 500k rows on a server), then never again.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var fill = connection.CreateCommand();
        fill.Transaction = transaction;
        fill.CommandText = "DELETE FROM latest_observations;" + FillMissingLatestSql;
        var rows = await fill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        fill.CommandText = "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES ($version, $now);";
        fill.Parameters.AddWithValue("$version", LatestObservationsSchemaVersion);
        fill.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await fill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        LatestBackfill = new LatestObservationsBackfill(rows, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    private static IEnumerable<StoredOriginRecord> NewestPerSignal(IEnumerable<StoredOriginRecord> records) =>
        records.GroupBy(record => (record.SubjectEntityId, record.SignalId))
            .Select(group => group.Aggregate((best, next) => IsNewer(next, best) ? next : best));

    // C# twin of NewerThanLatest, used only to pick one candidate per signal inside a batch; the SQL guard still decides.
    private static bool IsNewer(StoredOriginRecord candidate, StoredOriginRecord current)
    {
        var order = string.CompareOrdinal(Format(candidate.ObservedAt), Format(current.ObservedAt));
        if (order == 0) order = string.CompareOrdinal(Format(candidate.ReceivedAt), Format(current.ReceivedAt));
        if (order == 0) order = -string.CompareOrdinal(candidate.OriginPeerId, current.OriginPeerId);
        if (order == 0) order = -string.CompareOrdinal(candidate.OriginStreamId, current.OriginStreamId);
        if (order == 0) order = candidate.Sequence.CompareTo(current.Sequence);
        return order > 0;
    }

    private static async Task UpsertLatestObservationAsync(SqliteConnection connection, SqliteTransaction transaction,
        StoredOriginRecord write, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertLatestObservationSql;
        command.Parameters.AddWithValue("$subject", write.SubjectEntityId);
        command.Parameters.AddWithValue("$signal", write.SignalId);
        command.Parameters.AddWithValue("$origin", write.OriginPeerId);
        command.Parameters.AddWithValue("$stream", write.OriginStreamId);
        command.Parameters.AddWithValue("$sequence", ToInt64(write.Sequence));
        command.Parameters.AddWithValue("$bytes", write.RecordBytes);
        command.Parameters.AddWithValue("$observed", Format(write.ObservedAt));
        command.Parameters.AddWithValue("$received", Format(write.ReceivedAt));
        command.Parameters.AddWithValue("$provenance", (int)write.Provenance);
        command.Parameters.AddWithValue("$quality", write.Quality);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RepairLatestAfterPruneAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RepairLatestAfterPruneSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Result of the one-time schema v7 backfill of the latest-value table.</summary>
public sealed record LatestObservationsBackfill(int Rows, TimeSpan Duration);
