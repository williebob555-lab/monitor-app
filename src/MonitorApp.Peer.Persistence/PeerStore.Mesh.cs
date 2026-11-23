using System.Data;
using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

public enum StoredMeshStatementKind
{
    Admit = 1,
    Remove = 2,
}

/// <summary>
/// One stored mesh membership statement (T080). <see cref="StatementBytes"/> is the issuer-signed wire message; the
/// other columns are copies used for lookups. The store does not verify signatures: <c>MeshMembership</c> does that
/// before it writes and again whenever it derives trust.
/// </summary>
public sealed record StoredMeshStatement(
    string StatementId,
    StoredMeshStatementKind Kind,
    string IssuerPeerId,
    string SubjectPeerId,
    byte[] SubjectFingerprint,
    byte[] StatementBytes,
    DateTimeOffset IssuedAt,
    DateTimeOffset ReceivedAt,
    string? ReceivedFromPeerId);

/// <summary>How a trusted peer row relates to the mesh: created by the mesh, and/or revoked by the mesh.</summary>
public sealed record MeshTrustMark(string PeerId, bool AdmittedViaMesh, bool RemovedViaMesh, string? AdmittedBy, string? RemovedBy, DateTimeOffset UpdatedAt);

public enum MeshStatementWriteOutcome
{
    Inserted = 1,
    Replaced = 2,
    Unchanged = 3,
}

// Mesh membership statements and the marks that say which trust rows the mesh created or revoked (peer schema v5).
public sealed partial class PeerStore
{
    /// <summary>Upper bound on stored statements; a mesh of personal devices needs a few dozen.</summary>
    public const int MaximumMeshStatements = 1024;

    private const string MeshSchema = """
        CREATE TABLE IF NOT EXISTS mesh_statements (
            statement_id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL CHECK (kind IN (1, 2)),
            issuer_peer_id TEXT NOT NULL,
            subject_peer_id TEXT NOT NULL,
            subject_fingerprint BLOB NOT NULL,
            statement_bytes BLOB NOT NULL,
            issued_at TEXT NOT NULL,
            received_at TEXT NOT NULL,
            received_from_peer_id TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_mesh_statements_claim
            ON mesh_statements(kind, issuer_peer_id, subject_peer_id, subject_fingerprint);
        CREATE TABLE IF NOT EXISTS mesh_trust_marks (
            peer_id TEXT PRIMARY KEY,
            admitted_via_mesh INTEGER NOT NULL CHECK (admitted_via_mesh IN (0, 1)),
            removed_via_mesh INTEGER NOT NULL CHECK (removed_via_mesh IN (0, 1)),
            admitted_by TEXT NULL,
            removed_by TEXT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Stores a statement. One statement exists per (kind, issuer, subject, subject key): a newer one from the same
    /// issuer replaces the older (for example with fresher locators); an older or equal one is ignored.
    /// </summary>
    public async Task<MeshStatementWriteOutcome> UpsertMeshStatementAsync(StoredMeshStatement statement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        EnsureIdentifier(statement.StatementId, nameof(statement.StatementId));
        EnsureIdentifier(statement.IssuerPeerId, nameof(statement.IssuerPeerId));
        EnsureIdentifier(statement.SubjectPeerId, nameof(statement.SubjectPeerId));
        if (statement.Kind is not (StoredMeshStatementKind.Admit or StoredMeshStatementKind.Remove)) throw new ArgumentOutOfRangeException(nameof(statement));
        if (statement.SubjectFingerprint is not { Length: PublicKeyFingerprintSize }) throw new ArgumentException("Subject fingerprint must be 32 bytes.", nameof(statement));
        EnsureBytes(statement.StatementBytes, nameof(statement.StatementBytes));
        EnsureUtc(statement.IssuedAt, nameof(statement.IssuedAt));
        EnsureUtc(statement.ReceivedAt, nameof(statement.ReceivedAt));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT statement_id, issued_at FROM mesh_statements
            WHERE kind = $kind AND issuer_peer_id = $issuer AND subject_peer_id = $subject AND subject_fingerprint = $fingerprint;
            """;
        existing.Parameters.AddWithValue("$kind", (int)statement.Kind);
        existing.Parameters.AddWithValue("$issuer", statement.IssuerPeerId);
        existing.Parameters.AddWithValue("$subject", statement.SubjectPeerId);
        existing.Parameters.Add("$fingerprint", SqliteType.Blob).Value = statement.SubjectFingerprint;
        string? existingId = null;
        DateTimeOffset existingIssued = default;
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingId = reader.GetString(0);
                existingIssued = Parse(reader.GetString(1));
            }
        }

        if (existingId is not null && (existingId == statement.StatementId || existingIssued >= statement.IssuedAt))
        {
            return MeshStatementWriteOutcome.Unchanged;
        }

        if (existingId is null)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM mesh_statements;";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) >= MaximumMeshStatements)
            {
                throw new PeerPersistenceConflictException("The mesh statement limit is reached.");
            }
        }
        else
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM mesh_statements WHERE statement_id = $id;";
            delete.Parameters.AddWithValue("$id", existingId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO mesh_statements(statement_id, kind, issuer_peer_id, subject_peer_id, subject_fingerprint,
                statement_bytes, issued_at, received_at, received_from_peer_id)
            VALUES ($id, $kind, $issuer, $subject, $fingerprint, $bytes, $issued, $received, $from);
            """;
        insert.Parameters.AddWithValue("$id", statement.StatementId);
        insert.Parameters.AddWithValue("$kind", (int)statement.Kind);
        insert.Parameters.AddWithValue("$issuer", statement.IssuerPeerId);
        insert.Parameters.AddWithValue("$subject", statement.SubjectPeerId);
        insert.Parameters.Add("$fingerprint", SqliteType.Blob).Value = statement.SubjectFingerprint;
        insert.Parameters.Add("$bytes", SqliteType.Blob).Value = statement.StatementBytes;
        insert.Parameters.AddWithValue("$issued", Format(statement.IssuedAt));
        insert.Parameters.AddWithValue("$received", Format(statement.ReceivedAt));
        insert.Parameters.AddWithValue("$from", (object?)statement.ReceivedFromPeerId ?? DBNull.Value);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            // Same statement id under a different claim: an id collision or a tampered copy. Keep what is stored.
            return MeshStatementWriteOutcome.Unchanged;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existingId is null ? MeshStatementWriteOutcome.Inserted : MeshStatementWriteOutcome.Replaced;
    }

    public async Task<IReadOnlyList<StoredMeshStatement>> ReadMeshStatementsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT statement_id, kind, issuer_peer_id, subject_peer_id, subject_fingerprint, statement_bytes, issued_at, received_at, received_from_peer_id
            FROM mesh_statements ORDER BY issued_at, statement_id;
            """;
        var result = new List<StoredMeshStatement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredMeshStatement(reader.GetString(0), (StoredMeshStatementKind)reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), Bytes(reader, 4), Bytes(reader, 5), Parse(reader.GetString(6)), Parse(reader.GetString(7)),
                NullableString(reader, 8)));
        }

        return result;
    }

    public async Task<IReadOnlyList<MeshTrustMark>> ReadMeshTrustMarksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, admitted_via_mesh, removed_via_mesh, admitted_by, removed_by, updated_at FROM mesh_trust_marks ORDER BY peer_id;";
        var result = new List<MeshTrustMark>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MeshTrustMark(reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt32(2) == 1,
                NullableString(reader, 3), NullableString(reader, 4), Parse(reader.GetString(5))));
        }

        return result;
    }

    /// <summary>
    /// Creates trust for a peer the mesh admitted, in one transaction with its mark. Returns false (and changes nothing)
    /// when the peer id already has a trust row, whatever its key or state: the mesh never rebinds or revives a row.
    /// </summary>
    public async Task<bool> AdmitPeerViaMeshAsync(TrustedPeer peer, string admittedBy, IReadOnlyList<PeerLocator> locators,
        CancellationToken cancellationToken = default)
    {
        Validate(peer);
        EnsureIdentifier(admittedBy, nameof(admittedBy));
        if (peer.TrustState != PeerTrustState.Active) throw new ArgumentException("A mesh admission creates active trust.", nameof(peer));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO trusted_peers(peer_id, public_key_spki, public_key_fingerprint, display_name, trust_state, paired_at, revoked_at)
            VALUES ($peer, $key, $fingerprint, $name, $state, $paired, $revoked);
            """;
        Bind(command, peer);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            INSERT INTO mesh_trust_marks(peer_id, admitted_via_mesh, removed_via_mesh, admitted_by, removed_by, updated_at)
            VALUES ($peer, 1, 0, $by, NULL, $at)
            ON CONFLICT(peer_id) DO UPDATE SET admitted_via_mesh = 1, admitted_by = excluded.admitted_by, updated_at = excluded.updated_at;
            """;
        mark.Parameters.AddWithValue("$peer", peer.PeerId);
        mark.Parameters.AddWithValue("$by", admittedBy);
        mark.Parameters.AddWithValue("$at", Format(peer.PairedAt));
        await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        foreach (var locator in locators)
        {
            EnsureIdentifier(locator.Host, nameof(locator.Host));
            if (locator.PeerId != peer.PeerId || locator.Port is < 1 or > 65535) throw new ArgumentException("Locator does not belong to the admitted peer.", nameof(locators));
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO peer_locators(peer_id, host, port, updated_at, last_connected_at)
                VALUES ($peer, $host, $port, $updated, NULL)
                ON CONFLICT(peer_id, host, port) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$peer", locator.PeerId);
            insert.Parameters.AddWithValue("$host", locator.Host);
            insert.Parameters.AddWithValue("$port", locator.Port);
            insert.Parameters.AddWithValue("$updated", Format(locator.UpdatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Revokes an active peer because the mesh removed it, in one transaction with its mark. Returns false when the
    /// peer is missing, already revoked, or bound to a different key than the removal names.
    /// </summary>
    public async Task<bool> RemovePeerViaMeshAsync(string peerId, byte[] fingerprint, string removedBy, DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        EnsureIdentifier(peerId, nameof(peerId));
        EnsureIdentifier(removedBy, nameof(removedBy));
        EnsureUtc(at, nameof(at));
        if (fingerprint is not { Length: PublicKeyFingerprintSize }) throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(fingerprint));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE trusted_peers SET trust_state = $revoked, revoked_at = $at
            WHERE peer_id = $peer AND trust_state = $active AND public_key_fingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$revoked", (int)PeerTrustState.Revoked);
        command.Parameters.AddWithValue("$active", (int)PeerTrustState.Active);
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$peer", peerId);
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = fingerprint;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            INSERT INTO mesh_trust_marks(peer_id, admitted_via_mesh, removed_via_mesh, admitted_by, removed_by, updated_at)
            VALUES ($peer, 0, 1, NULL, $by, $at)
            ON CONFLICT(peer_id) DO UPDATE SET removed_via_mesh = 1, removed_by = excluded.removed_by, updated_at = excluded.updated_at;
            """;
        mark.Parameters.AddWithValue("$peer", peerId);
        mark.Parameters.AddWithValue("$by", removedBy);
        mark.Parameters.AddWithValue("$at", Format(at));
        await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
