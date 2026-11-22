using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

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
        bool retryable) => new()
        {
            Code = code,
            SafeMessage = message,
            Retryable = retryable,
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
    PeerHostOptions options) : PeerService.PeerServiceBase
{
    public override async Task PeerSession(
        IAsyncStreamReader<PeerSessionRequest> requestStream,
        IServerStreamWriter<PeerSessionResponse> responseStream,
        ServerCallContext context)
    {
        PeerHello? remoteHello = null;
        PeerHello? localHello = null;
        var authenticated = false;
        var messages = 0;

        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            if (++messages > options.MaximumMessagesPerSession)
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
                request,
                responseStream,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAuthenticatedAsync(
        string remotePeerId,
        PeerSessionRequest request,
        IServerStreamWriter<PeerSessionResponse> responseStream,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.MessageCase)
            {
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
                exception.Retryable).ConfigureAwait(false);
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
        bool retryable) => responseStream.WriteAsync(new PeerSessionResponse
        {
            Error = PeerPairingGrpcService.Error(code, message, retryable),
        });

    private static Task WriteTimeoutAsync(IServerStreamWriter<PeerSessionResponse> responseStream) =>
        WriteErrorAsync(
            responseStream,
            ProtocolErrorCode.Internal,
            "Peer coordinator timed out.",
            retryable: true);
}
