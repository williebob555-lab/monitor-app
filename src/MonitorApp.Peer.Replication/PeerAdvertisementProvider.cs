using MonitorApp.Contracts.V1;

// Namespace is intentionally unchanged from when this contract lived in the
// ASP.NET Core peer service; the type moved to a plain project so non-web hosts
// (Android) can reuse the sync application.
namespace MonitorApp.Peer.Service;

public interface IPeerAdvertisementProvider
{
    ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken);
}
