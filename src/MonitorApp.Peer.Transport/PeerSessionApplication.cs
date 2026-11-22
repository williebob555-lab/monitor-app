using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Transport;

public interface IPeerSessionApplication
{
    CapabilityExchange LocalCapabilities { get; }
    ValueTask PrepareHelloAsync(PeerHello localHello, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnAuthenticatedAsync(PeerHello remoteHello, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) =>
        OnAuthenticatedAsync(remoteHello.PeerId, remoteCapabilities, cancellationToken);
    ValueTask OnAuthenticatedAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
    IAsyncEnumerable<PeerSessionRequest> GetPendingRequestsAsync(string remotePeerId, CancellationToken cancellationToken);
    ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken);
    ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken);

    /// <summary>
    /// Opts a held-open session into live origin push and live interest. Null keeps the heartbeat-only behaviour.
    /// </summary>
    IPeerLiveSession? OpenLiveSession(string remotePeerId) => null;
}
