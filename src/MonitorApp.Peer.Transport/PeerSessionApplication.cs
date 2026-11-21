using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Transport;

public interface IPeerSessionApplication
{
    CapabilityExchange LocalCapabilities { get; }
    ValueTask OnAuthenticatedAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken);
    IAsyncEnumerable<PeerSessionRequest> GetPendingRequestsAsync(string remotePeerId, CancellationToken cancellationToken);
    ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken);
    ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken);
}
