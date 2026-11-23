using System.Globalization;
using Microsoft.Data.Sqlite;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.LocalApi;

internal sealed class LocalApiDataSource
{
    private readonly string connectionString;
    private readonly PeerStore peerStore;

    public LocalApiDataSource(string databasePath, PeerStore peerStore)
    {
        this.peerStore = peerStore;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task<LocalRuntimeView> ReadRuntimeAsync(CancellationToken cancellationToken)
    {
        var identity = await peerStore.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        var peers = await ReadPeersAsync(cancellationToken).ConfigureAwait(false);
        var pairings = await ReadPendingPairingsAsync(cancellationToken).ConfigureAwait(false);
        return new LocalRuntimeView(
            "running",
            "Implemented: local identity, trust, pairing administration, and stored observation query. " +
            "Planned: full experience UI, collector administration, platform packaging, and remote browser access.",
            identity is null
                ? null
                : new LocalPeerView(identity.PeerId, Convert.ToHexString(identity.PublicKeyFingerprint)),
            peers.Count,
            pairings.Count);
    }

    public async Task<IReadOnlyList<TrustedPeerView>> ReadPeersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT peer_id, display_name, public_key_fingerprint, trust_state, paired_at, revoked_at
            FROM trusted_peers ORDER BY peer_id;
            """;
        var result = new List<TrustedPeerView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TrustedPeerView(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Convert.ToHexString((byte[])reader[2]),
                ((PeerTrustState)reader.GetInt32(3)).ToString(),
                Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5))));
        }

        var provenance = await MeshProvenance.DescribeAsync(peerStore, cancellationToken).ConfigureAwait(false);
        return result.Select(peer => peer with { Provenance = provenance.GetValueOrDefault(peer.PeerId) }).ToList();
    }

    public async Task<IReadOnlyList<PendingPairingView>> ReadPendingPairingsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pairing_id, remote_peer_id, display_name, remote_public_key_fingerprint,
                approval_state, protocol_major, protocol_minor, created_at, expires_at
            FROM pairing_approvals
            WHERE approval_state IN ($pending, $approved)
            ORDER BY created_at, pairing_id;
            """;
        command.Parameters.AddWithValue("$pending", (int)PairingApprovalState.PendingApproval);
        command.Parameters.AddWithValue("$approved", (int)PairingApprovalState.Approved);
        var result = new List<PendingPairingView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PendingPairingView(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Convert.ToHexString((byte[])reader[3]),
                ((PairingApprovalState)reader.GetInt32(4)).ToString(),
                checked((uint)reader.GetInt64(5)),
                checked((uint)reader.GetInt64(6)),
                Parse(reader.GetString(7)),
                Parse(reader.GetString(8))));
        }

        return result;
    }

    public async Task<IReadOnlyList<OriginObservationView>> ReadObservationsAsync(
        string originPeerId,
        string originStreamId,
        ulong firstSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        var records = await peerStore.ReadOriginRangeAsync(
            originPeerId,
            originStreamId,
            firstSequence,
            limit,
            cancellationToken).ConfigureAwait(false);
        return records.Select(record => new OriginObservationView(
            record.OriginPeerId,
            record.OriginStreamId,
            record.Sequence,
            record.SubjectEntityId,
            record.AdapterInstanceId,
            record.SignalId,
            record.ObservedAt,
            record.ReceivedAt,
            record.ReceivedFromPeerId,
            record.Provenance.ToString(),
            record.Quality,
            Convert.ToHexString(record.ContentHash),
            record.RecordBytes.Length)).ToArray();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            MonitorApp.Peer.Persistence.SqliteConnectionTracker.Track(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
