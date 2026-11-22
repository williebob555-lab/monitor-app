using System.Data;
using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

// Tiered retention storage: hourly rollups, pinned incidents, raw pruning and pruned-range tombstones.
public sealed partial class PeerStore
{
    private const string RollupColumns = """
        origin_peer_id, subject_entity_id, signal_id, hour_start, sample_count, numeric_count, min_value, max_value,
        sum_value, unit, first_observed_at, last_observed_at, worst_quality, valid_count, estimated_count,
        unavailable_count, denied_count, error_count, other_quality_count
        """;

    // Hour key = the first 13 characters of the fixed-width UTC "O" timestamp (yyyy-MM-ddTHH).
    private const string HourStartExpression = "(substr(observed_at, 1, 13) || ':00:00.0000000+00:00')";

    private const string PruneFlagsSql = """
        WITH ordered AS (
            SELECT r.origin_peer_id AS o, r.origin_stream_id AS s, r.sequence AS q, r.observed_at AS obs, r.quality AS quality,
                LAG(r.quality) OVER (PARTITION BY r.origin_peer_id, r.subject_entity_id, r.signal_id
                    ORDER BY r.observed_at, r.origin_stream_id, r.sequence) AS prev
            FROM origin_records r WHERE r.observed_at < $rawCutoff),
        flagged AS (
            SELECT o, s, q,
                EXISTS(SELECT 1 FROM pinned_incidents p WHERE ordered.obs >= p.from_at AND ordered.obs < p.to_at) AS pinned,
                (obs >= $eventCutoff AND (prev IS NULL OR prev <> quality)) AS transition,
                ($local IS NOT NULL AND o = $local AND obs >= $hardCutoff AND q > COALESCE(
                    (SELECT MIN(COALESCE(c.accepted_through, 0))
                     FROM trusted_peers t LEFT JOIN replication_cursors c
                       ON c.remote_peer_id = t.peer_id AND c.origin_peer_id = ordered.o AND c.origin_stream_id = ordered.s
                     WHERE t.trust_state = 1), 9223372036854775807)) AS lagging
            FROM ordered)
        """;

    /// <summary>Hours (UTC) holding raw records whose rollup is missing, or (at or after <paramref name="staleCheckFrom"/>) whose count differs from the rollup.</summary>
    public async Task<IReadOnlyList<DateTimeOffset>> ReadRollupWorkHoursAsync(
        DateTimeOffset completeBefore, DateTimeOffset staleCheckFrom, CancellationToken cancellationToken = default)
    {
        EnsureUtc(completeBefore, nameof(completeBefore));
        EnsureUtc(staleCheckFrom, nameof(staleCheckFrom));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT x.hour_start FROM (
                SELECT origin_peer_id AS o, subject_entity_id AS s, signal_id AS g, {HourStartExpression} AS hour_start, COUNT(*) AS n
                FROM origin_records WHERE observed_at < $before GROUP BY o, s, g, hour_start) x
            LEFT JOIN observation_rollups_hourly h
                ON h.origin_peer_id = x.o AND h.subject_entity_id = x.s AND h.signal_id = x.g AND h.hour_start = x.hour_start
            WHERE h.sample_count IS NULL OR (x.hour_start >= $staleFrom AND h.sample_count <> x.n)
            ORDER BY x.hour_start;
            """;
        command.Parameters.AddWithValue("$before", Format(completeBefore));
        command.Parameters.AddWithValue("$staleFrom", Format(staleCheckFrom));
        var hours = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) hours.Add(Parse(reader.GetString(0)));
        return hours;
    }

    /// <summary>Writes rollups. With <paramref name="replace"/> an existing row is recomputed; otherwise an existing row is kept (finalised hours).</summary>
    public async Task<int> UpsertHourlyRollupsAsync(IReadOnlyCollection<HourlyRollup> rollups, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var written = 0;
        foreach (var rollup in rollups)
        {
            EnsureIdentifier(rollup.OriginPeerId, nameof(rollups));
            EnsureIdentifier(rollup.SubjectEntityId, nameof(rollups));
            EnsureIdentifier(rollup.SignalId, nameof(rollups));
            EnsureUtc(rollup.HourStart, nameof(rollups));
            EnsureUtc(rollup.FirstObservedAt, nameof(rollups));
            EnsureUtc(rollup.LastObservedAt, nameof(rollups));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT OR {(replace ? "REPLACE" : "IGNORE")} INTO observation_rollups_hourly({RollupColumns})
                VALUES ($o, $s, $g, $hour, $n, $numeric, $min, $max, $sum, $unit, $first, $last, $worst, $valid, $est, $unavail, $denied, $err, $other);
                """;
            command.Parameters.AddWithValue("$o", rollup.OriginPeerId);
            command.Parameters.AddWithValue("$s", rollup.SubjectEntityId);
            command.Parameters.AddWithValue("$g", rollup.SignalId);
            command.Parameters.AddWithValue("$hour", Format(rollup.HourStart));
            command.Parameters.AddWithValue("$n", rollup.SampleCount);
            command.Parameters.AddWithValue("$numeric", rollup.NumericCount);
            command.Parameters.AddWithValue("$min", rollup.MinValue is { } min ? min : DBNull.Value);
            command.Parameters.AddWithValue("$max", rollup.MaxValue is { } max ? max : DBNull.Value);
            command.Parameters.AddWithValue("$sum", rollup.SumValue is { } sum ? sum : DBNull.Value);
            command.Parameters.AddWithValue("$unit", Db(rollup.Unit));
            command.Parameters.AddWithValue("$first", Format(rollup.FirstObservedAt));
            command.Parameters.AddWithValue("$last", Format(rollup.LastObservedAt));
            command.Parameters.AddWithValue("$worst", rollup.WorstQuality);
            command.Parameters.AddWithValue("$valid", rollup.ValidCount);
            command.Parameters.AddWithValue("$est", rollup.EstimatedCount);
            command.Parameters.AddWithValue("$unavail", rollup.UnavailableCount);
            command.Parameters.AddWithValue("$denied", rollup.DeniedCount);
            command.Parameters.AddWithValue("$err", rollup.ErrorCount);
            command.Parameters.AddWithValue("$other", rollup.OtherQualityCount);
            written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    public async Task<HourlyRollupPage> ReadHourlyRollupsAsync(HourlyRollupQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureUtc(query.From, nameof(query));
        EnsureUtc(query.ToExclusive, nameof(query));
        if (query.ToExclusive <= query.From) throw new ArgumentException("Window end must follow its start.", nameof(query));
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RollupColumns} FROM observation_rollups_hourly
            WHERE hour_start >= $from AND hour_start < $to
                AND ($subject IS NULL OR subject_entity_id = $subject)
                AND ($signal IS NULL OR signal_id = $signal)
                AND ($origin IS NULL OR origin_peer_id = $origin)
                AND ($afterHour IS NULL OR (hour_start, origin_peer_id, subject_entity_id, signal_id) >
                    ($afterHour, $afterOrigin, $afterSubject, $afterSignal))
            ORDER BY hour_start, origin_peer_id, subject_entity_id, signal_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", Format(query.From));
        command.Parameters.AddWithValue("$to", Format(query.ToExclusive));
        command.Parameters.AddWithValue("$subject", Db(query.SubjectEntityId));
        command.Parameters.AddWithValue("$signal", Db(query.SignalId));
        command.Parameters.AddWithValue("$origin", Db(query.OriginPeerId));
        command.Parameters.AddWithValue("$afterHour", query.After is { } a ? Format(a.HourStart) : DBNull.Value);
        command.Parameters.AddWithValue("$afterOrigin", query.After?.OriginPeerId ?? string.Empty);
        command.Parameters.AddWithValue("$afterSubject", query.After?.SubjectEntityId ?? string.Empty);
        command.Parameters.AddWithValue("$afterSignal", query.After?.SignalId ?? string.Empty);
        command.Parameters.AddWithValue("$limit", query.Limit + 1);
        var rollups = new List<HourlyRollup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rollups.Add(new HourlyRollup(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3)),
                reader.GetInt32(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                Parse(reader.GetString(10)), Parse(reader.GetString(11)), reader.GetString(12),
                reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18)));
        }

        var truncated = rollups.Count > query.Limit;
        if (truncated) rollups.RemoveAt(rollups.Count - 1);
        return new HourlyRollupPage(rollups, truncated);
    }

    public async Task<int> PruneHourlyRollupsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        EnsureUtc(cutoff, nameof(cutoff));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM observation_rollups_hourly WHERE hour_start < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", Format(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PinIncidentAsync(PinnedIncident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        EnsureIdentifier(incident.IncidentId, nameof(incident));
        EnsureIdentifier(incident.Label, nameof(incident));
        EnsureUtc(incident.From, nameof(incident));
        EnsureUtc(incident.To, nameof(incident));
        EnsureUtc(incident.CreatedAt, nameof(incident));
        if (incident.To <= incident.From) throw new ArgumentException("Pinned window end must follow its start.", nameof(incident));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO pinned_incidents(incident_id, label, from_at, to_at, created_at) VALUES ($id, $label, $from, $to, $created);";
        command.Parameters.AddWithValue("$id", incident.IncidentId);
        command.Parameters.AddWithValue("$label", incident.Label);
        command.Parameters.AddWithValue("$from", Format(incident.From));
        command.Parameters.AddWithValue("$to", Format(incident.To));
        command.Parameters.AddWithValue("$created", Format(incident.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unpinning only makes the window prunable again; raw records already pruned stay pruned.</summary>
    public async Task<bool> UnpinIncidentAsync(string incidentId, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(incidentId, nameof(incidentId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pinned_incidents WHERE incident_id = $id;";
        command.Parameters.AddWithValue("$id", incidentId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<PinnedIncident>> ReadPinnedIncidentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT incident_id, label, from_at, to_at, created_at FROM pinned_incidents ORDER BY from_at, incident_id;";
        var result = new List<PinnedIncident>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PinnedIncident(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)), Parse(reader.GetString(3)), Parse(reader.GetString(4))));
        }

        return result;
    }

    /// <summary>
    /// Deletes raw records observed before the cutoff unless they are in a pinned window, are transition records still
    /// inside event retention, or are locally originated records some active trusted peer has not acknowledged yet
    /// (until the replication hard cap). Deleted sequence runs are recorded as pruned ranges in the same transaction.
    /// </summary>
    public async Task<RawPruneResult> PruneRawRecordsAsync(RawPruneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(request.RawCutoff, nameof(request));
        EnsureUtc(request.EventCutoff, nameof(request));
        EnsureUtc(request.ReplicationHardCutoff, nameof(request));
        EnsureUtc(request.PrunedAt, nameof(request));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            if (sql.Contains("$rawCutoff", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue("$rawCutoff", Format(request.RawCutoff));
                command.Parameters.AddWithValue("$eventCutoff", Format(request.EventCutoff));
                command.Parameters.AddWithValue("$hardCutoff", Format(request.ReplicationHardCutoff));
                command.Parameters.AddWithValue("$local", Db(request.LocalPeerId));
            }

            return command;
        }

        await using (var create = Command("""
            CREATE TEMP TABLE IF NOT EXISTS prune_candidates(
                origin_peer_id TEXT NOT NULL, origin_stream_id TEXT NOT NULL, sequence INTEGER NOT NULL,
                PRIMARY KEY(origin_peer_id, origin_stream_id, sequence)) WITHOUT ROWID;
            DELETE FROM prune_candidates;
            """))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var select = Command(PruneFlagsSql + "INSERT INTO prune_candidates SELECT o, s, q FROM flagged WHERE NOT pinned AND NOT transition AND NOT lagging;"))
        {
            await select.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var runs = new List<PrunedOriginRange>();
        await using (var islands = Command("""
            SELECT origin_peer_id, origin_stream_id, MIN(sequence), MAX(sequence) FROM (
                SELECT origin_peer_id, origin_stream_id, sequence,
                    sequence - ROW_NUMBER() OVER (PARTITION BY origin_peer_id, origin_stream_id ORDER BY sequence) AS grp
                FROM prune_candidates)
            GROUP BY origin_peer_id, origin_stream_id, grp ORDER BY origin_peer_id, origin_stream_id, MIN(sequence);
            """))
        await using (var reader = await islands.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runs.Add(new PrunedOriginRange(reader.GetString(0), reader.GetString(1), ToUInt64(reader.GetInt64(2)), ToUInt64(reader.GetInt64(3)), request.PrunedAt));
            }
        }

        int deleted;
        await using (var delete = Command("""
            DELETE FROM origin_records WHERE EXISTS (
                SELECT 1 FROM prune_candidates c WHERE c.origin_peer_id = origin_records.origin_peer_id
                    AND c.origin_stream_id = origin_records.origin_stream_id AND c.sequence = origin_records.sequence);
            """))
        {
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var run in runs)
        {
            await MergePrunedRangeAsync(connection, transaction, run, cancellationToken).ConfigureAwait(false);
        }

        int pinned, transitions, awaiting;
        await using (var counts = Command(PruneFlagsSql + """
            SELECT COALESCE(SUM(pinned), 0), COALESCE(SUM(NOT pinned AND transition), 0),
                COALESCE(SUM(NOT pinned AND NOT transition AND lagging), 0) FROM flagged;
            """))
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            pinned = reader.GetInt32(0);
            transitions = reader.GetInt32(1);
            awaiting = reader.GetInt32(2);
        }

        await using (var drop = Command("DROP TABLE prune_candidates;"))
        {
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RawPruneResult(deleted, pinned, transitions, awaiting, runs.Count);
    }

    private static async Task MergePrunedRangeAsync(SqliteConnection connection, SqliteTransaction transaction, PrunedOriginRange run, CancellationToken cancellationToken)
    {
        var first = run.FirstSequence;
        var last = run.LastSequence;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT first_sequence, last_sequence FROM pruned_origin_ranges
                WHERE origin_peer_id = $o AND origin_stream_id = $s AND first_sequence <= $lastPlus AND last_sequence >= $firstMinus;
                """;
            select.Parameters.AddWithValue("$o", run.OriginPeerId);
            select.Parameters.AddWithValue("$s", run.OriginStreamId);
            select.Parameters.AddWithValue("$lastPlus", ToInt64(last) + 1);
            select.Parameters.AddWithValue("$firstMinus", ToInt64(first) - 1);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                first = Math.Min(first, ToUInt64(reader.GetInt64(0)));
                last = Math.Max(last, ToUInt64(reader.GetInt64(1)));
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM pruned_origin_ranges WHERE origin_peer_id = $o AND origin_stream_id = $s AND first_sequence >= $first AND last_sequence <= $last;";
            delete.Parameters.AddWithValue("$o", run.OriginPeerId);
            delete.Parameters.AddWithValue("$s", run.OriginStreamId);
            delete.Parameters.AddWithValue("$first", ToInt64(first));
            delete.Parameters.AddWithValue("$last", ToInt64(last));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO pruned_origin_ranges(origin_peer_id, origin_stream_id, first_sequence, last_sequence, pruned_at) VALUES ($o, $s, $first, $last, $at);";
        insert.Parameters.AddWithValue("$o", run.OriginPeerId);
        insert.Parameters.AddWithValue("$s", run.OriginStreamId);
        insert.Parameters.AddWithValue("$first", ToInt64(first));
        insert.Parameters.AddWithValue("$last", ToInt64(last));
        insert.Parameters.AddWithValue("$at", Format(run.PrunedAt));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pruned range containing the sequence, or null when it was never pruned.</summary>
    public async Task<PrunedOriginRange?> ReadPrunedRangeContainingAsync(string originPeerId, string originStreamId, ulong sequence, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(originPeerId, nameof(originPeerId));
        EnsureIdentifier(originStreamId, nameof(originStreamId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_sequence, last_sequence, pruned_at FROM pruned_origin_ranges WHERE origin_peer_id = $o AND origin_stream_id = $s AND first_sequence <= $q AND last_sequence >= $q;";
        command.Parameters.AddWithValue("$o", originPeerId);
        command.Parameters.AddWithValue("$s", originStreamId);
        command.Parameters.AddWithValue("$q", ToInt64(sequence));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PrunedOriginRange(originPeerId, originStreamId, ToUInt64(reader.GetInt64(0)), ToUInt64(reader.GetInt64(1)), Parse(reader.GetString(2)))
            : null;
    }

    /// <summary>The first pruned range that starts after the sequence, or null.</summary>
    public async Task<PrunedOriginRange?> ReadNextPrunedRangeAfterAsync(string originPeerId, string originStreamId, ulong sequence, CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(originPeerId, nameof(originPeerId));
        EnsureIdentifier(originStreamId, nameof(originStreamId));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_sequence, last_sequence, pruned_at FROM pruned_origin_ranges WHERE origin_peer_id = $o AND origin_stream_id = $s AND first_sequence > $q ORDER BY first_sequence LIMIT 1;";
        command.Parameters.AddWithValue("$o", originPeerId);
        command.Parameters.AddWithValue("$s", originStreamId);
        command.Parameters.AddWithValue("$q", ToInt64(sequence));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PrunedOriginRange(originPeerId, originStreamId, ToUInt64(reader.GetInt64(0)), ToUInt64(reader.GetInt64(1)), Parse(reader.GetString(2)))
            : null;
    }
}
