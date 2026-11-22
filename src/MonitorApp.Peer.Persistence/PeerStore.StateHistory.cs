using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

/// <summary>Connectivity state of a trusted peer as observed by this node (or phone).</summary>
public enum PeerConnectivityState
{
    Paired = 0,
    Online = 1,
    Stale = 2,
    Offline = 3,
    Revoked = 4,
}

/// <summary>One recorded change of a peer's connectivity state.</summary>
public sealed record PeerStateTransition(string PeerId, PeerConnectivityState State, DateTimeOffset At, string? Reason);

// Durable peer-state transition history (the data behind the UI time-travel timeline). Only changes are stored.
public sealed partial class PeerStore
{
    private const string PeerStateHistorySchema = """
        CREATE TABLE IF NOT EXISTS peer_state_transitions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            peer_id TEXT NOT NULL,
            state TEXT NOT NULL,
            at TEXT NOT NULL,
            reason TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_peer_state_transitions_at ON peer_state_transitions(at);
        CREATE INDEX IF NOT EXISTS ix_peer_state_transitions_peer ON peer_state_transitions(peer_id, id);
        """;

    /// <summary>Records a transition unless the peer's latest recorded state is already <paramref name="state"/>. Returns true when stored.</summary>
    public async Task<bool> RecordPeerStateAsync(
        string peerId, PeerConnectivityState state, DateTimeOffset at, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO peer_state_transitions(peer_id, state, at, reason)
            SELECT $peer, $state, $at, $reason
            WHERE COALESCE((SELECT state FROM peer_state_transitions WHERE peer_id = $peer ORDER BY id DESC LIMIT 1), '') <> $state;
            """;
        command.Parameters.AddWithValue("$peer", peerId);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$at", Format(at.ToUniversalTime()));
        command.Parameters.AddWithValue("$reason", reason is null ? DBNull.Value : reason);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>The latest recorded transition of a peer, or null when none exists.</summary>
    public async Task<PeerStateTransition?> ReadLastPeerStateAsync(string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT peer_id, state, at, reason FROM peer_state_transitions WHERE peer_id = $peer ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$peer", peerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTransition(reader) : null;
    }

    /// <summary>Transitions with from &lt;= at &lt;= to, oldest first (then recording order), at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<PeerStateTransition>> ReadPeerStateTransitionsAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT peer_id, state, at, reason FROM peer_state_transitions
            WHERE at >= $from AND at <= $to ORDER BY at, id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", Format(from.ToUniversalTime()));
        command.Parameters.AddWithValue("$to", Format(to.ToUniversalTime()));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<PeerStateTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadTransition(reader));
        }

        return result;
    }

    /// <summary>Deletes transitions recorded before the cutoff (retention). Returns the number removed.</summary>
    public async Task<int> PrunePeerStateTransitionsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM peer_state_transitions WHERE at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", Format(cutoff.ToUniversalTime()));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PeerStateTransition ReadTransition(SqliteDataReader reader) =>
        new(reader.GetString(0), Enum.Parse<PeerConnectivityState>(reader.GetString(1)), Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
}
