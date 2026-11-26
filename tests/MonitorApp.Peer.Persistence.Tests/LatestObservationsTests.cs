using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Persistence.Tests;

/// <summary>
/// The latest-value table must answer exactly what the old full-history ranking answered: newest observed_at, then
/// newest received_at, then lowest origin peer, lowest stream, highest sequence.
/// </summary>
public sealed class LatestObservationsTests : IDisposable
{
    private const string RankAll = """
        WITH ranked AS (
            SELECT *, ROW_NUMBER() OVER (
                PARTITION BY subject_entity_id, signal_id
                ORDER BY observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id, sequence DESC) AS rank
            FROM origin_records)
        SELECT subject_entity_id, signal_id, origin_peer_id, origin_stream_id, sequence
        FROM ranked WHERE rank = 1 ORDER BY subject_entity_id, signal_id;
        """;

    private static readonly byte[] Signature = CreateSignature();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MonitorApp.Peer.Persistence.Tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private string DatabasePath => Path.Combine(directory, "peer.db");

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task MatchesFullRankingForOutOfOrderArrivalsTiesAndSeveralOrigins()
    {
        var store = await CreateStoreAsync();
        var random = new Random(7);
        var origins = new[] { "peer-b", "peer-c", "peer-d" };
        var next = origins.ToDictionary(origin => origin, _ => 1UL);
        var writes = new List<OriginRecordWrite>();
        for (var i = 0; i < 600; i++)
        {
            var origin = origins[random.Next(origins.Length)];
            // Few distinct times so equal observed_at and received_at happen often.
            var observed = now.AddSeconds(random.Next(20));
            var received = observed.AddSeconds(random.Next(3));
            writes.Add(Record(origin, next[origin]++, $"host-{random.Next(3)}", $"signal-{random.Next(4)}", observed, received));
        }

        // Deliver in shuffled batches, as several sessions would.
        foreach (var batch in writes.OrderBy(_ => random.Next()).Chunk(37))
        {
            await store.AppendOriginRecordsAsync(batch);
        }

        await AssertMatchesRankingAsync(store);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task AnOlderReadingArrivingLaterDoesNotReplaceTheCurrentValue()
    {
        var store = await CreateStoreAsync();
        await store.AppendOriginRecordAsync(Record("peer-b", 2, "host-b", "cpu", now.AddMinutes(1), now.AddMinutes(1)));
        await store.AppendOriginRecordAsync(Record("peer-b", 1, "host-b", "cpu", now, now.AddMinutes(2)));

        var current = Assert.Single(await store.ReadCurrentObservationsAsync());
        Assert.Equal(2UL, current.Sequence);
        Assert.Equal(now.AddMinutes(1), current.ObservedAt);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task AVersion6DatabaseIsBackfilledOnceWhenOpened()
    {
        var store = await CreateStoreAsync();
        for (ulong sequence = 1; sequence <= 50; sequence++)
        {
            await store.AppendOriginRecordAsync(Record("peer-b", sequence, $"host-{sequence % 3}", $"signal-{sequence % 5}",
                now.AddSeconds(sequence), now.AddSeconds(sequence)));
        }

        // Reduce the file to what 0.1.37 holds: no latest table, schema version 6.
        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE latest_observations; DELETE FROM schema_migrations WHERE version >= 7;";
            await downgrade.ExecuteNonQueryAsync();
        }

        var migrated = await CreateStoreAsync();
        Assert.Equal(15, (await migrated.ReadCurrentObservationsAsync()).Count);
        await AssertMatchesRankingAsync(migrated);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task RetentionFallsBackToTheNewestRemainingRecordOrDropsTheSignal()
    {
        var store = await CreateStoreAsync();
        // cpu: an old record that stays (pinned) and newer ones that are pruned. disk: everything is pruned.
        await store.AppendOriginRecordAsync(Record("peer-b", 1, "host-b", "cpu", now, now));
        await store.AppendOriginRecordAsync(Record("peer-b", 2, "host-b", "cpu", now.AddHours(2), now.AddHours(2)));
        await store.AppendOriginRecordAsync(Record("peer-b", 3, "host-b", "cpu", now.AddHours(3), now.AddHours(3)));
        await store.AppendOriginRecordAsync(Record("peer-b", 4, "host-b", "disk", now.AddHours(3), now.AddHours(3)));
        await store.PinIncidentAsync(new PinnedIncident("keep-first", "keep", now.AddMinutes(-1), now.AddMinutes(1), now));

        var result = await store.PruneRawRecordsAsync(new RawPruneRequest(
            RawCutoff: now.AddDays(1), EventCutoff: now.AddYears(10), ReplicationHardCutoff: now.AddDays(1),
            LocalPeerId: "peer-a", PrunedAt: now.AddDays(1)));

        Assert.Equal(3, result.Deleted);
        var current = Assert.Single(await store.ReadCurrentObservationsAsync());
        Assert.Equal(("cpu", 1UL), (current.SignalId, current.Sequence));
        await AssertMatchesRankingAsync(store);
    }

    [Theory]
    [Trait("Category", "SQLite")]
    [InlineData(25, 0, null)]
    [InlineData(25, 8, null)]
    [InlineData(25, 8, "host-1")]
    [InlineData(10, 8, null)]
    public async Task StateAsOfAnswersLikeTheRankingWhetherAsOfIsNowOrInThePast(int asOfSeconds, int lookbackStartSeconds, string? subject)
    {
        var store = await CreateStoreAsync();
        var random = new Random(11);
        for (ulong sequence = 1; sequence <= 300; sequence++)
        {
            var observed = now.AddSeconds(random.Next(20));
            await store.AppendOriginRecordAsync(Record("peer-b", sequence, $"host-{random.Next(3)}", $"signal-{random.Next(6)}", observed, observed));
        }

        var asOf = now.AddSeconds(asOfSeconds);
        var notBefore = now.AddSeconds(lookbackStartSeconds);
        var page = await store.ReadOriginStateAsOfAsync(asOf, notBefore, subject, null, 1000);

        var expected = new List<(string, string, ulong)>();
        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH ranked AS (
                    SELECT *, ROW_NUMBER() OVER (
                        PARTITION BY subject_entity_id, signal_id
                        ORDER BY observed_at DESC, received_at DESC, origin_peer_id, origin_stream_id, sequence DESC) AS rank
                    FROM origin_records
                    WHERE observed_at <= $asOf AND observed_at >= $notBefore AND ($subject IS NULL OR subject_entity_id = $subject))
                SELECT subject_entity_id, signal_id, sequence FROM ranked WHERE rank = 1 ORDER BY subject_entity_id, signal_id;
                """;
            command.Parameters.AddWithValue("$asOf", asOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$notBefore", notBefore.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                expected.Add((reader.GetString(0), reader.GetString(1), (ulong)reader.GetInt64(2)));
            }
        }

        Assert.NotEmpty(expected);
        Assert.False(page.Truncated);
        Assert.Equal(expected, page.Records.Select(record => (record.SubjectEntityId, record.SignalId, record.Sequence)).ToList());
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReadsDoNotWaitForAnOpenWriteTransaction()
    {
        var store = await CreateStoreAsync();
        await store.AppendOriginRecordAsync(Record("peer-b", 1, "host-b", "cpu", now, now));

        // A sync batch in progress: another connection in this process holds an open write transaction on
        // origin_records. It opens the way every store did before (shared cache), as another store instance would.
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, Cache = SqliteCacheMode.Shared,
        }.ToString());
        await writer.OpenAsync();
        await using var transaction = writer.BeginTransaction(deferred: false);
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO origin_records SELECT origin_peer_id, origin_stream_id, 2, subject_entity_id, adapter_instance_id,
                    signal_id, record_bytes, content_hash, signature, observed_at, received_at, received_from_peer_id, provenance, quality
                FROM origin_records WHERE sequence = 1;
                """;
            await insert.ExecuteNonQueryAsync();
        }

        // Under shared cache this read failed with "database table is locked" after the 30 s command timeout.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var state = await store.ReadOriginStateAsOfAsync(now.AddHours(1), now.AddHours(-1), null, null, 10).WaitAsync(TimeSpan.FromSeconds(10));
        var range = await store.ReadOriginRangeAsync("peer-b", "origin-peer-b", 1, 10).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"reads took {stopwatch.Elapsed}");
        Assert.Equal(1UL, Assert.Single(state.Records).Sequence);
        Assert.Equal([1UL], range.Select(record => record.Sequence));
        await transaction.RollbackAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task AssertMatchesRankingAsync(PeerStore store)
    {
        var expected = new List<(string, string, string, string, ulong)>();
        await using (var connection = Open())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = RankAll;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                expected.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), (ulong)reader.GetInt64(4)));
            }
        }

        var actual = (await store.ReadCurrentObservationsAsync())
            .Select(value => (value.SubjectEntityId, value.SignalId, value.OriginPeerId, value.OriginStreamId, value.Sequence))
            .ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    private async Task<PeerStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new PeerStore(PeerStoreOptions.Create(DatabasePath));
        await store.InitializeAsync();
        return store;
    }

    private SqliteConnection Open() =>
        new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

    private static OriginRecordWrite Record(string origin, ulong sequence, string subject, string signal,
        DateTimeOffset observedAt, DateTimeOffset receivedAt)
    {
        var bytes = BitConverter.GetBytes(sequence);
        return new OriginRecordWrite(origin, "origin-" + origin, sequence, subject, "system", signal, bytes, Signature,
            observedAt, receivedAt, origin, RecordProvenance.Replicated, "fresh");
    }

    private static byte[] CreateSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.SignData([1], HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
