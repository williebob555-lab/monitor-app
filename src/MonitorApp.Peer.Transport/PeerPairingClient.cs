using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Transport;

public sealed record PeerInitialPairingResult(PairingRemoteCandidate Remote, PairingConfirmation Confirmation);

public sealed class PeerPairingClient : IAsyncDisposable
{
    private readonly PeerTransportOptions transportOptions;
    private readonly PeerInitialPairingOptions pairingOptions;
    private readonly IPeerInitialPairingCoordinator coordinator;
    private readonly IPeerReconnectDelay pollDelay;
    private readonly GrpcChannel channel;

    public PeerPairingClient(PeerTransportOptions transportOptions, IPeerInitialPairingCoordinator coordinator,
        PeerInitialPairingOptions? pairingOptions = null, IPeerReconnectDelay? pollDelay = null)
    {
        this.transportOptions = transportOptions ?? throw new ArgumentNullException(nameof(transportOptions));
        this.transportOptions.Validate();
        this.pairingOptions = pairingOptions ?? PeerInitialPairingOptions.Default;
        this.pairingOptions.Validate();
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.pollDelay = pollDelay ?? new PeerReconnectDelay();
        channel = GrpcChannel.ForAddress(transportOptions.Endpoint.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = transportOptions.ConnectTimeout, EnableMultipleHttp2Connections = true },
            MaxReceiveMessageSize = transportOptions.MaximumMessageBytes,
            MaxSendMessageSize = transportOptions.MaximumMessageBytes,
        });
    }

    public async Task<PeerInitialPairingResult> PairAsync(PeerInitialPairingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PairingId);
        if (!request.InitiatingUserApproved)
            throw new PeerAuthenticationException("Initial pairing requires explicit approval by the initiating local user.");
        if (request.ExpectedRemotePublicKeySha256 is not null && request.ExpectedRemotePublicKeySha256.Length != PeerContractRules.Sha256Size)
            throw new ArgumentException("Expected remote fingerprint must contain 32 bytes.", nameof(request));

        var localHello = coordinator.CreateLocalHello(request.PairingId);
        RequireValid(PeerContractValidator.Validate(localHello), "Local pairing hello is invalid.");
        using var call = new PeerPairingService.PeerPairingServiceClient(channel).Pair(cancellationToken: cancellationToken);
        PairingHello? pinnedHello = null;
        PairingRemoteCandidate? candidate = null;
        var delay = pairingOptions.InitialPollDelay;
        for (var poll = 0; poll < pairingOptions.MaximumApprovalPolls; poll++)
        {
            await WriteAsync(call, new PairingFrame { Hello = localHello.Clone() }, cancellationToken).ConfigureAwait(false);
            var remoteFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
            ThrowIfFailure(remoteFrame);
            if (remoteFrame.MessageCase != PairingFrame.MessageOneofCase.Hello || remoteFrame.Hello.PairingId != request.PairingId)
                throw new PeerAuthenticationException("Peer returned an invalid pairing hello.");
            if (pinnedHello is null)
            {
                candidate = await coordinator.ApproveAndPinRemoteAsync(remoteFrame.Hello,
                    transportOptions.Endpoint.ExpectedPeerId, request.ExpectedRemotePublicKeySha256, cancellationToken).ConfigureAwait(false);
                pinnedHello = remoteFrame.Hello.Clone();
            }
            else if (!SameIdentity(pinnedHello, remoteFrame.Hello))
            {
                throw new PeerAuthenticationException("Remote pairing identity changed while awaiting approval.");
            }

            var approvalFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
            ThrowIfFailure(approvalFrame);
            if (approvalFrame.MessageCase != PairingFrame.MessageOneofCase.Approval ||
                !PeerContractValidator.Validate(approvalFrame.Approval).IsValid || approvalFrame.Approval.PairingId != request.PairingId)
                throw new PeerAuthenticationException("Peer returned invalid pairing approval status.");
            var approval = approvalFrame.Approval;
            if (approval.ExpiresAt.ToDateTimeOffset() < DateTimeOffset.UtcNow || approval.Disposition == PairingApprovalDisposition.Expired)
                throw new PeerAuthenticationException("Remote pairing approval expired.");
            if (approval.Disposition == PairingApprovalDisposition.Rejected)
                throw new PeerAuthenticationException("Remote user rejected pairing.");
            if (approval.Disposition == PairingApprovalDisposition.Approved)
                return await CompleteAsync(call, request.PairingId, localHello, pinnedHello, candidate!, cancellationToken).ConfigureAwait(false);
            if (approval.Disposition != PairingApprovalDisposition.AwaitingLocalApproval)
                throw new PeerAuthenticationException("Peer returned an unexpected pairing approval state.");
            if (poll + 1 == pairingOptions.MaximumApprovalPolls) break;
            await pollDelay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            delay = delay.Ticks >= pairingOptions.MaximumPollDelay.Ticks / 2
                ? pairingOptions.MaximumPollDelay
                : TimeSpan.FromTicks(delay.Ticks * 2);
        }

        throw new TimeoutException("Remote pairing approval was not received within the configured polling bound.");
    }

    public ValueTask DisposeAsync() { channel.Dispose(); return ValueTask.CompletedTask; }

    private async Task<PeerInitialPairingResult> CompleteAsync(AsyncDuplexStreamingCall<PairingFrame, PairingFrame> call,
        string pairingId, PairingHello localHello, PairingHello remoteHello, PairingRemoteCandidate candidate,
        CancellationToken cancellationToken)
    {
        var challengeFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
        ThrowIfFailure(challengeFrame);
        var proofFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
        ThrowIfFailure(proofFrame);
        if (challengeFrame.MessageCase != PairingFrame.MessageOneofCase.Challenge ||
            proofFrame.MessageCase != PairingFrame.MessageOneofCase.Proof)
            throw new PeerAuthenticationException("Peer did not return the bilateral pairing proof.");
        var proof = await coordinator.VerifyRemoteAndCreateLocalProofAsync(localHello, remoteHello,
            challengeFrame.Challenge, proofFrame.Proof, cancellationToken).ConfigureAwait(false);
        RequireValid(PeerContractValidator.Validate(proof), "Local pairing proof is invalid.");
        await WriteAsync(call, new PairingFrame { Proof = proof }, cancellationToken).ConfigureAwait(false);

        var confirmationFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
        ThrowIfFailure(confirmationFrame);
        var completedFrame = await ReadAsync(call, cancellationToken).ConfigureAwait(false);
        ThrowIfFailure(completedFrame);
        if (confirmationFrame.MessageCase != PairingFrame.MessageOneofCase.Confirmation ||
            confirmationFrame.Confirmation.PairingId != pairingId ||
            completedFrame.MessageCase != PairingFrame.MessageOneofCase.Approval ||
            completedFrame.Approval.PairingId != pairingId ||
            completedFrame.Approval.Disposition != PairingApprovalDisposition.Completed)
            throw new PeerAuthenticationException("Peer did not return a valid completed pairing confirmation.");
        await coordinator.PersistTrustAsync(candidate, confirmationFrame.Confirmation, cancellationToken).ConfigureAwait(false);
        return new PeerInitialPairingResult(candidate, confirmationFrame.Confirmation.Clone());
    }

    private async Task WriteAsync(AsyncDuplexStreamingCall<PairingFrame, PairingFrame> call, PairingFrame frame,
        CancellationToken cancellationToken) =>
        await call.RequestStream.WriteAsync(frame, cancellationToken).WaitAsync(transportOptions.OperationTimeout, cancellationToken).ConfigureAwait(false);

    private async Task<PairingFrame> ReadAsync(AsyncDuplexStreamingCall<PairingFrame, PairingFrame> call, CancellationToken cancellationToken)
    {
        if (!await call.ResponseStream.MoveNext(cancellationToken).WaitAsync(transportOptions.OperationTimeout, cancellationToken).ConfigureAwait(false))
            throw new PeerTransportException("Peer closed the pairing stream unexpectedly.");
        return call.ResponseStream.Current;
    }

    private static void ThrowIfFailure(PairingFrame frame)
    {
        if (frame.MessageCase == PairingFrame.MessageOneofCase.Incompatibility)
            throw new PeerCompatibilityException(frame.Incompatibility.SafeMessage);
        if (frame.MessageCase == PairingFrame.MessageOneofCase.Error)
            throw new PeerAuthenticationException(frame.Error.SafeMessage);
    }

    private static bool SameIdentity(PairingHello left, PairingHello right) =>
        left.PairingId == right.PairingId && left.PeerId == right.PeerId && left.DisplayName == right.DisplayName &&
        left.PublicKeySpki.Span.SequenceEqual(right.PublicKeySpki.Span) &&
        left.PublicKeySha256.Span.SequenceEqual(right.PublicKeySha256.Span);

    private static void RequireValid(ContractValidationResult result, string message)
    {
        if (!result.IsValid) throw new PeerTransportException(message);
    }
}
