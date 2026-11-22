using MonitorApp.Peer.Control;
using MonitorApp.Peer.LocalApi;

namespace MonitorApp.Peer.Node.Update;

/// <summary>Adapts the local update controller (the one behind the local API approve endpoint) for remote control.</summary>
public sealed class RemoteUpdateGateway(ILocalUpdateController controller) : IRemoteUpdateGateway
{
    public RemoteUpdateStatus GetStatus()
    {
        var status = controller.GetStatus();
        var staged = status.Stage == "Staged";
        return new RemoteUpdateStatus(
            status.Stage != "Disabled",
            status.InstalledVersion,
            staged ? status.TargetVersion : null,
            staged ? status.TargetDigest : null,
            status.Stage,
            status.LastError,
            status.ApprovalPending);
    }

    public Task<bool> ApproveAsync(string version, string digestHex, CancellationToken cancellationToken) =>
        controller.ApproveAsync(version, digestHex, cancellationToken);
}
