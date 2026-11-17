using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Persistence;

public sealed class EnrollmentStore
{
    private readonly EnrollmentStoreOptions options;
    private readonly TimeProvider timeProvider;
    private readonly string connectionString;

    public EnrollmentStore(EnrollmentStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (options.SecretPepper.Length < 32)
        {
            throw new ArgumentException("Secret pepper must contain at least 32 bytes.", nameof(options));
        }

        if (options.EnrollmentLifetime <= TimeSpan.Zero || options.DeviceChallengeLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException("Enrollment and challenge lifetimes must be positive.", nameof(options));
        }

        this.options = options with { SecretPepper = options.SecretPepper.ToArray() };
        this.timeProvider = timeProvider ?? TimeProvider.System;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
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
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS enrollments (
                enrollment_id TEXT PRIMARY KEY,
                secret_salt BLOB NOT NULL,
                secret_verifier BLOB NOT NULL,
                proof_challenge BLOB NOT NULL,
                platform INTEGER NOT NULL,
                display_name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                state INTEGER NOT NULL,
                agent_id TEXT NULL,
                node_id TEXT NULL,
                completed_at TEXT NULL,
                cancelled_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS nodes (
                node_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL UNIQUE,
                platform INTEGER NOT NULL,
                display_name TEXT NOT NULL,
                public_key_spki BLOB NOT NULL,
                public_key_sha256 BLOB NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS device_authentication_challenges (
                challenge_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                proof_challenge BLOB NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL,
                FOREIGN KEY(agent_id) REFERENCES nodes(agent_id)
            );
            CREATE TABLE IF NOT EXISTS security_audit_events (
                audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                target_id TEXT NOT NULL,
                outcome TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, $now);
            """;
        command.Parameters.AddWithValue("$now", Format(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateEnrollmentResponse> CreateAsync(
        CreateEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureDisplayName(request.DisplayName);
        var now = timeProvider.GetUtcNow();
        var expires = now + options.EnrollmentLifetime;
        var enrollmentId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var secret = RandomNumberGenerator.GetBytes(EnrollmentContractRules.EnrollmentSecretSize);
        var salt = RandomNumberGenerator.GetBytes(16);
        var challenge = RandomNumberGenerator.GetBytes(EnrollmentContractRules.ProofChallengeSize);
        var verifier = CreateSecretVerifier(salt, secret);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO enrollments(
                    enrollment_id, secret_salt, secret_verifier, proof_challenge,
                    platform, display_name, created_at, expires_at, state)
                VALUES ($id, $salt, $verifier, $challenge, $platform, $name, $created, $expires, $state);
                """;
            command.Parameters.AddWithValue("$id", enrollmentId);
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$verifier", verifier);
            command.Parameters.AddWithValue("$challenge", challenge);
            command.Parameters.AddWithValue("$platform", (int)request.Platform);
            command.Parameters.AddWithValue("$name", request.DisplayName.Trim());
            command.Parameters.AddWithValue("$created", Format(now));
            command.Parameters.AddWithValue("$expires", Format(expires));
            command.Parameters.AddWithValue("$state", (int)EnrollmentState.Pending);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(connection, transaction, "enrollment.created", enrollmentId, "success", now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CreateEnrollmentResponse
        {
            EnrollmentId = enrollmentId,
            EnrollmentSecret = ByteString.CopyFrom(secret),
            ProofChallenge = ByteString.CopyFrom(challenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(now),
            ExpiresAt = Timestamp.FromDateTimeOffset(expires)
        };
    }

    public async Task<EnrollmentStatusResponse> GetStatusAsync(
        EnrollmentStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureIdentifier(request.EnrollmentId, "Enrollment id");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExpireIfNeededAsync(connection, request.EnrollmentId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enrollment_id, state, created_at, expires_at, agent_id, node_id
            FROM enrollments WHERE enrollment_id = $id;
            """;
        command.Parameters.AddWithValue("$id", request.EnrollmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Fail(EnrollmentFailure.NotFound, "Enrollment was not found.");
        }

        return new EnrollmentStatusResponse
        {
            EnrollmentId = reader.GetString(0),
            State = (EnrollmentState)reader.GetInt32(1),
            CreatedAt = Timestamp.FromDateTimeOffset(Parse(reader.GetString(2))),
            ExpiresAt = Timestamp.FromDateTimeOffset(Parse(reader.GetString(3))),
            AgentId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            NodeId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
        };
    }

    public async Task<CancelEnrollmentResponse> CancelAsync(
        CancelEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureIdentifier(request.EnrollmentId, "Enrollment id");
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var record = await ReadEnrollmentAsync(connection, transaction, request.EnrollmentId, cancellationToken).ConfigureAwait(false);
        ThrowForInactive(record.State, record.ExpiresAt, now);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE enrollments SET state = $cancelled, cancelled_at = $now
                WHERE enrollment_id = $id AND state = $pending;
                """;
            command.Parameters.AddWithValue("$cancelled", (int)EnrollmentState.Cancelled);
            command.Parameters.AddWithValue("$pending", (int)EnrollmentState.Pending);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$id", request.EnrollmentId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Fail(EnrollmentFailure.AlreadyUsed, "Enrollment is no longer pending.");
            }
        }

        await WriteAuditAsync(connection, transaction, "enrollment.cancelled", request.EnrollmentId, "success", now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CancelEnrollmentResponse
        {
            EnrollmentId = request.EnrollmentId,
            State = EnrollmentState.Cancelled,
            CancelledAt = Timestamp.FromDateTimeOffset(now)
        };
    }

    public async Task<CompleteEnrollmentResponse> CompleteAsync(
        CompleteEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureIdentifier(request.EnrollmentId, "Enrollment id");
        EnsureDisplayName(request.DisplayName);
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var record = await ReadEnrollmentAsync(connection, transaction, request.EnrollmentId, cancellationToken).ConfigureAwait(false);
        ThrowForInactive(record.State, record.ExpiresAt, now);
        if (record.Platform != request.Platform ||
            !string.Equals(record.DisplayName, request.DisplayName.Trim(), StringComparison.Ordinal))
        {
            await WriteAuditAsync(connection, transaction, "enrollment.completion", request.EnrollmentId, "identity_mismatch", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw Fail(EnrollmentFailure.Unauthorized, "Enrollment identity could not be verified.");
        }

        var submittedVerifier = CreateSecretVerifier(record.SecretSalt, request.EnrollmentSecret.Span);
        if (!CryptographicOperations.FixedTimeEquals(record.SecretVerifier, submittedVerifier))
        {
            await WriteAuditAsync(connection, transaction, "enrollment.completion", request.EnrollmentId, "credential_rejected", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw Fail(EnrollmentFailure.Unauthorized, "Enrollment identity could not be verified.");
        }

        var publicKey = request.PublicKeySpki.ToByteArray();
        var payload = ProofPayload.CreateEnrollment(request.EnrollmentId, record.ProofChallenge, publicKey);
        if (!VerifySignature(publicKey, payload, request.ProofSignature.Span))
        {
            await WriteAuditAsync(connection, transaction, "enrollment.completion", request.EnrollmentId, "proof_rejected", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw Fail(EnrollmentFailure.Unauthorized, "Enrollment identity could not be verified.");
        }

        var agentId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var nodeId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var fingerprint = SHA256.HashData(publicKey);
        try
        {
            await using (var node = connection.CreateCommand())
            {
                node.Transaction = transaction;
                node.CommandText = """
                    INSERT INTO nodes(node_id, agent_id, platform, display_name, public_key_spki, public_key_sha256, created_at)
                    VALUES ($node, $agent, $platform, $name, $key, $fingerprint, $now);
                    """;
                node.Parameters.AddWithValue("$node", nodeId);
                node.Parameters.AddWithValue("$agent", agentId);
                node.Parameters.AddWithValue("$platform", (int)request.Platform);
                node.Parameters.AddWithValue("$name", request.DisplayName.Trim());
                node.Parameters.AddWithValue("$key", publicKey);
                node.Parameters.AddWithValue("$fingerprint", fingerprint);
                node.Parameters.AddWithValue("$now", Format(now));
                await node.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var complete = connection.CreateCommand())
            {
                complete.Transaction = transaction;
                complete.CommandText = """
                    UPDATE enrollments
                    SET state = $completed, agent_id = $agent, node_id = $node, completed_at = $now,
                        secret_salt = zeroblob(0), secret_verifier = zeroblob(0), proof_challenge = zeroblob(0)
                    WHERE enrollment_id = $id AND state = $pending;
                    """;
                complete.Parameters.AddWithValue("$completed", (int)EnrollmentState.Completed);
                complete.Parameters.AddWithValue("$pending", (int)EnrollmentState.Pending);
                complete.Parameters.AddWithValue("$agent", agentId);
                complete.Parameters.AddWithValue("$node", nodeId);
                complete.Parameters.AddWithValue("$now", Format(now));
                complete.Parameters.AddWithValue("$id", request.EnrollmentId);
                if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw Fail(EnrollmentFailure.AlreadyUsed, "Enrollment is no longer pending.");
                }
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw Fail(EnrollmentFailure.AlreadyUsed, "Device identity is already enrolled.");
        }

        await WriteAuditAsync(connection, transaction, "enrollment.completed", agentId, "success", now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CompleteEnrollmentResponse
        {
            AgentId = agentId,
            NodeId = nodeId,
            PublicKeySha256 = ByteString.CopyFrom(fingerprint),
            CompletedAt = Timestamp.FromDateTimeOffset(now)
        };
    }

    public async Task<CreateDeviceChallengeResponse> CreateDeviceChallengeAsync(
        CreateDeviceChallengeRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureIdentifier(request.AgentId, "Agent id");
        var now = timeProvider.GetUtcNow();
        var expires = now + options.DeviceChallengeLifetime;
        var challengeId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var challenge = RandomNumberGenerator.GetBytes(EnrollmentContractRules.ProofChallengeSize);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        if (!await ActiveAgentExistsAsync(connection, transaction, request.AgentId, cancellationToken).ConfigureAwait(false))
        {
            throw Fail(EnrollmentFailure.NotFound, "Agent was not found.");
        }

        if (await CountOutstandingChallengesAsync(connection, transaction, request.AgentId, now, cancellationToken).ConfigureAwait(false) >= 5)
        {
            throw Fail(EnrollmentFailure.InvalidRequest, "Agent has too many outstanding device challenges.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO device_authentication_challenges(challenge_id, agent_id, proof_challenge, created_at, expires_at)
                VALUES ($id, $agent, $challenge, $created, $expires);
                """;
            command.Parameters.AddWithValue("$id", challengeId);
            command.Parameters.AddWithValue("$agent", request.AgentId);
            command.Parameters.AddWithValue("$challenge", challenge);
            command.Parameters.AddWithValue("$created", Format(now));
            command.Parameters.AddWithValue("$expires", Format(expires));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(connection, transaction, "device_challenge.created", request.AgentId, "success", now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreateDeviceChallengeResponse
        {
            AgentId = request.AgentId,
            ChallengeId = challengeId,
            ProofChallenge = ByteString.CopyFrom(challenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(now),
            ExpiresAt = Timestamp.FromDateTimeOffset(expires)
        };
    }

    public async Task<VerifyDeviceProofResponse> VerifyDeviceProofAsync(
        VerifyDeviceProofRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(ContractValidator.Validate(request));
        EnsureIdentifier(request.AgentId, "Agent id");
        EnsureIdentifier(request.ChallengeId, "Challenge id");
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT c.agent_id, c.proof_challenge, c.expires_at, c.consumed_at, n.node_id, n.public_key_spki
            FROM device_authentication_challenges c
            JOIN nodes n ON n.agent_id = c.agent_id AND n.revoked_at IS NULL
            WHERE c.challenge_id = $id;
            """;
        read.Parameters.AddWithValue("$id", request.ChallengeId);
        string agentId;
        byte[] challenge;
        DateTimeOffset expires;
        string nodeId;
        byte[] publicKey;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Fail(EnrollmentFailure.NotFound, "Device challenge was not found.");
            }

            agentId = reader.GetString(0);
            challenge = (byte[])reader[1];
            expires = Parse(reader.GetString(2));
            if (!reader.IsDBNull(3))
            {
                throw Fail(EnrollmentFailure.AlreadyUsed, "Device challenge has already been used.");
            }

            nodeId = reader.GetString(4);
            publicKey = (byte[])reader[5];
        }

        if (!string.Equals(agentId, request.AgentId, StringComparison.Ordinal))
        {
            throw Fail(EnrollmentFailure.Unauthorized, "Device proof could not be verified.");
        }

        if (now >= expires)
        {
            throw Fail(EnrollmentFailure.Expired, "Device challenge has expired.");
        }

        var payload = ProofPayload.CreateDevice(agentId, request.ChallengeId, challenge);
        if (!VerifySignature(publicKey, payload, request.ProofSignature.Span))
        {
            await WriteAuditAsync(connection, transaction, "device_challenge.verification", agentId, "proof_rejected", now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw Fail(EnrollmentFailure.Unauthorized, "Device proof could not be verified.");
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE device_authentication_challenges SET consumed_at = $now
                WHERE challenge_id = $id AND consumed_at IS NULL;
                """;
            consume.Parameters.AddWithValue("$now", Format(now));
            consume.Parameters.AddWithValue("$id", request.ChallengeId);
            if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Fail(EnrollmentFailure.AlreadyUsed, "Device challenge has already been used.");
            }
        }

        await WriteAuditAsync(connection, transaction, "device_challenge.verified", agentId, "success", now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new VerifyDeviceProofResponse
        {
            AgentId = agentId,
            NodeId = nodeId,
            VerifiedAt = Timestamp.FromDateTimeOffset(now)
        };
    }

    public async Task<IReadOnlyList<(string EventType, string TargetId, string Outcome)>> ReadAuditAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string, string, string)>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type, target_id, outcome FROM security_audit_events ORDER BY audit_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return results;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private byte[] CreateSecretVerifier(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> secret)
    {
        var material = new byte[salt.Length + secret.Length];
        salt.CopyTo(material);
        secret.CopyTo(material.AsSpan(salt.Length));
        return HMACSHA256.HashData(options.SecretPepper, material);
    }

    private static bool VerifySignature(byte[] publicKey, byte[] payload, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            var parameters = ecdsa.ExportParameters(false);
            return consumed == publicKey.Length && ecdsa.KeySize == 256 &&
                string.Equals(parameters.Curve.Oid.Value, "1.2.840.10045.3.1.7", StringComparison.Ordinal) &&
                ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void EnsureValid(ContractValidationResult result)
    {
        if (!result.IsValid)
        {
            throw Fail(EnrollmentFailure.InvalidRequest, string.Join(" ", result.Errors));
        }
    }

    private static EnrollmentFailureException Fail(EnrollmentFailure failure, string message) => new(failure, message);

    private static void ThrowForInactive(EnrollmentState state, DateTimeOffset expires, DateTimeOffset now)
    {
        if (state == EnrollmentState.Completed)
        {
            throw Fail(EnrollmentFailure.AlreadyUsed, "Enrollment has already been used.");
        }

        if (state == EnrollmentState.Cancelled)
        {
            throw Fail(EnrollmentFailure.Cancelled, "Enrollment has been cancelled.");
        }

        if (state == EnrollmentState.Expired || now >= expires)
        {
            throw Fail(EnrollmentFailure.Expired, "Enrollment has expired.");
        }
    }

    private static async Task<EnrollmentRecord> ReadEnrollmentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string enrollmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT secret_salt, secret_verifier, proof_challenge, platform, display_name, expires_at, state
            FROM enrollments WHERE enrollment_id = $id;
            """;
        command.Parameters.AddWithValue("$id", enrollmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Fail(EnrollmentFailure.NotFound, "Enrollment was not found.");
        }

        return new EnrollmentRecord(
            (byte[])reader[0],
            (byte[])reader[1],
            (byte[])reader[2],
            (Platform)reader.GetInt32(3),
            reader.GetString(4),
            Parse(reader.GetString(5)),
            (EnrollmentState)reader.GetInt32(6));
    }

    private async Task ExpireIfNeededAsync(SqliteConnection connection, string enrollmentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE enrollments SET state = $expired
            WHERE enrollment_id = $id AND state = $pending AND expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$expired", (int)EnrollmentState.Expired);
        command.Parameters.AddWithValue("$pending", (int)EnrollmentState.Pending);
        command.Parameters.AddWithValue("$id", enrollmentId);
        command.Parameters.AddWithValue("$now", Format(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ActiveAgentExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM nodes WHERE agent_id = $agent AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$agent", agentId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long> CountOutstandingChallengesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM device_authentication_challenges
            WHERE agent_id = $agent AND consumed_at IS NULL AND expires_at > $now;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$now", Format(now));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static void EnsureIdentifier(string value, string fieldName)
    {
        if (value.Length > 128)
        {
            throw Fail(EnrollmentFailure.InvalidRequest, $"{fieldName} is too long.");
        }
    }

    private static void EnsureDisplayName(string value)
    {
        if (value.Trim().Length > 200)
        {
            throw Fail(EnrollmentFailure.InvalidRequest, "Display name is too long.");
        }
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventType,
        string targetId,
        string outcome,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO security_audit_events(event_type, target_id, outcome, occurred_at)
            VALUES ($event, $target, $outcome, $at);
            """;
        command.Parameters.AddWithValue("$event", eventType);
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$at", Format(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private sealed record EnrollmentRecord(
        byte[] SecretSalt,
        byte[] SecretVerifier,
        byte[] ProofChallenge,
        Platform Platform,
        string DisplayName,
        DateTimeOffset ExpiresAt,
        EnrollmentState State);
}
