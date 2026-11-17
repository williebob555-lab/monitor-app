using Google.Protobuf;
using Microsoft.Data.Sqlite;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public sealed class SqliteTelemetrySpool : IAsyncDisposable
{
    private readonly TelemetrySpoolOptions options;
    private readonly string connectionString;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private SqliteTelemetrySpool(TelemetrySpoolOptions options)
    {
        this.options = options;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public static async Task<SqliteTelemetrySpool> OpenAsync(
        TelemetrySpoolOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var spool = new SqliteTelemetrySpool(options);
        await spool.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return spool;
    }

    public async Task<TelemetryEnvelope> EnqueueAsync(
        TelemetryEnvelope unsequencedEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unsequencedEnvelope);
        if (unsequencedEnvelope.Sequence != 0)
        {
            throw new ArgumentException(
                "The spool assigns telemetry sequence numbers; input sequence must be zero.",
                nameof(unsequencedEnvelope));
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var sequencedEnvelope = unsequencedEnvelope.Clone();
            sequencedEnvelope.Sequence = state.NextSequence;

            var validation = ContractValidator.Validate(sequencedEnvelope);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    string.Join(" ", validation.Errors),
                    nameof(unsequencedEnvelope));
            }

            var payload = sequencedEnvelope.ToByteArray();
            if (state.PendingCount >= options.MaximumPendingCount ||
                state.PendingBytes > options.MaximumPendingBytes - payload.LongLength)
            {
                throw new SpoolCapacityExceededException();
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO pending_telemetry(sequence, envelope, payload_size) " +
                    "VALUES ($sequence, $envelope, $payload_size);";
                insert.Parameters.AddWithValue("$sequence", checked((long)sequencedEnvelope.Sequence));
                insert.Parameters.Add("$envelope", SqliteType.Blob).Value = payload;
                insert.Parameters.AddWithValue("$payload_size", payload.LongLength);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE spool_state SET next_sequence = $next WHERE id = 1;";
                update.Parameters.AddWithValue(
                    "$next",
                    checked((long)(sequencedEnvelope.Sequence + 1)));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return sequencedEnvelope;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<TelemetryEnvelope>> ReadBatchAsync(
        int maximumCount,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT envelope, payload_size FROM pending_telemetry " +
            "ORDER BY sequence LIMIT $maximum_count;";
        command.Parameters.AddWithValue("$maximum_count", maximumCount);

        var envelopes = new List<TelemetryEnvelope>(maximumCount);
        var usedBytes = 0L;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payloadSize = reader.GetInt64(1);
            if (envelopes.Count == 0 && payloadSize > maximumBytes)
            {
                throw new InvalidOperationException(
                    "The oldest telemetry envelope exceeds the controller batch-size limit.");
            }

            if (envelopes.Count > 0 && usedBytes + payloadSize > maximumBytes)
            {
                break;
            }

            var payload = (byte[])reader.GetValue(0);
            envelopes.Add(TelemetryEnvelope.Parser.ParseFrom(payload));
            usedBytes += payloadSize;
        }

        return envelopes;
    }

    public async Task AcknowledgeAsync(
        ulong acceptedThroughSequence,
        CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            var state = await ReadStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (acceptedThroughSequence < state.AcceptedThroughSequence)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (acceptedThroughSequence >= state.NextSequence)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(acceptedThroughSequence),
                    "An acknowledgement cannot include a sequence the agent has not created.");
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM pending_telemetry WHERE sequence <= $sequence;";
                delete.Parameters.AddWithValue("$sequence", checked((long)acceptedThroughSequence));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE spool_state SET accepted_through_sequence = $sequence WHERE id = 1;";
                update.Parameters.AddWithValue("$sequence", checked((long)acceptedThroughSequence));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<TelemetrySpoolState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS spool_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                next_sequence INTEGER NOT NULL CHECK (next_sequence > 0),
                accepted_through_sequence INTEGER NOT NULL CHECK (accepted_through_sequence >= 0)
            );
            INSERT OR IGNORE INTO spool_state(id, next_sequence, accepted_through_sequence)
            VALUES (1, 1, 0);
            CREATE TABLE IF NOT EXISTS pending_telemetry (
                sequence INTEGER PRIMARY KEY CHECK (sequence > 0),
                envelope BLOB NOT NULL,
                payload_size INTEGER NOT NULL CHECK (payload_size > 0)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<TelemetrySpoolState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.next_sequence,
                   s.accepted_through_sequence,
                   COALESCE(MIN(p.sequence), s.next_sequence),
                   COUNT(p.sequence),
                   COALESCE(SUM(p.payload_size), 0)
            FROM spool_state AS s
            LEFT JOIN pending_telemetry AS p
            WHERE s.id = 1
            GROUP BY s.next_sequence, s.accepted_through_sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Telemetry spool state is missing.");
        }

        return new TelemetrySpoolState(
            checked((ulong)reader.GetInt64(0)),
            checked((ulong)reader.GetInt64(1)),
            checked((ulong)reader.GetInt64(2)),
            reader.GetInt32(3),
            reader.GetInt64(4));
    }
}
