using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Persistence;

public sealed record TelemetryNodeView(
    string NodeId,
    string AgentId,
    string DisplayName,
    Platform Platform,
    ulong AcceptedThroughSequence,
    DateTimeOffset? LastIngestedAt);

public sealed record CurrentMetricView(
    string NodeId,
    string AgentId,
    string MetricId,
    string ComponentId,
    MetricUnit Unit,
    ObservationQuality Quality,
    object? Value,
    DateTimeOffset ObservedAt,
    DateTimeOffset IngestedAt,
    string CollectorId,
    ulong Sequence);

public sealed class TelemetryStore
{
    private const int MaximumOutstandingChallenges = 4;
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan challengeLifetime;

    public TelemetryStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        TimeSpan? challengeLifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.challengeLifetime = challengeLifetime ?? TimeSpan.FromMinutes(2);
        if (this.challengeLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(challengeLifetime));
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_session_challenges (
                challenge_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                session_id TEXT NOT NULL UNIQUE,
                proof_challenge BLOB NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL,
                FOREIGN KEY(agent_id) REFERENCES nodes(agent_id)
            );
            CREATE TABLE IF NOT EXISTS agent_stream_state (
                agent_id TEXT PRIMARY KEY,
                accepted_through_sequence INTEGER NOT NULL CHECK (accepted_through_sequence >= 0),
                boot_id TEXT NOT NULL,
                platform INTEGER NOT NULL,
                agent_version TEXT NOT NULL,
                capabilities TEXT NOT NULL,
                last_ingested_at TEXT NULL,
                FOREIGN KEY(agent_id) REFERENCES nodes(agent_id)
            );
            CREATE TABLE IF NOT EXISTS telemetry_envelopes (
                agent_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                boot_id TEXT NOT NULL,
                observed_at TEXT NOT NULL,
                ingested_at TEXT NOT NULL,
                collector_id TEXT NOT NULL,
                submission BLOB NOT NULL,
                envelope BLOB NOT NULL,
                PRIMARY KEY(agent_id, sequence),
                FOREIGN KEY(agent_id) REFERENCES nodes(agent_id)
            );
            CREATE INDEX IF NOT EXISTS ix_telemetry_agent_observed
                ON telemetry_envelopes(agent_id, observed_at, sequence);
            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (2, $now);
            """;
        command.Parameters.AddWithValue("$now", Format(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentSessionChallenge> CreateSessionChallengeAsync(
        string agentId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(agentId, "Agent id");
        EnsureIdentifier(sessionId, "Session id");
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + challengeLifetime;
        var challengeId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var proofChallenge = RandomNumberGenerator.GetBytes(EnrollmentContractRules.ProofChallengeSize);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT COUNT(*) FROM nodes WHERE agent_id = $agent AND revoked_at IS NULL;";
            verify.Parameters.AddWithValue("$agent", agentId);
            if (Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            {
                throw new TelemetryFailureException(TelemetryFailureReason.Unauthorized, "Agent identity is not enrolled.");
            }
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*) FROM agent_session_challenges
                WHERE agent_id = $agent AND consumed_at IS NULL AND expires_at > $now;
                """;
            count.Parameters.AddWithValue("$agent", agentId);
            count.Parameters.AddWithValue("$now", Format(now));
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) >= MaximumOutstandingChallenges)
            {
                throw new TelemetryFailureException(TelemetryFailureReason.Unauthorized, "Too many outstanding authentication challenges.");
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO agent_session_challenges(
                    challenge_id, agent_id, session_id, proof_challenge,
                    created_at, expires_at)
                VALUES ($id, $agent, $session, $challenge, $created, $expires);
                """;
            insert.Parameters.AddWithValue("$id", challengeId);
            insert.Parameters.AddWithValue("$agent", agentId);
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$challenge", proofChallenge);
            insert.Parameters.AddWithValue("$created", Format(now));
            insert.Parameters.AddWithValue("$expires", Format(expiresAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AgentSessionChallenge
        {
            AgentId = agentId,
            SessionId = sessionId,
            ChallengeId = challengeId,
            ProofChallenge = ByteString.CopyFrom(proofChallenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(now),
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };
    }

    public async Task VerifySessionProofAsync(
        AgentSessionChallenge challenge,
        AgentSessionProof proof,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(proof);
        if (!validation.IsValid ||
            proof.AgentId != challenge.AgentId ||
            proof.SessionId != challenge.SessionId ||
            proof.ChallengeId != challenge.ChallengeId)
        {
            throw new TelemetryFailureException(TelemetryFailureReason.Unauthorized, "Agent authentication failed.");
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        byte[] storedChallenge;
        byte[] publicKey;
        DateTimeOffset expiresAt;
        string storedSession;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT c.proof_challenge, c.expires_at, c.session_id, n.public_key_spki
                FROM agent_session_challenges AS c
                JOIN nodes AS n ON n.agent_id = c.agent_id
                WHERE c.challenge_id = $id AND c.agent_id = $agent
                  AND c.consumed_at IS NULL AND n.revoked_at IS NULL;
                """;
            select.Parameters.AddWithValue("$id", proof.ChallengeId);
            select.Parameters.AddWithValue("$agent", proof.AgentId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new TelemetryFailureException(TelemetryFailureReason.Replay, "Agent authentication failed.");
            }

            storedChallenge = (byte[])reader.GetValue(0);
            expiresAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            storedSession = reader.GetString(2);
            publicKey = (byte[])reader.GetValue(3);
        }

        if (expiresAt <= now || storedSession != proof.SessionId ||
            !CryptographicOperations.FixedTimeEquals(storedChallenge, challenge.ProofChallenge.Span))
        {
            throw new TelemetryFailureException(TelemetryFailureReason.Unauthorized, "Agent authentication failed.");
        }

        var payload = ProofPayload.CreateAgentSession(
            proof.AgentId,
            proof.SessionId,
            proof.ChallengeId,
            storedChallenge);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var read);
        if (read != publicKey.Length || !key.VerifyData(
                payload,
                proof.ProofSignature.Span,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new TelemetryFailureException(TelemetryFailureReason.Unauthorized, "Agent authentication failed.");
        }

        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE agent_session_challenges SET consumed_at = $now
            WHERE challenge_id = $id AND agent_id = $agent AND session_id = $session
              AND consumed_at IS NULL AND expires_at > $now;
            """;
        consume.Parameters.AddWithValue("$now", Format(now));
        consume.Parameters.AddWithValue("$id", proof.ChallengeId);
        consume.Parameters.AddWithValue("$agent", proof.AgentId);
        consume.Parameters.AddWithValue("$session", proof.SessionId);
        if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new TelemetryFailureException(TelemetryFailureReason.Replay, "Agent authentication failed.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ulong> GetAcceptedThroughAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT accepted_through_sequence FROM agent_stream_state WHERE agent_id = $agent;";
        command.Parameters.AddWithValue("$agent", agentId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : checked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    public async Task<ulong> IngestAsync(
        AgentHello hello,
        TelemetryBatch batch,
        CancellationToken cancellationToken = default)
    {
        var validation = ContractValidator.Validate(batch);
        if (!validation.IsValid)
        {
            throw new TelemetryFailureException(TelemetryFailureReason.InvalidMessage, string.Join(" ", validation.Errors));
        }

        if (batch.Envelopes.Any(envelope =>
                envelope.AgentId != hello.AgentId || envelope.IngestedAt is not null))
        {
            throw new TelemetryFailureException(TelemetryFailureReason.InvalidMessage, "Telemetry identity or ingestion timestamp is invalid.");
        }

        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var accepted = await ReadAcceptedThroughAsync(connection, transaction, hello.AgentId, cancellationToken).ConfigureAwait(false);

        foreach (var submitted in batch.Envelopes)
        {
            var submission = submitted.ToByteArray();
            if (submitted.Sequence <= accepted)
            {
                await EnsureIdenticalReplayAsync(connection, transaction, hello.AgentId, submitted.Sequence, submission, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (submitted.Sequence != accepted + 1)
            {
                throw new TelemetryFailureException(TelemetryFailureReason.SequenceConflict, "Telemetry sequence is not contiguous with durable controller state.");
            }

            var stored = submitted.Clone();
            stored.IngestedAt = Timestamp.FromDateTimeOffset(now);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO telemetry_envelopes(
                    agent_id, sequence, boot_id, observed_at, ingested_at,
                    collector_id, submission, envelope)
                VALUES ($agent, $sequence, $boot, $observed, $ingested,
                        $collector, $submission, $envelope);
                """;
            insert.Parameters.AddWithValue("$agent", hello.AgentId);
            insert.Parameters.AddWithValue("$sequence", checked((long)submitted.Sequence));
            insert.Parameters.AddWithValue("$boot", submitted.BootId);
            insert.Parameters.AddWithValue("$observed", Format(submitted.ObservedAt.ToDateTimeOffset()));
            insert.Parameters.AddWithValue("$ingested", Format(now));
            insert.Parameters.AddWithValue("$collector", submitted.CollectorId);
            insert.Parameters.Add("$submission", SqliteType.Blob).Value = submission;
            insert.Parameters.Add("$envelope", SqliteType.Blob).Value = stored.ToByteArray();
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            accepted = submitted.Sequence;
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                INSERT INTO agent_stream_state(
                    agent_id, accepted_through_sequence, boot_id, platform,
                    agent_version, capabilities, last_ingested_at)
                VALUES ($agent, $accepted, $boot, $platform, $version, $capabilities, $ingested)
                ON CONFLICT(agent_id) DO UPDATE SET
                    accepted_through_sequence = excluded.accepted_through_sequence,
                    boot_id = excluded.boot_id,
                    platform = excluded.platform,
                    agent_version = excluded.agent_version,
                    capabilities = excluded.capabilities,
                    last_ingested_at = excluded.last_ingested_at;
                """;
            update.Parameters.AddWithValue("$agent", hello.AgentId);
            update.Parameters.AddWithValue("$accepted", checked((long)accepted));
            update.Parameters.AddWithValue("$boot", hello.BootId);
            update.Parameters.AddWithValue("$platform", (int)hello.Platform);
            update.Parameters.AddWithValue("$version", hello.AgentVersion);
            update.Parameters.AddWithValue("$capabilities", string.Join(',', hello.Capabilities.Select(capability => $"{capability.Id}:{(int)capability.State}")));
            update.Parameters.AddWithValue("$ingested", Format(now));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return accepted;
    }

    public async Task<IReadOnlyList<TelemetryNodeView>> ReadInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id, n.agent_id, n.display_name, n.platform,
                   COALESCE(s.accepted_through_sequence, 0), s.last_ingested_at
            FROM nodes AS n
            LEFT JOIN agent_stream_state AS s ON s.agent_id = n.agent_id
            WHERE n.revoked_at IS NULL ORDER BY n.display_name, n.agent_id;
            """;
        var result = new List<TelemetryNodeView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TelemetryNodeView(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (Platform)reader.GetInt32(3),
                checked((ulong)reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task<IReadOnlyList<CurrentMetricView>> ReadCurrentMetricsAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.envelope, n.node_id
            FROM telemetry_envelopes AS t
            JOIN nodes AS n ON n.agent_id = t.agent_id
            WHERE ($agent IS NULL OR t.agent_id = $agent)
            ORDER BY t.agent_id, t.sequence DESC;
            """;
        command.Parameters.AddWithValue("$agent", (object?)agentId ?? DBNull.Value);
        var latest = new Dictionary<(string AgentId, string MetricId, string ComponentId), CurrentMetricView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var envelope = TelemetryEnvelope.Parser.ParseFrom((byte[])reader.GetValue(0));
            if (envelope.PayloadCase != TelemetryEnvelope.PayloadOneofCase.Metric)
            {
                continue;
            }

            var key = (envelope.AgentId, envelope.Metric.MetricId, envelope.Metric.ComponentId);
            if (latest.ContainsKey(key))
            {
                continue;
            }

            latest.Add(key, new CurrentMetricView(
                reader.GetString(1),
                envelope.AgentId,
                envelope.Metric.MetricId,
                envelope.Metric.ComponentId,
                envelope.Metric.Unit,
                envelope.Metric.Quality,
                ReadMetricValue(envelope.Metric.Value),
                envelope.ObservedAt.ToDateTimeOffset(),
                envelope.IngestedAt.ToDateTimeOffset(),
                envelope.CollectorId,
                envelope.Sequence));
        }

        return latest.Values.OrderBy(value => value.AgentId).ThenBy(value => value.MetricId).ToArray();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<ulong> ReadAcceptedThroughAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT accepted_through_sequence FROM agent_stream_state WHERE agent_id = $agent;";
        command.Parameters.AddWithValue("$agent", agentId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : checked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task EnsureIdenticalReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        ulong sequence,
        byte[] submission,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT submission FROM telemetry_envelopes WHERE agent_id = $agent AND sequence = $sequence;";
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$sequence", checked((long)sequence));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        if (stored is null || !CryptographicOperations.FixedTimeEquals(stored, submission))
        {
            throw new TelemetryFailureException(TelemetryFailureReason.SequenceConflict, "Telemetry sequence conflicts with durable controller state.");
        }
    }

    private static object? ReadMetricValue(MetricValue? value) => value?.ValueCase switch
    {
        MetricValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        MetricValue.ValueOneofCase.SignedIntegerValue => value.SignedIntegerValue,
        MetricValue.ValueOneofCase.UnsignedIntegerValue => value.UnsignedIntegerValue,
        MetricValue.ValueOneofCase.BooleanValue => value.BooleanValue,
        MetricValue.ValueOneofCase.StringValue => value.StringValue,
        _ => null,
    };

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void EnsureIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new TelemetryFailureException(TelemetryFailureReason.InvalidMessage, $"{field} is invalid.");
        }
    }
}
