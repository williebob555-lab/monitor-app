namespace MonitorApp.Peer.Control;

/// <summary>The desktop's update state as exposed to a trusted phone (no URLs, paths or other host detail).</summary>
public sealed record RemoteUpdateStatus(
    bool Available,
    string InstalledVersion,
    string? StagedVersion,
    string? StagedDigestHex,
    string State,
    string? LastError,
    bool ApprovalPending);

/// <summary>
/// The desktop's update agent as seen by remote control. Implemented in the node host over the same controller the local
/// API's approve endpoint uses, so a remote approval takes exactly the local approval path.
/// </summary>
public interface IRemoteUpdateGateway
{
    RemoteUpdateStatus GetStatus();

    /// <summary>Approves installation only if the staged update is exactly this version and artifact digest.</summary>
    Task<bool> ApproveAsync(string version, string digestHex, CancellationToken cancellationToken);
}
