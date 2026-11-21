using System.Net.Http;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Transport;

public sealed class PeerGrpcConnector : IAsyncDisposable
{
    private readonly PeerTransportOptions options;
    private readonly IPeerSessionAuthenticationProvider authentication;
    private readonly IPeerSessionApplication application;
    private readonly IPeerReconnectDelay reconnectDelay;
    private readonly GrpcChannel channel;
    private int state = (int)PeerTransportState.Stopped;

    public PeerGrpcConnector(PeerTransportOptions options, IPeerSessionAuthenticationProvider authentication,
        IPeerSessionApplication application, IPeerReconnectDelay? reconnectDelay = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options)); this.options.Validate();
        this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.reconnectDelay = reconnectDelay ?? new PeerReconnectDelay();
        channel = GrpcChannel.ForAddress(options.Endpoint.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout, EnableMultipleHttp2Connections = true },
            MaxReceiveMessageSize = options.MaximumMessageBytes,
            MaxSendMessageSize = options.MaximumMessageBytes,
        });
    }

    public PeerTransportState State => (PeerTransportState)Volatile.Read(ref state);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = options.InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(cancellationToken).ConfigureAwait(false);
                delay = options.InitialReconnectDelay;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (PeerCompatibilityException) { SetState(PeerTransportState.Stopped); throw; }
            catch (PeerAuthenticationException) { SetState(PeerTransportState.Stopped); throw; }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException or TimeoutException or PeerTransportException)
            {
                SetState(PeerTransportState.BackingOff);
                try
                {
                    await reconnectDelay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                delay = delay.Ticks >= options.MaximumReconnectDelay.Ticks / 2
                    ? options.MaximumReconnectDelay
                    : TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }

        SetState(PeerTransportState.Stopped);
    }

    public async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        SetState(PeerTransportState.Connecting);
        using var call = new PeerService.PeerServiceClient(channel).PeerSession(cancellationToken: cancellationToken);
        var sentHello = await authentication.CreateHelloAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        RequireHello(sentHello);
        SetState(PeerTransportState.Authenticating);
        await WriteAsync(call, new PeerSessionRequest { Hello = sentHello }, cancellationToken).ConfigureAwait(false);

        var hello = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.Hello, cancellationToken).ConfigureAwait(false);
        var challenge = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.Challenge, cancellationToken).ConfigureAwait(false);
        var serverProof = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.Proof, cancellationToken).ConfigureAwait(false);
        var clientProof = await authentication.VerifyServerAndCreateClientProofAsync(sentHello, hello.Hello, challenge.Challenge,
            serverProof.Proof, options.Endpoint.ExpectedPeerId, cancellationToken).ConfigureAwait(false);
        RequireValid(PeerContractValidator.Validate(clientProof), "Local peer proof is invalid.");
        await WriteAsync(call, new PeerSessionRequest { Proof = clientProof }, cancellationToken).ConfigureAwait(false);
        await WriteAsync(call, new PeerSessionRequest { CapabilityExchange = application.LocalCapabilities.Clone() }, cancellationToken).ConfigureAwait(false);
        var capabilities = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.CapabilityExchange, cancellationToken).ConfigureAwait(false);

        SetState(PeerTransportState.Authenticated);
        await application.OnAuthenticatedAsync(hello.Hello.PeerId, capabilities.CapabilityExchange, cancellationToken).ConfigureAwait(false);
        await foreach (var request in application.GetPendingRequestsAsync(hello.Hello.PeerId, cancellationToken).WithCancellation(cancellationToken))
            await DispatchAsync(call, hello.Hello.PeerId, request, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            await WriteAsync(call, new PeerSessionRequest { Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } }, cancellationToken).ConfigureAwait(false);
            _ = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.Heartbeat, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() { channel.Dispose(); return ValueTask.CompletedTask; }

    private async Task DispatchAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call, string remotePeerId,
        PeerSessionRequest request, CancellationToken cancellationToken)
    {
        switch (request.MessageCase)
        {
            case PeerSessionRequest.MessageOneofCase.OriginRangeRequest:
                RequireValid(PeerContractValidator.Validate(request.OriginRangeRequest), "Local origin range request is invalid.");
                await WriteAsync(call, request, cancellationToken).ConfigureAwait(false);
                var batch = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.OriginBatch, cancellationToken).ConfigureAwait(false);
                var acknowledgement = await application.AcceptOriginBatchAsync(remotePeerId, batch.OriginBatch, cancellationToken).ConfigureAwait(false);
                RequireValid(PeerContractValidator.Validate(acknowledgement), "Local origin acknowledgement is invalid.");
                await WriteAsync(call, new PeerSessionRequest { OriginAck = acknowledgement }, cancellationToken).ConfigureAwait(false);
                break;
            case PeerSessionRequest.MessageOneofCase.OriginBatch:
                RequireValid(PeerContractValidator.Validate(request.OriginBatch), "Local origin batch is invalid.");
                await WriteAsync(call, request, cancellationToken).ConfigureAwait(false);
                var ack = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.OriginAck, cancellationToken).ConfigureAwait(false);
                await application.AcceptOriginAckAsync(remotePeerId, ack.OriginAck, cancellationToken).ConfigureAwait(false);
                break;
            case PeerSessionRequest.MessageOneofCase.OriginAck:
                RequireValid(PeerContractValidator.Validate(request.OriginAck), "Local origin acknowledgement is invalid.");
                await WriteAsync(call, request, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new PeerTransportException("Application requests are restricted to authenticated replication operations.");
        }
    }

    private async Task WriteAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call,
        PeerSessionRequest request, CancellationToken cancellationToken) =>
        await call.RequestStream.WriteAsync(request, cancellationToken).WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);

    private async Task<PeerSessionResponse> ReadExpectedAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call,
        PeerSessionResponse.MessageOneofCase expected, CancellationToken cancellationToken)
    {
        if (!await call.ResponseStream.MoveNext(cancellationToken).WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false))
            throw new PeerTransportException("Peer closed the session unexpectedly.");
        var response = call.ResponseStream.Current;
        if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Incompatibility)
            throw new PeerCompatibilityException(response.Incompatibility.SafeMessage);
        if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Error)
        {
            if (response.Error.Retryable)
            {
                throw new PeerTransportException(response.Error.SafeMessage);
            }

            throw new PeerAuthenticationException(response.Error.SafeMessage);
        }
        if (response.MessageCase != expected) throw new PeerTransportException("Peer returned an unexpected session response.");
        return response;
    }

    private static void RequireValid(ContractValidationResult result, string message)
    {
        if (!result.IsValid) throw new PeerTransportException(message);
    }

    private static void RequireHello(PeerHello hello)
    {
        if (hello.ProtocolVersion is null || hello.ProtocolVersion.Major == 0 ||
            string.IsNullOrWhiteSpace(hello.PeerId) || string.IsNullOrWhiteSpace(hello.SessionId) ||
            hello.PublicKeySha256.Length != PeerContractRules.Sha256Size)
        {
            throw new PeerTransportException("Local peer hello is invalid.");
        }
    }

    private void SetState(PeerTransportState value) => Volatile.Write(ref state, (int)value);
}
