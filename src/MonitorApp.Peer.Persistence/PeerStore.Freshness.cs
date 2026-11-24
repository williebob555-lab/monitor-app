namespace MonitorApp.Peer.Persistence;

// How fresh each device's readings are here, for "last reading from the laptop: 3 minutes ago" on its card (R4).
public sealed partial class PeerStore
{
    /// <summary>
    /// For each origin device whose readings this store accepted from another device, when its newest accepted reading was
    /// taken. Reads the small cursor table and one primary-key row per stream, never the whole record table.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> ReadLatestAcceptedByOriginAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.origin_peer_id, MAX(r.observed_at)
            FROM replication_cursors c
            JOIN origin_records r
              ON r.origin_peer_id = c.origin_peer_id AND r.origin_stream_id = c.origin_stream_id AND r.sequence = c.accepted_through
            WHERE c.accepted_through > 0
            GROUP BY c.origin_peer_id;
            """;
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                result[reader.GetString(0)] = Parse(reader.GetString(1));
            }
        }

        return result;
    }
}
