using MonitorApp.Contracts;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Service;

public sealed record PeerSessionOpenResult(
    PeerHello? LocalHello,
    PeerChallenge? Challenge,
    PeerProof? LocalProof,
    ProtocolError? Error = null);

public sealed record PeerAuthenticationResult(bool IsAuthenticated, ProtocolError? Error = null);

public interface IPeerSessionCoordinator
{
    ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken);

    ValueTask<PeerAuthenticationResult> AuthenticateAsync(
        PeerHello remoteHello,
        PeerProof proof,
        CancellationToken cancellationToken);

    ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(
        string remotePeerId,
        CapabilityExchange remoteCapabilities,
        CancellationToken cancellationToken);

    ValueTask<OriginRecordBatch> ReadOriginRangeAsync(
        string remotePeerId,
        OriginRangeRequest request,
        CancellationToken cancellationToken);

    ValueTask<OriginAck> AcceptOriginBatchAsync(
        string remotePeerId,
        OriginRecordBatch batch,
        CancellationToken cancellationToken);

    ValueTask AcceptOriginAckAsync(
        string remotePeerId,
        OriginAck acknowledgement,
        CancellationToken cancellationToken);

    ValueTask<PeerHeartbeat> HeartbeatAsync(
        string remotePeerId,
        PeerHeartbeat heartbeat,
        CancellationToken cancellationToken);

    /// <summary>
    /// Live push/interest state for an authenticated, held-open session. Null means live sessions are unsupported.
    /// </summary>
    IPeerLiveSession? OpenLiveSession(string remotePeerId) => null;

    /// <summary>Alert delivery is additive; coordinators that do not implement it refuse with InvalidMessage.</summary>
    ValueTask<AlertBatch> PullAlertsAsync(
        string remotePeerId,
        AlertPull pull,
        CancellationToken cancellationToken) =>
        throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Alerts are not supported by this peer.");

    ValueTask AcceptAlertAckAsync(
        string remotePeerId,
        AlertAck acknowledgement,
        CancellationToken cancellationToken) =>
        throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Alerts are not supported by this peer.");

    /// <summary>
    /// Remote monitoring control is additive; coordinators that do not implement it refuse with InvalidMessage. The
    /// session id lets the implementation check that the command is bound to this very authenticated session.
    /// </summary>
    ValueTask<ControlResult> ExecuteControlAsync(
        string remotePeerId,
        string sessionId,
        ControlCommand command,
        CancellationToken cancellationToken) =>
        throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Remote control is not supported by this peer.");

    ValueTask<PendingPairingsResponse> ReadPendingPairingsAsync(
        string remotePeerId,
        PendingPairingsRequest request,
        CancellationToken cancellationToken) =>
        throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Remote control is not supported by this peer.");
}

public sealed class PeerPairingSessionContext
{
    public string? RemotePeerId { get; set; }

    public string? PairingId { get; set; }

    public string? ChallengeId { get; set; }

    public string? ChallengeSessionId { get; set; }

    public byte[]? RemotePublicKeyFingerprint { get; set; }

    public DateTimeOffset? PairingExpiresAt { get; set; }

    public bool Completed { get; set; }
}

public interface IPeerPairingCoordinator
{
    ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(
        PeerPairingSessionContext context,
        PairingFrame frame,
        CancellationToken cancellationToken);
}

internal sealed class UnconfiguredPeerSessionCoordinator : IPeerSessionCoordinator
{
    private static readonly ProtocolError Unavailable = new()
    {
        Code = ProtocolErrorCode.Internal,
        SafeMessage = "Peer session coordinator is unavailable.",
        Retryable = true,
    };

    public ValueTask<PeerSessionOpenResult> OpenAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PeerSessionOpenResult(null, null, null, Unavailable.Clone()));

    public ValueTask<PeerAuthenticationResult> AuthenticateAsync(
        PeerHello remoteHello,
        PeerProof proof,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new PeerAuthenticationResult(false, Unavailable.Clone()));

    public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(
        string remotePeerId,
        CapabilityExchange remoteCapabilities,
        CancellationToken cancellationToken) => throw new InvalidOperationException(Unavailable.SafeMessage);

    public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(
        string remotePeerId,
        OriginRangeRequest request,
        CancellationToken cancellationToken) => throw new InvalidOperationException(Unavailable.SafeMessage);

    public ValueTask<OriginAck> AcceptOriginBatchAsync(
        string remotePeerId,
        OriginRecordBatch batch,
        CancellationToken cancellationToken) => throw new InvalidOperationException(Unavailable.SafeMessage);

    public ValueTask AcceptOriginAckAsync(
        string remotePeerId,
        OriginAck acknowledgement,
        CancellationToken cancellationToken) => throw new InvalidOperationException(Unavailable.SafeMessage);

    public ValueTask<PeerHeartbeat> HeartbeatAsync(
        string remotePeerId,
        PeerHeartbeat heartbeat,
        CancellationToken cancellationToken) => throw new InvalidOperationException(Unavailable.SafeMessage);
}

internal sealed class UnconfiguredPeerPairingCoordinator : IPeerPairingCoordinator
{
    public ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(
        PeerPairingSessionContext context,
        PairingFrame frame,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PairingFrame>>(
        [
            new PairingFrame
            {
                Error = new ProtocolError
                {
                    Code = ProtocolErrorCode.Internal,
                    SafeMessage = "Peer pairing coordinator is unavailable.",
                    Retryable = true,
                },
            },
        ]);
}

public sealed class PeerRuntimeState
{
    private int running;

    public bool IsRunning => Volatile.Read(ref running) == 1;

    internal void Started() => Interlocked.Exchange(ref running, 1);

    internal void Stopping() => Interlocked.Exchange(ref running, 0);
}
