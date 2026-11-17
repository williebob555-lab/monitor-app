using Grpc.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Persistence;

namespace MonitorApp.Controller;

public sealed class AgentGrpcService(TelemetryStore store) : AgentService.AgentServiceBase
{
    private const uint MaximumBatchCount = 256;
    private const uint MaximumBatchBytes = 512 * 1024;

    public override async Task AgentSession(
        IAsyncStreamReader<AgentSessionRequest> requestStream,
        IServerStreamWriter<AgentSessionResponse> responseStream,
        ServerCallContext context)
    {
        try
        {
            var first = await ReadRequiredAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
            if (first.MessageCase != AgentSessionRequest.MessageOneofCase.Hello)
            {
                throw Invalid("Agent hello must be the first stream message.");
            }

            var hello = first.Hello;
            var helloValidation = ContractValidator.Validate(hello);
            if (!helloValidation.IsValid)
            {
                throw Invalid(string.Join(" ", helloValidation.Errors));
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var challenge = await store.CreateSessionChallengeAsync(
                hello.AgentId,
                sessionId,
                context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(
                new AgentSessionResponse { Challenge = challenge },
                context.CancellationToken).ConfigureAwait(false);

            var second = await ReadRequiredAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
            if (second.MessageCase != AgentSessionRequest.MessageOneofCase.Proof)
            {
                throw Unauthenticated();
            }

            await store.VerifySessionProofAsync(
                challenge,
                second.Proof,
                context.CancellationToken).ConfigureAwait(false);

            var accepted = await store.GetAcceptedThroughAsync(
                hello.AgentId,
                context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(
                new AgentSessionResponse
                {
                    Hello = new ControllerHello
                    {
                        ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                        SessionId = sessionId,
                        AcceptedThroughSequence = accepted,
                        MaximumBatchCount = MaximumBatchCount,
                        MaximumBatchBytes = MaximumBatchBytes,
                        ServerTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                            DateTimeOffset.UtcNow),
                    },
                },
                context.CancellationToken).ConfigureAwait(false);

            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var request = requestStream.Current;
                switch (request.MessageCase)
                {
                    case AgentSessionRequest.MessageOneofCase.Telemetry:
                        accepted = await store.IngestAsync(
                            hello,
                            request.Telemetry,
                            context.CancellationToken).ConfigureAwait(false);
                        await responseStream.WriteAsync(
                            new AgentSessionResponse
                            {
                                TelemetryAck = new TelemetryAck
                                {
                                    AcceptedThroughSequence = accepted,
                                },
                            },
                            context.CancellationToken).ConfigureAwait(false);
                        break;
                    case AgentSessionRequest.MessageOneofCase.Heartbeat:
                        ValidateHeartbeat(request.Heartbeat);
                        break;
                    default:
                        throw Invalid("The authenticated stream message is not supported.");
                }
            }
        }
        catch (TelemetryFailureException exception)
        {
            throw Map(exception);
        }
    }

    private static async Task<AgentSessionRequest> ReadRequiredAsync(
        IAsyncStreamReader<AgentSessionRequest> requestStream,
        CancellationToken cancellationToken)
    {
        if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            throw Invalid("The agent stream ended before authentication completed.");
        }

        return requestStream.Current;
    }

    private static void ValidateHeartbeat(AgentHeartbeat heartbeat)
    {
        if (heartbeat.SentAt is null)
        {
            throw Invalid("Heartbeat sent_at is required.");
        }

        try
        {
            _ = heartbeat.SentAt.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            throw Invalid("Heartbeat sent_at is invalid.");
        }
    }

    private static RpcException Map(TelemetryFailureException exception) => exception.Reason switch
    {
        TelemetryFailureReason.InvalidMessage => Invalid(exception.Message),
        TelemetryFailureReason.SequenceConflict =>
            new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message)),
        TelemetryFailureReason.Replay or TelemetryFailureReason.Unauthorized => Unauthenticated(),
        _ => new RpcException(new Status(StatusCode.Internal, "Telemetry processing failed.")),
    };

    private static RpcException Invalid(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException Unauthenticated() =>
        new(new Status(StatusCode.Unauthenticated, "Agent authentication failed."));
}
