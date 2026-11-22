using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MonitorApp.Contracts;
using Microsoft.Extensions.Hosting;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Service;

public sealed class PeerPairingGrpcService(
    IPeerPairingCoordinator coordinator,
    PeerHostOptions options) : PeerPairingService.PeerPairingServiceBase
{
    public override async Task Pair(
        IAsyncStreamReader<PairingFrame> requestStream,
        IServerStreamWriter<PairingFrame> responseStream,
        ServerCallContext context)
    {
        var state = new PeerPairingSessionContext();
        var messages = 0;
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            if (++messages > options.MaximumMessagesPerSession)
            {
                await responseStream.WriteAsync(ErrorFrame(
                    ProtocolErrorCode.MessageTooLarge,
                    "Pairing session message limit exceeded.",
                    retryable: false)).ConfigureAwait(false);
                return;
            }

            var frame = requestStream.Current;
            if (frame.MessageCase == PairingFrame.MessageOneofCase.Hello &&
                IsUnsupportedPairing(frame.Hello.ProtocolVersion))
            {
                await responseStream.WriteAsync(new PairingFrame
                {
                    Incompatibility = Incompatibility(frame.Hello.ProtocolVersion),
                }).ConfigureAwait(false);
                return;
            }

            var validationError = Validate(frame);
            if (validationError is not null)
            {
                await responseStream.WriteAsync(ErrorFrame(
                    ProtocolErrorCode.InvalidMessage,
                    validationError,
                    retryable: false)).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<PairingFrame> responses;
            try
            {
                responses = await BoundedAsync(
                    token => coordinator.HandleAsync(state, frame, token),
                    options.CoordinatorTimeout,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await responseStream.WriteAsync(ErrorFrame(
                    ProtocolErrorCode.Internal,
                    "Pairing coordinator timed out.",
                    retryable: true)).ConfigureAwait(false);
                continue;
            }

            foreach (var response in responses)
            {
                await responseStream.WriteAsync(response).ConfigureAwait(false);
            }
        }
    }

    private static string? Validate(PairingFrame frame) => frame.MessageCase switch
    {
        PairingFrame.MessageOneofCase.Hello => Error(PeerContractValidator.Validate(frame.Hello)),
        PairingFrame.MessageOneofCase.Challenge => Error(PeerContractValidator.Validate(frame.Challenge)),
        PairingFrame.MessageOneofCase.Proof => Error(PeerContractValidator.Validate(frame.Proof)),
        PairingFrame.MessageOneofCase.Confirmation => ValidateConfirmation(frame.Confirmation),
        _ => "Pairing frame must contain a request message.",
    };

    private static string? Error(ContractValidationResult result) =>
        result.IsValid ? null : string.Join(" ", result.Errors);

    private static string? ValidateConfirmation(PairingConfirmation confirmation)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(confirmation.PairingId) ||
            string.IsNullOrWhiteSpace(confirmation.LocalPeerId) ||
            string.IsNullOrWhiteSpace(confirmation.RemotePeerId))
        {
            errors.Add("Pairing confirmation identities are required.");
        }

        if (confirmation.RemotePublicKeySha256.Length != PeerContractRules.Sha256Size)
        {
            errors.Add("Remote public key fingerprint must contain exactly 32 bytes.");
        }

        if (!IsTimestamp(confirmation.PairedAt))
        {
            errors.Add("paired_at timestamp is required and must be valid.");
        }

        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static PairingFrame ErrorFrame(
        ProtocolErrorCode code,
        string message,
        bool retryable) => new()
        {
            Error = Error(code, message, retryable),
        };

    internal static ProtocolError Error(
        ProtocolErrorCode code,
        string message,
        bool retryable,
        PrunedRange? prunedRange = null) => new()
        {
            Code = code,
            SafeMessage = message,
            Retryable = retryable,
            PrunedRange = prunedRange?.Clone(),
        };

    internal static CompatibilityFailure Incompatibility(ProtocolVersion received) => new()
    {
        Supported = new ProtocolCompatibility
        {
            MinimumMajor = PeerContractRules.ProtocolMajor,
            MaximumMajor = PeerContractRules.ProtocolMajor,
            MinimumMinor = PeerContractRules.BilateralSessionProofMinor,
            MaximumMinor = PeerContractRules.BilateralSessionProofMinor,
        },
        Received = received?.Clone() ?? new ProtocolVersion(),
        SafeMessage = "Peer protocol major is unsupported.",
    };

    internal static bool IsUnsupported(ProtocolVersion? version) =>
        version is not null && version.Major != 0 && version.Major != PeerContractRules.ProtocolMajor;

    private static bool IsUnsupportedPairing(ProtocolVersion? version) =>
        IsUnsupported(version) ||
        (version?.Major == PeerContractRules.ProtocolMajor &&
            version.Minor < PeerContractRules.BilateralSessionProofMinor);

    internal static bool IsTimestamp(Timestamp? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            _ = value.ToDateTimeOffset();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static async ValueTask<T> BoundedAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await action(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Peer coordinator operation timed out.");
        }
    }
}

public sealed class PeerSessionGrpcService(
    IPeerSessionCoordinator coordinator,
    PeerHostOptions options,
    IHostApplicationLifetime? lifetime = null) : PeerService.PeerServiceBase
{
    public override async Task PeerSession(
        IAsyncStreamReader<PeerSessionRequest> requestStream,
        IServerStreamWriter<PeerSessionResponse> responseStream,
        ServerCallContext context)
    {
        // Live sessions write from the request loop and from a push task, so every write goes through one gate.
        using var serialized = new SerializedResponseWriter(responseStream);
        // Host shutdown must end held-open live sessions promptly or a graceful stop would wait out its timeout.
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            lifetime?.ApplicationStopping ?? CancellationToken.None);
        var live = new LiveSessionState(sessionCancellation);
        try
        {
            await RunSessionAsync(requestStream, serialized, context, live).ConfigureAwait(false);
        }
        finally
        {
            await live.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(
        IAsyncStreamReader<PeerSessionRequest> requestStream,
        IServerStreamWriter<PeerSessionResponse> responseStream,
        ServerCallContext context,
        LiveSessionState live)
    {
        PeerHello? remoteHello = null;
        PeerHello? localHello = null;
        var authenticated = false;
        var messages = 0;

        while (true)
        {
            bool hasRequest;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(live.Token))
            {
                if (live.Active)
                {
                    idle.CancelAfter(options.LiveIdleTimeout);
                }

                try
                {
                    hasRequest = await requestStream.MoveNext(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                {
                    // Idle timeout or a failed push loop ended this live session; the client reconnects from cursors.
                    return;
                }
            }

            if (!hasRequest)
            {
                return;
            }

            if (live.Active ? !live.AllowMessage(options.MaximumMessagesPerSession) : ++messages > options.MaximumMessagesPerSession)
            {
                await WriteErrorAsync(
                    responseStream,
                    ProtocolErrorCode.MessageTooLarge,
                    "Peer session message limit exceeded.",
                    retryable: false).ConfigureAwait(false);
                return;
            }

            var request = requestStream.Current;
            if (remoteHello is null)
            {
                if (request.MessageCase != PeerSessionRequest.MessageOneofCase.Hello)
                {
                    await WriteErrorAsync(
                        responseStream,
                        ProtocolErrorCode.Unauthorized,
                        "An authenticated peer hello is required before session operations.",
                        retryable: false).ConfigureAwait(false);
                    continue;
                }

                if (IsUnsupportedSession(request.Hello.ProtocolVersion))
                {
                    await responseStream.WriteAsync(new PeerSessionResponse
                    {
                        Incompatibility = PeerPairingGrpcService.Incompatibility(
                            request.Hello.ProtocolVersion),
                    }).ConfigureAwait(false);
                    return;
                }

                var validation = PeerContractValidator.Validate(request.Hello);
                if (!validation.IsValid)
                {
                    await WriteErrorAsync(
                        responseStream,
                        ProtocolErrorCode.InvalidMessage,
                        string.Join(" ", validation.Errors),
                        retryable: false).ConfigureAwait(false);
                    continue;
                }

                PeerSessionOpenResult opened;
                try
                {
                    opened = await BoundedAsync(
                        token => coordinator.OpenAsync(request.Hello, token),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await WriteTimeoutAsync(responseStream).ConfigureAwait(false);
                    continue;
                }

                if (opened.Error is not null)
                {
                    await responseStream.WriteAsync(new PeerSessionResponse
                    {
                        Error = opened.Error,
                    }).ConfigureAwait(false);
                    continue;
                }

                if (opened.LocalHello is null || opened.Challenge is null || opened.LocalProof is null ||
                    !PeerContractValidator.Validate(opened.LocalHello).IsValid ||
                    !PeerContractValidator.Validate(opened.Challenge).IsValid ||
                    !PeerContractValidator.Validate(opened.LocalProof).IsValid ||
                    opened.LocalProof.SessionId != opened.Challenge.SessionId ||
                    opened.LocalProof.ChallengeId != opened.Challenge.ChallengeId ||
                    opened.LocalProof.SignerPeerId != opened.LocalHello.PeerId ||
                    opened.LocalProof.VerifierPeerId != request.Hello.PeerId)
                {
                    await WriteErrorAsync(
                        responseStream,
                        ProtocolErrorCode.Internal,
                        "Peer session coordinator returned an invalid handshake.",
                        retryable: true).ConfigureAwait(false);
                    continue;
                }

                remoteHello = request.Hello.Clone();
                localHello = opened.LocalHello.Clone();
                await responseStream.WriteAsync(new PeerSessionResponse
                {
                    Hello = localHello.Clone(),
                }).ConfigureAwait(false);
                await responseStream.WriteAsync(new PeerSessionResponse
                {
                    Challenge = opened.Challenge.Clone(),
                }).ConfigureAwait(false);
                await responseStream.WriteAsync(new PeerSessionResponse
                {
                    Proof = opened.LocalProof.Clone(),
                }).ConfigureAwait(false);
                continue;
            }

            if (!authenticated)
            {
                if (request.MessageCase != PeerSessionRequest.MessageOneofCase.Proof)
                {
                    await WriteErrorAsync(
                        responseStream,
                        ProtocolErrorCode.Unauthorized,
                        "Peer proof is required before capability or replication operations.",
                        retryable: false).ConfigureAwait(false);
                    continue;
                }

                var validation = PeerContractValidator.Validate(request.Proof);
                var proof = request.Proof;
                if (!validation.IsValid ||
                    proof.SessionId != remoteHello.SessionId ||
                    proof.SignerPeerId != remoteHello.PeerId ||
                    proof.VerifierPeerId != localHello!.PeerId)
                {
                    await WriteErrorAsync(
                        responseStream,
                        ProtocolErrorCode.InvalidIdentity,
                        "Peer proof does not match the active session.",
                        retryable: false).ConfigureAwait(false);
                    continue;
                }

                PeerAuthenticationResult authentication;
                try
                {
                    authentication = await BoundedAsync(
                        token => coordinator.AuthenticateAsync(remoteHello, proof, token),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await WriteTimeoutAsync(responseStream).ConfigureAwait(false);
                    continue;
                }

                if (!authentication.IsAuthenticated)
                {
                    await responseStream.WriteAsync(new PeerSessionResponse
                    {
                        Error = authentication.Error ?? PeerPairingGrpcService.Error(
                            ProtocolErrorCode.InvalidIdentity,
                            "Peer authentication failed.",
                            retryable: false),
                    }).ConfigureAwait(false);
                    continue;
                }

                authenticated = true;
                continue;
            }

            await HandleAuthenticatedAsync(
                remoteHello.PeerId,
                remoteHello.SessionId,
                request,
                responseStream,
                live,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAuthenticatedAsync(
        string remotePeerId,
        string sessionId,
        PeerSessionRequest request,
        IServerStreamWriter<PeerSessionResponse> responseStream,
        LiveSessionState live,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.MessageCase)
            {
                case PeerSessionRequest.MessageOneofCase.LiveSubscribe:
                    {
                        var session = live.Session ?? coordinator.OpenLiveSession(remotePeerId);
                        var subscribe = request.LiveSubscribe;
                        if (session is null)
                        {
                            await responseStream.WriteAsync(new PeerSessionResponse
                            {
                                LiveSubscribeAck = new LiveSubscribeAck { Accepted = false, SafeMessage = "Live sessions are not supported." },
                            }, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        live.Attach(session);
                        live.Active = true;
                        await responseStream.WriteAsync(new PeerSessionResponse
                        {
                            LiveSubscribeAck = new LiveSubscribeAck
                            {
                                Accepted = true,
                                MaximumInFlightBatches = (uint)session.MaximumInFlightBatches,
                            },
                        }, cancellationToken).ConfigureAwait(false);
                        if (subscribe.PushOrigin || subscribe.PushInterests)
                        {
                            live.StartPush(session, responseStream, subscribe.PushOrigin, subscribe.PushInterests);
                        }

                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.LiveInterest:
                    {
                        var session = live.Session ?? coordinator.OpenLiveSession(remotePeerId);
                        if (session is null)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                "Live interest is not supported.", false).ConfigureAwait(false);
                            return;
                        }

                        live.Attach(session);
                        var interestAck = await BoundedAsync(
                            token => session.AcceptRemoteInterestAsync(request.LiveInterest, token),
                            cancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(new PeerSessionResponse { LiveInterestAck = interestAck }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.LiveInterestAck:
                    break;
                case PeerSessionRequest.MessageOneofCase.CapabilityExchange:
                    {
                        var validationError = ValidateCapabilities(request.CapabilityExchange);
                        if (validationError is not null)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage, validationError, false)
                                .ConfigureAwait(false);
                            return;
                        }

                        var response = await BoundedAsync(
                            token => coordinator.ExchangeCapabilitiesAsync(
                                remotePeerId,
                                request.CapabilityExchange,
                                token),
                            cancellationToken).ConfigureAwait(false);
                        var responseError = ValidateCapabilities(response);
                        if (responseError is not null)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.Internal,
                                "Peer coordinator returned invalid capabilities.", true).ConfigureAwait(false);
                            return;
                        }

                        await responseStream.WriteAsync(new PeerSessionResponse
                        {
                            CapabilityExchange = response,
                        }, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.OriginRangeRequest:
                    {
                        var validation = PeerContractValidator.Validate(request.OriginRangeRequest);
                        if (!validation.IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                string.Join(" ", validation.Errors), false).ConfigureAwait(false);
                            return;
                        }

                        var batch = await BoundedAsync(
                            token => coordinator.ReadOriginRangeAsync(
                                remotePeerId,
                                request.OriginRangeRequest,
                                token),
                            cancellationToken).ConfigureAwait(false);
                        if (!PeerContractValidator.Validate(batch).IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.Internal,
                                "Peer coordinator returned an invalid origin batch.", true).ConfigureAwait(false);
                            return;
                        }

                        await responseStream.WriteAsync(new PeerSessionResponse { OriginBatch = batch }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.OriginBatch:
                    {
                        var validation = PeerContractValidator.Validate(request.OriginBatch);
                        if (!validation.IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                string.Join(" ", validation.Errors), false).ConfigureAwait(false);
                            return;
                        }

                        var acknowledgement = await BoundedAsync(
                            token => coordinator.AcceptOriginBatchAsync(
                                remotePeerId,
                                request.OriginBatch,
                                token),
                            cancellationToken).ConfigureAwait(false);
                        if (!PeerContractValidator.Validate(acknowledgement).IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.Internal,
                                "Peer coordinator returned an invalid acknowledgement.", true).ConfigureAwait(false);
                            return;
                        }

                        await responseStream.WriteAsync(new PeerSessionResponse { OriginAck = acknowledgement }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.OriginAck:
                    {
                        var validation = PeerContractValidator.Validate(request.OriginAck);
                        if (!validation.IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                string.Join(" ", validation.Errors), false).ConfigureAwait(false);
                            return;
                        }

                        await BoundedAsync(
                            async token =>
                            {
                                await coordinator.AcceptOriginAckAsync(
                                    remotePeerId,
                                    request.OriginAck,
                                    token).ConfigureAwait(false);
                                return true;
                            },
                            cancellationToken).ConfigureAwait(false);
                        live.Session?.OnBatchAcknowledged(request.OriginAck.Cursor);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.AlertPull:
                    {
                        var validation = PeerContractValidator.Validate(request.AlertPull);
                        if (!validation.IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                string.Join(" ", validation.Errors), false).ConfigureAwait(false);
                            return;
                        }

                        var batch = await BoundedAsync(
                            token => coordinator.PullAlertsAsync(remotePeerId, request.AlertPull, token),
                            cancellationToken).ConfigureAwait(false);
                        if (!PeerContractValidator.Validate(batch).IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.Internal,
                                "Peer coordinator returned an invalid alert batch.", true).ConfigureAwait(false);
                            return;
                        }

                        await responseStream.WriteAsync(new PeerSessionResponse { AlertBatch = batch }, cancellationToken)
                            .ConfigureAwait(false);
                        if (live.Active)
                        {
                            // A live session whose peer pulls alerts is subscribed: push new alerts to it from now on.
                            live.StartAlertPush(coordinator, remotePeerId, responseStream);
                        }

                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.AlertAck:
                    {
                        var validation = PeerContractValidator.Validate(request.AlertAck);
                        if (!validation.IsValid)
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                string.Join(" ", validation.Errors), false).ConfigureAwait(false);
                            return;
                        }

                        await BoundedAsync(
                            async token =>
                            {
                                await coordinator.AcceptAlertAckAsync(remotePeerId, request.AlertAck, token)
                                    .ConfigureAwait(false);
                                return true;
                            },
                            cancellationToken).ConfigureAwait(false);
                        live.NotifyAlertAck();
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.ControlCommand:
                    {
                        var result = await BoundedAsync(
                            token => coordinator.ExecuteControlAsync(remotePeerId, sessionId, request.ControlCommand, token),
                            cancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(new PeerSessionResponse { ControlResult = result }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.PendingPairingsRequest:
                    {
                        var response = await BoundedAsync(
                            token => coordinator.ReadPendingPairingsAsync(remotePeerId, request.PendingPairingsRequest, token),
                            cancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(new PeerSessionResponse { PendingPairingsResponse = response }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.UpdateStatusRequest:
                    {
                        var response = await BoundedAsync(
                            token => coordinator.ReadUpdateStatusAsync(remotePeerId, request.UpdateStatusRequest, token),
                            cancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(new PeerSessionResponse { UpdateStatusResponse = response }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.AlertPreferencesRequest:
                    {
                        var response = await BoundedAsync(
                            token => coordinator.ReadAlertPreferencesAsync(remotePeerId, request.AlertPreferencesRequest, token),
                            cancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(new PeerSessionResponse { AlertPreferencesResponse = response }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case PeerSessionRequest.MessageOneofCase.Heartbeat:
                    {
                        if (!PeerPairingGrpcService.IsTimestamp(request.Heartbeat.SentAt))
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                                "Heartbeat timestamp is required and must be valid.", false).ConfigureAwait(false);
                            return;
                        }

                        var heartbeat = await BoundedAsync(
                            token => coordinator.HeartbeatAsync(remotePeerId, request.Heartbeat, token),
                            cancellationToken).ConfigureAwait(false);
                        if (!PeerPairingGrpcService.IsTimestamp(heartbeat.SentAt))
                        {
                            await WriteErrorAsync(responseStream, ProtocolErrorCode.Internal,
                                "Peer coordinator returned an invalid heartbeat.", true).ConfigureAwait(false);
                            return;
                        }

                        await responseStream.WriteAsync(new PeerSessionResponse { Heartbeat = heartbeat }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                default:
                    await WriteErrorAsync(responseStream, ProtocolErrorCode.InvalidMessage,
                        "Message is not valid after authentication.", false).ConfigureAwait(false);
                    break;
            }
        }
        catch (TimeoutException)
        {
            await WriteTimeoutAsync(responseStream).ConfigureAwait(false);
        }
        catch (PeerSessionOperationException exception)
        {
            await WriteErrorAsync(
                responseStream,
                exception.Code,
                exception.SafeMessage,
                exception.Retryable,
                exception.PrunedRange).ConfigureAwait(false);
        }
    }

    private sealed class SerializedResponseWriter(IServerStreamWriter<PeerSessionResponse> inner)
        : IServerStreamWriter<PeerSessionResponse>, IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        public void Dispose() => gate.Dispose();

        public WriteOptions? WriteOptions
        {
            get => inner.WriteOptions;
            set => inner.WriteOptions = value;
        }

        public Task WriteAsync(PeerSessionResponse message) => WriteAsync(message, CancellationToken.None);

        public async Task WriteAsync(PeerSessionResponse message, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await inner.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Per-session live state: the opt-in flag, rate window, and the background push loop.</summary>
    private sealed class LiveSessionState(CancellationTokenSource cancellation)
    {
        private DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        private int windowCount;
        private Task? pushLoop;
        private Task? alertPushLoop;
        private readonly System.Threading.Channels.Channel<byte> alertAcked = System.Threading.Channels.Channel.CreateBounded<byte>(
            new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

        public CancellationToken Token => cancellation.Token;

        public bool Active { get; set; }

        public IPeerLiveSession? Session { get; private set; }

        public void Attach(IPeerLiveSession session) => Session ??= session;

        /// <summary>Held-open sessions are bounded by message rate rather than a lifetime message total.</summary>
        public bool AllowMessage(int perMinute)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - windowStart >= TimeSpan.FromMinutes(1))
            {
                windowStart = now;
                windowCount = 0;
            }

            return ++windowCount <= perMinute;
        }

        public void StartPush(
            IPeerLiveSession session,
            IServerStreamWriter<PeerSessionResponse> writer,
            bool pushOrigin,
            bool pushInterests) =>
            pushLoop ??= Task.Run(() => PushAsync(session, writer, pushOrigin, pushInterests), CancellationToken.None);

        /// <summary>
        /// Starts (once) the unsolicited alert push for this held-open session: new outbox rows reach the phone as soon as
        /// they are queued instead of at its next pull. Acknowledgements and the durable outbox are unchanged.
        /// </summary>
        public void StartAlertPush(
            IPeerSessionCoordinator coordinator,
            string remotePeerId,
            IServerStreamWriter<PeerSessionResponse> writer) =>
            alertPushLoop ??= Task.Run(() => PushAlertsAsync(coordinator, remotePeerId, writer), CancellationToken.None);

        public void NotifyAlertAck()
        {
            // A bounded channel coalesces: an acknowledgement already pending for the push loop is enough.
            alertAcked.Writer.TryWrite(0);
        }

        public async Task StopAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (pushLoop is not null)
            {
                await pushLoop.ConfigureAwait(false);
            }

            if (alertPushLoop is not null)
            {
                await alertPushLoop.ConfigureAwait(false);
            }

            Session?.Dispose();
        }

        private static readonly TimeSpan AlertPushWait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan AlertRedeliveryAfter = TimeSpan.FromSeconds(5);

        private async Task PushAlertsAsync(
            IPeerSessionCoordinator coordinator,
            string remotePeerId,
            IServerStreamWriter<PeerSessionResponse> writer)
        {
            var token = cancellation.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var batch = await coordinator.WaitForAlertPushAsync(remotePeerId, AlertPushWait, token).ConfigureAwait(false);
                    if (batch is null)
                    {
                        continue;
                    }

                    if (!PeerContractValidator.Validate(batch).IsValid)
                    {
                        throw new PeerSessionOperationException(
                            ProtocolErrorCode.Internal, "Peer coordinator returned an invalid alert batch.", true);
                    }

                    await writer.WriteAsync(new PeerSessionResponse { AlertBatch = batch }, token).ConfigureAwait(false);

                    // Unacknowledged rows stay pending, so wait for the phone's acknowledgement (sent after its durable
                    // write) rather than re-sending the same batch; redeliver if it never arrives.
                    using var redelivery = CancellationTokenSource.CreateLinkedTokenSource(token);
                    redelivery.CancelAfter(AlertRedeliveryAfter);
                    try
                    {
                        _ = await alertAcked.Reader.ReadAsync(redelivery.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (PeerSessionOperationException exception)
            {
                await TryWriteErrorAsync(writer, exception).ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        private async Task PushAsync(
            IPeerLiveSession session,
            IServerStreamWriter<PeerSessionResponse> writer,
            bool pushOrigin,
            bool pushInterests)
        {
            var token = cancellation.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var progressed = false;
                    while (pushOrigin && await session.TryNextBatchAsync(token).ConfigureAwait(false) is { } batch)
                    {
                        if (!PeerContractValidator.Validate(batch).IsValid)
                        {
                            throw new PeerSessionOperationException(
                                ProtocolErrorCode.Internal, "Peer coordinator returned an invalid origin batch.", true);
                        }

                        await writer.WriteAsync(new PeerSessionResponse { OriginBatch = batch }, token).ConfigureAwait(false);
                        progressed = true;
                    }

                    if (pushInterests)
                    {
                        foreach (var interest in session.DrainOutgoingInterests())
                        {
                            await writer.WriteAsync(new PeerSessionResponse { LiveInterest = interest }, token).ConfigureAwait(false);
                            progressed = true;
                        }
                    }

                    if (!progressed)
                    {
                        await session.WaitForActivityAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (PeerSessionOperationException exception)
            {
                // Trust withdrawn or the range became unreadable mid-session: tell the peer, then end the session.
                await TryWriteErrorAsync(writer, exception).ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        private static async Task TryWriteErrorAsync(
            IServerStreamWriter<PeerSessionResponse> writer,
            PeerSessionOperationException exception)
        {
            try
            {
                await writer.WriteAsync(new PeerSessionResponse
                {
                    Error = PeerPairingGrpcService.Error(exception.Code, exception.SafeMessage, exception.Retryable, exception.PrunedRange),
                }).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or OperationCanceledException)
            {
                // The stream already ended; there is nobody left to tell.
            }
        }
    }

    private ValueTask<T> BoundedAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken) =>
        PeerPairingGrpcService.BoundedAsync(action, options.CoordinatorTimeout, cancellationToken);

    private static bool IsUnsupportedSession(ProtocolVersion? version) =>
        PeerPairingGrpcService.IsUnsupported(version) ||
        (version?.Major == PeerContractRules.ProtocolMajor &&
            version.Minor < PeerContractRules.BilateralSessionProofMinor);

    private static string? ValidateCapabilities(CapabilityExchange exchange)
    {
        foreach (var capability in exchange.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.ModuleId) ||
                string.IsNullOrWhiteSpace(capability.ModuleVersion) ||
                capability.DeclarationSha256.Length != PeerContractRules.Sha256Size)
            {
                return "Capability descriptors require module id, version, and a 32-byte declaration hash.";
            }
        }

        return null;
    }

    private static Task WriteErrorAsync(
        IServerStreamWriter<PeerSessionResponse> responseStream,
        ProtocolErrorCode code,
        string message,
        bool retryable,
        PrunedRange? prunedRange = null) => responseStream.WriteAsync(new PeerSessionResponse
        {
            Error = PeerPairingGrpcService.Error(code, message, retryable, prunedRange),
        });

    private static Task WriteTimeoutAsync(IServerStreamWriter<PeerSessionResponse> responseStream) =>
        WriteErrorAsync(
            responseStream,
            ProtocolErrorCode.Internal,
            "Peer coordinator timed out.",
            retryable: true);
}

/// <summary>
/// Narrow, unauthenticated-by-design lookup that turns a one-time typed pairing code into the desktop's public
/// identity plus a code-keyed proof. Every failure (unknown, expired, used, malformed, rate limited) yields the
/// same status so it is not a guessing oracle; trust is still only granted by the bilateral pairing that follows.
/// </summary>
public sealed class PeerPairingCodeGrpcService(PairingShortCodeRegistry registry)
    : PeerPairingCodeService.PeerPairingCodeServiceBase
{
    public override Task<RedeemPairingCodeResponse> Redeem(RedeemPairingCodeRequest request, ServerCallContext context)
    {
        var redemption = PeerPairingGrpcService.IsUnsupported(request.ProtocolVersion)
            ? null
            : registry.Redeem(request.LookupId.ToByteArray());
        if (redemption is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Pairing code was not accepted."));
        }

        return Task.FromResult(new RedeemPairingCodeResponse
        {
            PeerId = redemption.PeerId,
            PublicKeySha256 = Google.Protobuf.ByteString.CopyFrom(redemption.FingerprintSha256),
            Proof = Google.Protobuf.ByteString.CopyFrom(redemption.Proof),
        });
    }
}
