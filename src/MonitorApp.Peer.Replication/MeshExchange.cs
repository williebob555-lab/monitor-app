using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Replication;

/// <summary>Applies the mesh statements carried by an authenticated capability exchange without ever failing the session.</summary>
public static class MeshExchange
{
    /// <summary>Fills the outgoing exchange with this peer's statements and its own locators.</summary>
    public static async Task AddLocalAsync(MeshMembership mesh, CapabilityExchange local, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(local);
        local.MeshStatements.Clear();
        local.SelfLocators.Clear();
        local.MeshStatements.Add(await mesh.GetStatementsForExchangeAsync(cancellationToken).ConfigureAwait(false));
        local.SelfLocators.Add(mesh.SelfLocators.Select(item => item.Clone()));
    }

    public static async Task<MeshApplyResult?> ApplyAsync(MeshMembership mesh, string remotePeerId, CapabilityExchange? remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (remote is null) return null;
        try
        {
            if (remote.SelfLocators.Count > 0)
                await mesh.ApplySelfLocatorsAsync(remotePeerId, remote.SelfLocators, cancellationToken).ConfigureAwait(false);
            return remote.MeshStatements.Count == 0
                ? null
                : await mesh.ApplyAsync(remotePeerId, remote.MeshStatements, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OriginReplicationLimitException or OriginReplicationTrustException or PeerPersistenceConflictException)
        {
            // A malformed, oversized or untrusted roster is dropped; replication over this session continues.
            return null;
        }
    }
}
