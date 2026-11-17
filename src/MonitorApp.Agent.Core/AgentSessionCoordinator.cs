using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public interface IAgentSessionTransport
{
    Task<ControllerHello> OpenAsync(AgentHello hello, CancellationToken cancellationToken);

    Task<TelemetryAck> SendAsync(TelemetryBatch batch, CancellationToken cancellationToken);
}

public sealed class AgentSessionCoordinator
{
    private readonly SqliteTelemetrySpool spool;
    private readonly IAgentSessionTransport transport;

    public AgentSessionCoordinator(SqliteTelemetrySpool spool, IAgentSessionTransport transport)
    {
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<int> FlushOnceAsync(
        string agentId,
        string bootId,
        string agentVersion,
        Platform platform,
        IEnumerable<Capability> capabilities,
        CancellationToken cancellationToken = default)
    {
        var state = await spool.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var hello = new AgentHello
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
            AgentId = agentId,
            BootId = bootId,
            AgentVersion = agentVersion,
            Platform = platform,
            LowestPendingSequence = state.LowestPendingSequence,
            NextSequence = state.NextSequence,
        };
        hello.Capabilities.AddRange(capabilities);

        var validation = ContractValidator.Validate(hello);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Errors));
        }

        var controllerHello = await transport.OpenAsync(hello, cancellationToken).ConfigureAwait(false);
        ValidateControllerHello(controllerHello);
        if (controllerHello.AcceptedThroughSequence >= state.NextSequence)
        {
            throw new InvalidOperationException("Controller acknowledgement includes unsent telemetry.");
        }

        if (controllerHello.AcceptedThroughSequence > state.AcceptedThroughSequence)
        {
            await spool.AcknowledgeAsync(
                controllerHello.AcceptedThroughSequence,
                cancellationToken).ConfigureAwait(false);
        }

        var maximumCount = checked((int)Math.Min(controllerHello.MaximumBatchCount, int.MaxValue));
        var maximumBytes = checked((int)Math.Min(controllerHello.MaximumBatchBytes, int.MaxValue));
        var pending = await spool.ReadBatchAsync(
            maximumCount,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        var batch = new TelemetryBatch();
        batch.Envelopes.AddRange(pending);
        var batchValidation = ContractValidator.Validate(batch);
        if (!batchValidation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", batchValidation.Errors));
        }

        var acknowledgement = await transport.SendAsync(batch, cancellationToken).ConfigureAwait(false);
        if (acknowledgement.AcceptedThroughSequence < pending[0].Sequence ||
            acknowledgement.AcceptedThroughSequence > pending[^1].Sequence)
        {
            throw new InvalidOperationException("Controller returned an acknowledgement outside the submitted batch.");
        }

        await spool.AcknowledgeAsync(
            acknowledgement.AcceptedThroughSequence,
            cancellationToken).ConfigureAwait(false);
        return pending.Count;
    }

    private static void ValidateControllerHello(ControllerHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.ProtocolVersion is null || hello.ProtocolVersion.Major != 1)
        {
            throw new InvalidOperationException("Controller protocol version is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(hello.SessionId))
        {
            throw new InvalidOperationException("Controller session identity is missing.");
        }

        if (hello.MaximumBatchCount == 0 || hello.MaximumBatchBytes == 0)
        {
            throw new InvalidOperationException("Controller batch limits must be greater than zero.");
        }

        if (hello.ServerTime is null)
        {
            throw new InvalidOperationException("Controller time is missing.");
        }

        _ = hello.ServerTime.ToDateTimeOffset();
    }
}
