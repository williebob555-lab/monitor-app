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
    private int established;

    public PeerGrpcConnector(PeerTransportOptions options, IPeerSessionAuthenticationProvider authentication,
        IPeerSessionApplication application, IPeerReconnectDelay? reconnectDelay = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options)); this.options.Validate();
        this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        this.reconnectDelay = reconnectDelay ?? new PeerReconnectDelay();
        channel = GrpcChannel.ForAddress(options.Endpoint.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = options.ConnectTimeout,
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
            },
            MaxReceiveMessageSize = options.MaximumMessageBytes,
            MaxSendMessageSize = options.MaximumMessageBytes,
        });
    }

    /// <summary>Raised on every state transition (possibly from a background thread); handlers must not throw.</summary>
    public event Action<PeerTransportState>? StateChanged;

    /// <summary>Raised with the failure that ended a session, before any reconnect delay.</summary>
    public event Action<Exception>? SessionFailed;

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
            catch (PeerCompatibilityException exception) { SetState(PeerTransportState.Stopped); SessionFailed?.Invoke(exception); throw; }
            catch (PeerAuthenticationException exception) { SetState(PeerTransportState.Stopped); SessionFailed?.Invoke(exception); throw; }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException or TimeoutException or PeerTransportException)
            {
                SetState(PeerTransportState.BackingOff);
                SessionFailed?.Invoke(exception);
                if (Interlocked.Exchange(ref established, 0) == 1)
                {
                    // The session ran before dropping: reconnect promptly, then back off again if it keeps failing.
                    delay = options.InitialReconnectDelay;
                }

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

    public Task RunSessionAsync(CancellationToken cancellationToken) => RunSessionCoreAsync(keepAlive: true, cancellationToken);

    /// <summary>
    /// Authenticates, drains every pending replication operation in both directions, then closes the
    /// session. Outbound-only peers (phones) call this repeatedly instead of holding a heartbeat session open.
    /// </summary>
    public Task SyncOnceAsync(CancellationToken cancellationToken) => RunSessionCoreAsync(keepAlive: false, cancellationToken);

    /// <summary>
    /// Opens one short authenticated session and sends a single remote-control command that is signed for that very
    /// session: <paramref name="createCommand"/> receives the session id (chosen before connecting, so a slow biometric
    /// prompt happens before any connection is held open) and must return the fully signed command.
    /// </summary>
    public async Task<ControlResult> SendControlAsync(
        Func<string, CancellationToken, Task<ControlCommand>> createCommand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createCommand);
        var sessionId = Guid.NewGuid().ToString("N");
        var command = await createCommand(sessionId, cancellationToken).ConfigureAwait(false);
        using var call = await OpenAuthenticatedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await WriteAsync(call.Call, new PeerSessionRequest { ControlCommand = command }, cancellationToken).ConfigureAwait(false);
        var response = await ReadExpectedAsync(call.Call, PeerSessionResponse.MessageOneofCase.ControlResult, cancellationToken)
            .ConfigureAwait(false);
        await call.Call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        SetState(PeerTransportState.Stopped);
        return response.ControlResult;
    }

    /// <summary>Asks a trusted desktop for the pairing requests it is holding (read-only summaries).</summary>
    public async Task<PendingPairingsResponse> ReadPendingPairingsAsync(CancellationToken cancellationToken)
    {
        using var call = await OpenAuthenticatedAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var request = new PendingPairingsRequest { RequestId = Guid.NewGuid().ToString("N") };
        await WriteAsync(call.Call, new PeerSessionRequest { PendingPairingsRequest = request }, cancellationToken).ConfigureAwait(false);
        var response = await ReadExpectedAsync(call.Call, PeerSessionResponse.MessageOneofCase.PendingPairingsResponse, cancellationToken)
            .ConfigureAwait(false);
        await call.Call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        SetState(PeerTransportState.Stopped);
        return response.PendingPairingsResponse;
    }

    /// <summary>Asks a trusted desktop for its update state (installed/staged version and digest); read-only.</summary>
    public async Task<UpdateStatusResponse> ReadUpdateStatusAsync(CancellationToken cancellationToken)
    {
        using var call = await OpenAuthenticatedAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var request = new UpdateStatusRequest { RequestId = Guid.NewGuid().ToString("N") };
        await WriteAsync(call.Call, new PeerSessionRequest { UpdateStatusRequest = request }, cancellationToken).ConfigureAwait(false);
        var response = await ReadExpectedAsync(call.Call, PeerSessionResponse.MessageOneofCase.UpdateStatusResponse, cancellationToken)
            .ConfigureAwait(false);
        await call.Call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        SetState(PeerTransportState.Stopped);
        return response.UpdateStatusResponse;
    }

    /// <summary>Asks a trusted desktop for this phone's own alert preferences; read-only.</summary>
    public async Task<AlertPreferencesResponse> ReadAlertPreferencesAsync(CancellationToken cancellationToken)
    {
        using var call = await OpenAuthenticatedAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var request = new AlertPreferencesRequest { RequestId = Guid.NewGuid().ToString("N") };
        await WriteAsync(call.Call, new PeerSessionRequest { AlertPreferencesRequest = request }, cancellationToken).ConfigureAwait(false);
        var response = await ReadExpectedAsync(call.Call, PeerSessionResponse.MessageOneofCase.AlertPreferencesResponse, cancellationToken)
            .ConfigureAwait(false);
        await call.Call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        SetState(PeerTransportState.Stopped);
        return response.AlertPreferencesResponse;
    }

    /// <summary>Asks a trusted desktop for its effective alert rules (defaults plus overrides); read-only.</summary>
    public async Task<AlertRulesResponse> ReadAlertRulesAsync(CancellationToken cancellationToken)
    {
        using var call = await OpenAuthenticatedAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var request = new AlertRulesRequest { RequestId = Guid.NewGuid().ToString("N") };
        await WriteAsync(call.Call, new PeerSessionRequest { AlertRulesRequest = request }, cancellationToken).ConfigureAwait(false);
        var response = await ReadExpectedAsync(call.Call, PeerSessionResponse.MessageOneofCase.AlertRulesResponse, cancellationToken)
            .ConfigureAwait(false);
        await call.Call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
        SetState(PeerTransportState.Stopped);
        return response.AlertRulesResponse;
    }

    private sealed class SessionCall(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call, PeerHello remote, CapabilityExchange capabilities)
        : IDisposable
    {
        public AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> Call { get; } = call;

        public PeerHello Remote { get; } = remote;

        public CapabilityExchange Capabilities { get; } = capabilities;

        public void Dispose() => Call.Dispose();
    }

    private async Task<SessionCall> OpenAuthenticatedAsync(string sessionId, CancellationToken cancellationToken)
    {
        SetState(PeerTransportState.Connecting);
        var call = new PeerService.PeerServiceClient(channel).PeerSession(cancellationToken: cancellationToken);
        try
        {
            var (_, hello, capabilities) = await HandshakeAsync(call, sessionId, cancellationToken).ConfigureAwait(false);
            return new SessionCall(call, hello, capabilities);
        }
        catch
        {
            call.Dispose();
            throw;
        }
    }

    private async Task<(PeerHello Sent, PeerHello Remote, CapabilityExchange Capabilities)> HandshakeAsync(
        AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var sentHello = await authentication.CreateHelloAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await application.PrepareHelloAsync(sentHello, cancellationToken).ConfigureAwait(false);
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
        return (sentHello, hello.Hello, capabilities.CapabilityExchange);
    }

    private async Task RunSessionCoreAsync(bool keepAlive, CancellationToken cancellationToken)
    {
        SetState(PeerTransportState.Connecting);
        using var call = new PeerService.PeerServiceClient(channel).PeerSession(cancellationToken: cancellationToken);
        var sentHello = await authentication.CreateHelloAsync(Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        await application.PrepareHelloAsync(sentHello, cancellationToken).ConfigureAwait(false);
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
        Volatile.Write(ref established, 1);
        await application.OnAuthenticatedAsync(hello.Hello, capabilities.CapabilityExchange, cancellationToken).ConfigureAwait(false);
        await foreach (var request in application.GetPendingRequestsAsync(hello.Hello.PeerId, cancellationToken).WithCancellation(cancellationToken))
            await DispatchAsync(call, hello.Hello.PeerId, request, cancellationToken).ConfigureAwait(false);

        if (application.WantsAlerts)
            await PullAlertsAsync(call, hello.Hello.PeerId, cancellationToken).ConfigureAwait(false);

        if (!keepAlive)
        {
            await call.RequestStream.CompleteAsync().WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false);
            // Wait for the remote to finish processing (the alert acknowledgement has no reply of its own).
            while (await call.ResponseStream.MoveNext(cancellationToken).WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false)) { }
            SetState(PeerTransportState.Stopped);
            return;
        }

        var live = application.OpenLiveSession(hello.Hello.PeerId);
        if (live is not null)
        {
            using (live)
            {
                await RunLiveAsync(call, hello.Hello.PeerId, live, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            await WriteAsync(call, new PeerSessionRequest { Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } }, cancellationToken).ConfigureAwait(false);
            _ = await ReadExpectedAsync(call, PeerSessionResponse.MessageOneofCase.Heartbeat, cancellationToken).ConfigureAwait(false);
        }
    }

    private const int MaximumAlertPages = 16;

    /// <summary>
    /// Safety-net pull on a live phone session. The desktop pushes new alerts the moment they are queued, so this only
    /// covers a missed wakeup (and a desktop that cannot push); acknowledgement still follows a durable write.
    /// </summary>
    private static readonly TimeSpan LiveAlertPullInterval = TimeSpan.FromSeconds(30);

    private async Task PullAlertsAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call,
        string remotePeerId, CancellationToken cancellationToken)
    {
        for (var page = 0; page < MaximumAlertPages; page++)
        {
            var pull = new AlertPull
            {
                RequestId = Guid.NewGuid().ToString("N"),
                MaximumCount = (uint)PeerContractRules.MaximumAlertsPerBatch,
            };
            await WriteAsync(call, new PeerSessionRequest { AlertPull = pull }, cancellationToken).ConfigureAwait(false);
            if (!await call.ResponseStream.MoveNext(cancellationToken).WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false))
                throw new PeerTransportException("Peer closed the session unexpectedly.");
            var response = call.ResponseStream.Current;
            // A peer that predates alert delivery refuses with InvalidMessage; that must not fail replication.
            if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Error &&
                response.Error.Code == ProtocolErrorCode.InvalidMessage)
                return;
            if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Error)
                throw response.Error.Retryable
                    ? new PeerTransportException(response.Error.SafeMessage)
                    : new PeerAuthenticationException(response.Error.SafeMessage);
            if (response.MessageCase != PeerSessionResponse.MessageOneofCase.AlertBatch || response.AlertBatch.RequestId != pull.RequestId)
                throw new PeerTransportException("Peer returned an unexpected session response.");
            RequireValid(PeerContractValidator.Validate(response.AlertBatch), "Peer alert batch is invalid.");
            var ack = await application.AcceptAlertBatchAsync(remotePeerId, response.AlertBatch, cancellationToken).ConfigureAwait(false);
            if (ack is not null)
            {
                RequireValid(PeerContractValidator.Validate(ack), "Local alert acknowledgement is invalid.");
                await WriteAsync(call, new PeerSessionRequest { AlertAck = ack }, cancellationToken).ConfigureAwait(false);
            }

            if (!response.AlertBatch.HasMore)
                return;
        }
    }

    public ValueTask DisposeAsync() { channel.Dispose(); return ValueTask.CompletedTask; }

    private async Task RunLiveAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call, string remotePeerId,
        IPeerLiveSession live, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writeGate = new SemaphoreSlim(1, 1);
        var token = linked.Token;
        var lastInbound = Environment.TickCount64;
        var alertPullOutstanding = 0;
        var alertPullDue = 0;
        var alertsUnsupported = 0;

        async Task SendAsync(PeerSessionRequest request)
        {
            await writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await WriteAsync(call, request, token).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        // Always announce the live session: the server then applies a rate (not lifetime) message bound and an idle
        // timeout; only outbound-only peers also ask it to push its origin records and interests.
        await SendAsync(new PeerSessionRequest
        {
            LiveSubscribe = new LiveSubscribe
            {
                PushOrigin = live.RequestRemotePush,
                PushInterests = live.RequestRemotePush,
                MaximumInFlightBatches = (uint)live.MaximumInFlightBatches,
            },
        }).ConfigureAwait(false);

        async Task ReadLoopAsync()
        {
            while (await call.ResponseStream.MoveNext(token).ConfigureAwait(false))
            {
                Volatile.Write(ref lastInbound, Environment.TickCount64);
                var response = call.ResponseStream.Current;
                // A peer that predates alert delivery refuses a pull with InvalidMessage; that must not end the session.
                if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Error &&
                    response.Error.Code == ProtocolErrorCode.InvalidMessage &&
                    Interlocked.Exchange(ref alertPullOutstanding, 0) == 1)
                {
                    Volatile.Write(ref alertsUnsupported, 1);
                    continue;
                }

                if (IsRangePruned(response))
                {
                    // The origin's push hit a range it pruned: record it as absent, tell it where this replica resumes,
                    // then end the session so the next one starts after the range.
                    var prunedAck = await application.AcceptRangePrunedAsync(remotePeerId, response.Error.PrunedRange, token).ConfigureAwait(false);
                    if (prunedAck is not null)
                    {
                        try { await SendAsync(new PeerSessionRequest { OriginAck = prunedAck }).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is RpcException or IOException or InvalidOperationException) { }
                        throw new PeerTransportException("Peer pruned part of its origin stream; resuming after the pruned range.");
                    }
                }

                ThrowIfFailure(response);
                switch (response.MessageCase)
                {
                    case PeerSessionResponse.MessageOneofCase.Heartbeat:
                        break;
                    case PeerSessionResponse.MessageOneofCase.AlertBatch:
                        // An unsolicited push (empty request id) does not answer the outstanding pull.
                        if (response.AlertBatch.RequestId.Length > 0) Volatile.Write(ref alertPullOutstanding, 0);
                        RequireValid(PeerContractValidator.Validate(response.AlertBatch), "Peer alert batch is invalid.");
                        var alertAck = await application.AcceptAlertBatchAsync(remotePeerId, response.AlertBatch, token).ConfigureAwait(false);
                        if (alertAck is not null)
                        {
                            RequireValid(PeerContractValidator.Validate(alertAck), "Local alert acknowledgement is invalid.");
                            await SendAsync(new PeerSessionRequest { AlertAck = alertAck }).ConfigureAwait(false);
                        }

                        if (response.AlertBatch.HasMore) Volatile.Write(ref alertPullDue, 1);
                        live.Nudge();
                        break;
                    case PeerSessionResponse.MessageOneofCase.OriginBatch:
                        RequireValid(PeerContractValidator.Validate(response.OriginBatch), "Peer pushed an invalid origin batch.");
                        var acknowledgement = await application.AcceptOriginBatchAsync(remotePeerId, response.OriginBatch, token).ConfigureAwait(false);
                        RequireValid(PeerContractValidator.Validate(acknowledgement), "Local origin acknowledgement is invalid.");
                        await SendAsync(new PeerSessionRequest { OriginAck = acknowledgement }).ConfigureAwait(false);
                        break;
                    case PeerSessionResponse.MessageOneofCase.OriginAck:
                        RequireValid(PeerContractValidator.Validate(response.OriginAck), "Peer origin acknowledgement is invalid.");
                        await application.AcceptOriginAckAsync(remotePeerId, response.OriginAck, token).ConfigureAwait(false);
                        live.OnBatchAcknowledged(response.OriginAck.Cursor);
                        break;
                    case PeerSessionResponse.MessageOneofCase.LiveSubscribeAck:
                        if (!response.LiveSubscribeAck.Accepted)
                            throw new PeerTransportException("Peer refused the live session subscription.");
                        break;
                    case PeerSessionResponse.MessageOneofCase.LiveInterest:
                        var interestAck = await live.AcceptRemoteInterestAsync(response.LiveInterest, token).ConfigureAwait(false);
                        await SendAsync(new PeerSessionRequest { LiveInterestAck = interestAck }).ConfigureAwait(false);
                        break;
                    case PeerSessionResponse.MessageOneofCase.LiveInterestAck:
                        break;
                    default:
                        throw new PeerTransportException("Peer returned an unexpected live session response.");
                }
            }

            throw new PeerTransportException("Peer closed the session unexpectedly.");
        }

        async Task SendLoopAsync()
        {
            var nextHeartbeat = DateTime.UtcNow + options.HeartbeatInterval;
            var nextAlertPull = DateTime.UtcNow;
            var deadAfter = options.HeartbeatInterval * 3 + options.OperationTimeout;
            while (!token.IsCancellationRequested)
            {
                if (live.RenewRequested)
                    throw new PeerTransportException("Mesh membership changed; renewing the session to exchange it.");
                var progressed = false;
                while (await live.TryNextBatchAsync(token).ConfigureAwait(false) is { } batch)
                {
                    RequireValid(PeerContractValidator.Validate(batch), "Local origin batch is invalid.");
                    await SendAsync(new PeerSessionRequest { OriginBatch = batch }).ConfigureAwait(false);
                    progressed = true;
                }

                foreach (var interest in live.DrainOutgoingInterests())
                {
                    await SendAsync(new PeerSessionRequest { LiveInterest = interest }).ConfigureAwait(false);
                    progressed = true;
                }

                var now = DateTime.UtcNow;
                if (application.WantsAlerts && Volatile.Read(ref alertsUnsupported) == 0 &&
                    Volatile.Read(ref alertPullOutstanding) == 0 && (now >= nextAlertPull || Volatile.Read(ref alertPullDue) == 1))
                {
                    Volatile.Write(ref alertPullDue, 0);
                    Volatile.Write(ref alertPullOutstanding, 1);
                    nextAlertPull = now + LiveAlertPullInterval;
                    await SendAsync(new PeerSessionRequest
                    {
                        AlertPull = new AlertPull
                        {
                            RequestId = Guid.NewGuid().ToString("N"),
                            MaximumCount = (uint)PeerContractRules.MaximumAlertsPerBatch,
                        },
                    }).ConfigureAwait(false);
                    progressed = true;
                }

                if (now >= nextHeartbeat)
                {
                    await SendAsync(new PeerSessionRequest
                    {
                        Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
                    }).ConfigureAwait(false);
                    nextHeartbeat = now + options.HeartbeatInterval;
                }

                if (TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref lastInbound)) > deadAfter)
                    throw new PeerTransportException("Peer session is unresponsive.");
                if (!progressed)
                {
                    var wait = nextHeartbeat - DateTime.UtcNow;
                    if (application.WantsAlerts && nextAlertPull - DateTime.UtcNow is var untilPull && untilPull < wait)
                        wait = untilPull;
                    var poll = TimeSpan.FromSeconds(5);
                    await live.WaitForActivityAsync(wait < poll ? wait : poll, token).ConfigureAwait(false);
                }
            }
        }

        var reader = Task.Run(ReadLoopAsync, CancellationToken.None);
        var sender = Task.Run(SendLoopAsync, CancellationToken.None);
        await Task.WhenAny(reader, sender).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { reader, sender })
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // The sibling loop ended the session; its own failure (if any) is rethrown by its own await.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ThrowIfFailure(PeerSessionResponse response)
    {
        if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Incompatibility)
            throw new PeerCompatibilityException(response.Incompatibility.SafeMessage);
        if (response.MessageCase == PeerSessionResponse.MessageOneofCase.Error)
        {
            if (response.Error.Retryable) throw new PeerTransportException(response.Error.SafeMessage);
            throw new PeerAuthenticationException(response.Error.SafeMessage);
        }
    }

    private async Task DispatchAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call, string remotePeerId,
        PeerSessionRequest request, CancellationToken cancellationToken)
    {
        switch (request.MessageCase)
        {
            case PeerSessionRequest.MessageOneofCase.OriginRangeRequest:
                RequireValid(PeerContractValidator.Validate(request.OriginRangeRequest), "Local origin range request is invalid.");
                await WriteAsync(call, request, cancellationToken).ConfigureAwait(false);
                var batch = await ReadOriginBatchOrPrunedAsync(call, cancellationToken).ConfigureAwait(false);
                OriginAck acknowledgement;
                if (batch.MessageCase == PeerSessionResponse.MessageOneofCase.Error)
                {
                    // RANGE_PRUNED: the origin removed the range by retention. Mark it intentionally absent and resume after it.
                    acknowledgement = await application.AcceptRangePrunedAsync(remotePeerId, batch.Error.PrunedRange, cancellationToken).ConfigureAwait(false)
                        ?? throw new PeerAuthenticationException(batch.Error.SafeMessage);
                }
                else
                {
                    acknowledgement = await application.AcceptOriginBatchAsync(remotePeerId, batch.OriginBatch, cancellationToken).ConfigureAwait(false);
                }

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

    /// <summary>Reads the reply to an origin range request: a batch, or a RANGE_PRUNED error that names the pruned range.</summary>
    private async Task<PeerSessionResponse> ReadOriginBatchOrPrunedAsync(
        AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call, CancellationToken cancellationToken)
    {
        if (!await call.ResponseStream.MoveNext(cancellationToken).WaitAsync(options.OperationTimeout, cancellationToken).ConfigureAwait(false))
            throw new PeerTransportException("Peer closed the session unexpectedly.");
        var response = call.ResponseStream.Current;
        if (IsRangePruned(response)) return response;
        ThrowIfFailure(response);
        if (response.MessageCase != PeerSessionResponse.MessageOneofCase.OriginBatch)
            throw new PeerTransportException("Peer returned an unexpected session response.");
        return response;
    }

    private static bool IsRangePruned(PeerSessionResponse response) =>
        response.MessageCase == PeerSessionResponse.MessageOneofCase.Error &&
        response.Error.Code == ProtocolErrorCode.RangePruned &&
        response.Error.PrunedRange is { FirstSequence: > 0 };

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

    private void SetState(PeerTransportState value)
    {
        Volatile.Write(ref state, (int)value);
        StateChanged?.Invoke(value);
    }
}
