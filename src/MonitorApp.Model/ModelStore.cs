using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MonitorApp.Model;

public sealed class ModelValidationException(string message) : Exception(message);

/// <summary>
/// The local model store: an append-only change log in its own SQLite file (model.db), separate from
/// the monitoring database. Every view of the model is rebuilt by replaying the log
/// (docs/ARCHITECTURE-MODEL.md 11). Writes are validated against the state they apply to and are atomic per batch.
/// </summary>
public sealed class ModelStore : IAsyncDisposable
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<ModelChangeRecord> log = [];
    private int changeCount;
    private readonly TimeProvider time;
    private bool loaded;

    /// <summary>
    /// The actual world now, rebuilt only after a change. Replaying the whole log on every read made each Things refresh and
    /// every bridge pass (one read per monitored subject) cost more as the log grew.
    /// </summary>
    private SystemModel? current;

    public ModelStore(string databasePath, ModelLibrary? library = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        Library = library ?? ModelLibrary.Starter;
        time = timeProvider ?? TimeProvider.System;
    }

    public ModelLibrary Library { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE IF NOT EXISTS model_schema (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS model_changes (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        change_id TEXT NOT NULL UNIQUE,
                        recorded_at TEXT NOT NULL,
                        layer_id TEXT NULL,
                        origin TEXT NOT NULL,
                        payload TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT MAX(version) FROM model_schema;";
                var current = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (current is long existing && existing > SchemaVersion)
                    throw new InvalidOperationException($"model.db schema v{existing} is newer than this build (v{SchemaVersion}).");
                version.CommandText = "INSERT OR IGNORE INTO model_schema(version, applied_at) VALUES ($v, $at);";
                version.Parameters.AddWithValue("$v", SchemaVersion);
                version.Parameters.AddWithValue("$at", Format(time.GetUtcNow()));
                await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            log.Clear();
            current = null;
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT sequence, change_id, recorded_at, layer_id, origin, payload FROM model_changes ORDER BY sequence;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var change = JsonSerializer.Deserialize<ModelChange>(reader.GetString(5), Json)
                    ?? throw new InvalidDataException($"Model change {reader.GetInt64(0)} is empty.");
                log.Add(new ModelChangeRecord(reader.GetInt64(0), reader.GetString(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), change));
            }
            loaded = true;
            Volatile.Write(ref changeCount, log.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The model now (or as of <paramref name="asOf"/>), seen as the actual world or with one design layer on top.</summary>
    public async Task<SystemModel> LoadAsync(DateTimeOffset? asOf = null, string? layerId = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (asOf is null && layerId is null)
            {
                return current ??= Build(null, null);
            }

            return Build(layerId, asOf);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>How many changes the in-memory log holds (diagnostics: a count that keeps rising means passes keep appending).</summary>
    public int ChangeCount => Volatile.Read(ref changeCount);

    /// <summary>The full change log, oldest first: the raw material for history views and event lists.</summary>
    public async Task<IReadOnlyList<ModelChangeRecord>> ReadChangesAsync(long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return log.Where(r => r.Sequence > afterSequence).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Validates and records changes as one batch, in the actual world (<paramref name="layerId"/> null) or in a design layer.
    /// Either every change applies or none is stored.
    /// </summary>
    public async Task<SystemModel> AppendAsync(IEnumerable<ModelChange> changes, string origin, string? layerId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var batch = changes.ToList();
        if (batch.Count == 0) throw new ArgumentException("Nothing to append.", nameof(changes));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var model = Build(layerId, null);
            var now = time.GetUtcNow();
            var pending = new List<ModelChangeRecord>(batch.Count);
            var next = (log.Count == 0 ? 0 : log[^1].Sequence) + 1;
            foreach (var change in batch)
            {
                var record = new ModelChangeRecord(next++, Guid.NewGuid().ToString("N"), now, layerId, origin, change);
                if (model.Apply(record) is { } error)
                    throw new ModelValidationException(error);
                pending.Add(record);
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var stored = new List<ModelChangeRecord>(pending.Count);
            foreach (var record in pending)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO model_changes(change_id, recorded_at, layer_id, origin, payload) VALUES ($id, $at, $layer, $origin, $payload) RETURNING sequence;";
                insert.Parameters.AddWithValue("$id", record.ChangeId);
                insert.Parameters.AddWithValue("$at", Format(record.RecordedAt));
                insert.Parameters.AddWithValue("$layer", (object?)record.LayerId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$origin", record.Origin);
                insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record.Change, Json));
                var sequence = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                stored.Add(record with { Sequence = sequence });
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            log.AddRange(stored);
            Volatile.Write(ref changeCount, log.Count);
            current = null;
            return model;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<SystemModel> AppendAsync(ModelChange change, string origin, string? layerId = null, CancellationToken cancellationToken = default) =>
        AppendAsync([change], origin, layerId, cancellationToken);

    /// <summary>
    /// Makes an object proposed in a design real: records, in the actual world, the object with the same id,
    /// its kind, name, properties and ports as the design has them, and the given lifecycle state.
    /// </summary>
    public async Task<SystemModel> RealizeAsync(string objectId, string layerId, string origin, Lifecycle lifecycle = Lifecycle.Owned, CancellationToken cancellationToken = default)
    {
        var design = await LoadAsync(null, layerId, cancellationToken).ConfigureAwait(false);
        var proposed = design.Find(objectId) ?? throw new ModelValidationException($"Object '{objectId}' does not exist in design '{layerId}'.");
        if (proposed.CreatedInLayer != layerId) throw new ModelValidationException($"'{objectId}' was not proposed in design '{layerId}'.");
        if (lifecycle < Lifecycle.Ordered) throw new ModelValidationException("Realizing means the object was at least ordered.");

        var changes = new List<ModelChange> { new ObjectCreated(objectId, proposed.Name, proposed.KindId, lifecycle) };
        changes.AddRange(proposed.Ports.Select(p => (ModelChange)new PortAdded(objectId, p)));
        changes.AddRange(proposed.Properties.Select(p => (ModelChange)new PropertyAsserted(objectId, p.Key, p.Value, p.Evidence, p.Scope)));
        changes.AddRange(proposed.Tags.Select(t => (ModelChange)new ObjectTagged(objectId, t)));
        return await AppendAsync(changes, origin, null, cancellationToken).ConfigureAwait(false);
    }

    private SystemModel Build(string? layerId, DateTimeOffset? asOf)
    {
        var model = new SystemModel(Library, layerId);
        foreach (var record in log)
        {
            if (asOf is { } cutoff && record.RecordedAt > cutoff) break;
            model.Apply(record);
        }
        return model;
    }

    private void EnsureLoaded()
    {
        if (!loaded) throw new InvalidOperationException("Call InitializeAsync first.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
