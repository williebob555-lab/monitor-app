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

    /// <summary>True when this side wants to pull alerts addressed to it after replication (phones).</summary>
    bool WantsAlerts => false;

    /// <summary>
    /// Persists a received batch durably and returns the acknowledgement to send, or null when nothing was stored.
    /// The connector sends the acknowledgement only after this returns.
    /// </summary>
    ValueTask<AlertAck?> AcceptAlertBatchAsync(string remotePeerId, AlertBatch batch, CancellationToken cancellationToken) =>
        ValueTask.FromResult<AlertAck?>(null);
}
