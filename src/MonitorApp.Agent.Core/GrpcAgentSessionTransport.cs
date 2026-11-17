using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public sealed class GrpcAgentSessionTransport : IAgentSessionTransport, IAsyncDisposable
{
    private readonly GrpcChannel channel;
    private readonly ECDsa deviceKey;
    private AsyncDuplexStreamingCall<AgentSessionRequest, AgentSessionResponse>? call;
    private string? authenticatedAgentId;

    public GrpcAgentSessionTransport(Uri controllerAddress, ECDsa deviceKey)
    {
        ArgumentNullException.ThrowIfNull(controllerAddress);
        this.deviceKey = deviceKey ?? throw new ArgumentNullException(nameof(deviceKey));
        channel = GrpcChannel.ForAddress(controllerAddress);
    }

    public async Task<ControllerHello> OpenAsync(
        AgentHello hello,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        await CloseCallAsync().ConfigureAwait(false);
        call = new AgentService.AgentServiceClient(channel).AgentSession(
            cancellationToken: cancellationToken);

        await call.RequestStream.WriteAsync(
            new AgentSessionRequest { Hello = hello },
            cancellationToken).ConfigureAwait(false);
        var challengeResponse = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (challengeResponse.MessageCase != AgentSessionResponse.MessageOneofCase.Challenge)
        {
            throw new InvalidOperationException("Controller did not return an authentication challenge.");
        }

        var challenge = challengeResponse.Challenge;
        var validation = ContractValidator.Validate(challenge);
        if (!validation.IsValid || challenge.AgentId != hello.AgentId)
        {
            throw new InvalidOperationException("Controller authentication challenge is invalid.");
        }

        var payload = ProofPayload.CreateAgentSession(
            hello.AgentId,
            challenge.SessionId,
            challenge.ChallengeId,
            challenge.ProofChallenge.Span);
        var signature = deviceKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await call.RequestStream.WriteAsync(
            new AgentSessionRequest
            {
                Proof = new AgentSessionProof
                {
                    AgentId = hello.AgentId,
                    SessionId = challenge.SessionId,
                    ChallengeId = challenge.ChallengeId,
                    SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                    ProofSignature = ByteString.CopyFrom(signature),
                },
            },
            cancellationToken).ConfigureAwait(false);

        var helloResponse = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (helloResponse.MessageCase != AgentSessionResponse.MessageOneofCase.Hello)
        {
            throw new InvalidOperationException("Controller did not authenticate the agent stream.");
        }

        authenticatedAgentId = hello.AgentId;
        return helloResponse.Hello;
    }

    public async Task<TelemetryAck> SendAsync(
        TelemetryBatch batch,
        CancellationToken cancellationToken)
    {
        if (call is null || authenticatedAgentId is null)
        {
            throw new InvalidOperationException("The agent stream is not authenticated.");
        }

        if (batch.Envelopes.Any(envelope => envelope.AgentId != authenticatedAgentId))
        {
            throw new InvalidOperationException("Telemetry does not belong to the authenticated agent.");
        }

        await call.RequestStream.WriteAsync(
            new AgentSessionRequest { Telemetry = batch },
            cancellationToken).ConfigureAwait(false);
        var response = await ReadRequiredAsync(cancellationToken).ConfigureAwait(false);
        if (response.MessageCase != AgentSessionResponse.MessageOneofCase.TelemetryAck)
        {
            throw new InvalidOperationException("Controller did not acknowledge telemetry.");
        }

        return response.TelemetryAck;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseCallAsync().ConfigureAwait(false);
        channel.Dispose();
    }

    private async Task<AgentSessionResponse> ReadRequiredAsync(
        CancellationToken cancellationToken)
    {
        if (call is null ||
            !await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("Controller closed the agent stream unexpectedly.");
        }

        return call.ResponseStream.Current;
    }

    private async Task CloseCallAsync()
    {
        if (call is null)
        {
            return;
        }

        try
        {
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException)
        {
        }
        finally
        {
            call.Dispose();
            call = null;
            authenticatedAgentId = null;
        }
    }
}
