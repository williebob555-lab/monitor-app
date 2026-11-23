namespace MonitorApp.Peer.Persistence;

/// <summary>
/// How each trusted device came to be trusted or removed here (T086): paired on this device, or added or removed by
/// another member of the mesh, in words for the device list.
/// </summary>
public static class MeshProvenance
{
    public static async Task<IReadOnlyDictionary<string, string>> DescribeAsync(PeerStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var local = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        var peers = await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false);
        var marks = await store.ReadMeshTrustMarksAsync(cancellationToken).ConfigureAwait(false);
        var names = peers.ToDictionary(peer => peer.PeerId, peer => peer.DisplayName, StringComparer.Ordinal);
        string Name(string? peerId) => peerId is null ? "another device"
            : peerId == local?.PeerId ? "this device"
            : names.TryGetValue(peerId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : "another device";

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var peer in peers)
            result[peer.PeerId] = "Paired on this device";
        foreach (var mark in marks)
        {
            if (!names.ContainsKey(mark.PeerId)) continue;
            if (mark.RemovedViaMesh) result[mark.PeerId] = $"Removed by {Name(mark.RemovedBy)}";
            else if (mark.AdmittedViaMesh) result[mark.PeerId] = $"Added by {Name(mark.AdmittedBy)}";
        }

        return result;
    }
}
