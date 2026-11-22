namespace MonitorApp.Peer.LocalApi;

/// <summary>Read-only view of the node's update state for the local API and desktop client.</summary>
public sealed record LocalUpdateStatus(
    string Policy,
    string InstalledVersion,
    string Stage,
    string? TargetVersion,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    string? LastError,
    bool ApprovalPending);

/// <summary>Implemented by the node's update agent; keeps this project independent of the update library.</summary>
public interface ILocalUpdateController
{
    LocalUpdateStatus GetStatus();

    /// <summary>Approve the staged update. Returns false when nothing is staged and awaiting approval.</summary>
    Task<bool> ApproveAsync(CancellationToken cancellationToken);
}
