using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public static partial class ContractValidator
{
    public static ContractValidationResult Validate(TelemetryEnvelope? envelope)
    {
        var errors = new List<string>();
        ValidateEnvelope(envelope, errors, null);
        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(TelemetryBatch? batch)
    {
        var errors = new List<string>();
        if (batch is null)
        {
            errors.Add("Telemetry batch is required.");
            return CreateResult(errors);
        }

        if (batch.Envelopes.Count == 0)
        {
            errors.Add("Telemetry batch must contain at least one envelope.");
            return CreateResult(errors);
        }

        ulong? previousSequence = null;
        for (var index = 0; index < batch.Envelopes.Count; index++)
        {
            var envelope = batch.Envelopes[index];
            ValidateEnvelope(envelope, errors, index);

            if (previousSequence.HasValue && envelope.Sequence != previousSequence.Value + 1)
            {
                errors.Add($"Telemetry batch sequence at index {index} must immediately follow the previous sequence.");
            }

            previousSequence = envelope.Sequence;
        }

        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(AgentHello? hello)
    {
        var errors = new List<string>();
        if (hello is null)
        {
            errors.Add("Agent hello is required.");
            return CreateResult(errors);
        }

        ValidateProtocolVersion(hello.ProtocolVersion, errors);
        RequireText(hello.AgentId, "Agent id", errors);
        RequireText(hello.BootId, "Boot id", errors);
        RequireText(hello.AgentVersion, "Agent version", errors);
        ValidatePlatform(hello.Platform, errors);

        if (hello.NextSequence == 0)
        {
            errors.Add("Next sequence must be greater than zero.");
        }

        if (hello.LowestPendingSequence > hello.NextSequence)
        {
            errors.Add("Lowest pending sequence cannot exceed next sequence.");
        }

        foreach (var capability in hello.Capabilities)
        {
            RequireText(capability.Id, "Capability id", errors);
            if (capability.State == CapabilityState.Unspecified ||
                !System.Enum.IsDefined(capability.State))
            {
                errors.Add("Capability state must be specified.");
            }
        }

        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(AgentSessionProof? proof)
    {
        var errors = new List<string>();
        if (proof is null)
        {
            errors.Add("Agent session proof is required.");
            return CreateResult(errors);
        }

        RequireText(proof.AgentId, "Agent id", errors);
        RequireText(proof.SessionId, "Session id", errors);
        RequireText(proof.ChallengeId, "Challenge id", errors);
        if (proof.SignatureAlgorithm != SignatureAlgorithm.EcdsaP256Sha256)
        {
            errors.Add("Signature algorithm must be ECDSA P-256 with SHA-256.");
        }

        if (proof.ProofSignature.Length != EnrollmentContractRules.EcdsaP256SignatureSize)
        {
            errors.Add($"Proof signature must contain exactly {EnrollmentContractRules.EcdsaP256SignatureSize} bytes.");
        }

        return CreateResult(errors);
    }

    public static ContractValidationResult Validate(AgentSessionChallenge? challenge)
    {
        var errors = new List<string>();
        if (challenge is null)
        {
            errors.Add("Agent session challenge is required.");
            return CreateResult(errors);
        }

        RequireText(challenge.AgentId, "Agent id", errors);
        RequireText(challenge.SessionId, "Session id", errors);
        RequireText(challenge.ChallengeId, "Challenge id", errors);
        if (challenge.ProofChallenge.Length != EnrollmentContractRules.ProofChallengeSize)
        {
            errors.Add($"Proof challenge must contain exactly {EnrollmentContractRules.ProofChallengeSize} bytes.");
        }

        if (challenge.SignatureAlgorithm != SignatureAlgorithm.EcdsaP256Sha256)
        {
            errors.Add("Signature algorithm must be ECDSA P-256 with SHA-256.");
        }

        ValidateTimestamp(challenge.CreatedAt, "created_at", errors, required: true);
        ValidateTimestamp(challenge.ExpiresAt, "expires_at", errors, required: true);
        if (challenge.CreatedAt is not null && challenge.ExpiresAt is not null)
        {
            try
            {
                if (challenge.ExpiresAt.ToDateTimeOffset() <= challenge.CreatedAt.ToDateTimeOffset())
                {
                    errors.Add("Agent session challenge expiration must be later than creation.");
                }
            }
            catch (InvalidOperationException)
            {
                // Timestamp validation above supplies the safe field error.
            }
        }

        return CreateResult(errors);
    }

    private static void ValidateEnvelope(
        TelemetryEnvelope? envelope,
        List<string> errors,
        int? index)
    {
        var prefix = index.HasValue ? $"Envelope at index {index.Value}: " : string.Empty;
        if (envelope is null)
        {
            errors.Add($"{prefix}telemetry envelope is required.");
            return;
        }

        RequireText(envelope.AgentId, $"{prefix}agent id", errors);
        RequireText(envelope.BootId, $"{prefix}boot id", errors);
        RequireText(envelope.CollectorId, $"{prefix}collector id", errors);

        if (envelope.Sequence == 0)
        {
            errors.Add($"{prefix}sequence must be greater than zero.");
        }

        ValidateTimestamp(envelope.ObservedAt, $"{prefix}observed_at", errors, required: true);
        ValidateTimestamp(envelope.IngestedAt, $"{prefix}ingested_at", errors, required: false);

        switch (envelope.PayloadCase)
        {
            case TelemetryEnvelope.PayloadOneofCase.Metric:
                ValidateMetric(envelope.Metric, errors, prefix);
                break;
            case TelemetryEnvelope.PayloadOneofCase.Event:
                RequireText(envelope.Event.EventType, $"{prefix}event type", errors);
                if (envelope.Event.Severity == EventSeverity.Unspecified ||
                    !System.Enum.IsDefined(envelope.Event.Severity))
                {
                    errors.Add($"{prefix}event severity must be specified.");
                }

                break;
            case TelemetryEnvelope.PayloadOneofCase.CollectorStatus:
                RequireText(envelope.CollectorStatus.CapabilityId, $"{prefix}capability id", errors);
                if (envelope.CollectorStatus.State == CapabilityState.Unspecified ||
                    !System.Enum.IsDefined(envelope.CollectorStatus.State))
                {
                    errors.Add($"{prefix}collector state must be specified.");
                }

                break;
            case TelemetryEnvelope.PayloadOneofCase.None:
            default:
                errors.Add($"{prefix}payload is required.");
                break;
        }
    }

    private static void ValidateMetric(
        MetricSample metric,
        List<string> errors,
        string prefix)
    {
        RequireText(metric.MetricId, $"{prefix}metric id", errors);
        if (metric.Unit == MetricUnit.Unspecified || !System.Enum.IsDefined(metric.Unit))
        {
            errors.Add($"{prefix}metric unit must be specified.");
        }

        if (metric.Quality == ObservationQuality.Unspecified ||
            !System.Enum.IsDefined(metric.Quality))
        {
            errors.Add($"{prefix}metric quality must be specified.");
            return;
        }

        var hasValue = metric.Value?.ValueCase is not null and not MetricValue.ValueOneofCase.None;
        var requiresValue = metric.Quality is ObservationQuality.Valid or ObservationQuality.Estimated;
        if (requiresValue && !hasValue)
        {
            errors.Add($"{prefix}valid or estimated metric must contain a typed value.");
        }

        if (!requiresValue && hasValue)
        {
            errors.Add($"{prefix}unavailable or failed metric must not contain a value.");
        }
    }

    private static void ValidateProtocolVersion(
        ProtocolVersion? version,
        List<string> errors)
    {
        if (version is null || version.Major == 0)
        {
            errors.Add("Protocol version major must be greater than zero.");
        }
    }

    private static void ValidatePlatform(Platform platform, List<string> errors)
    {
        if (platform == Platform.Unspecified || !System.Enum.IsDefined(platform))
        {
            errors.Add("Platform must be a recognized, specified value.");
        }
    }

    private static void ValidateTimestamp(
        Timestamp? timestamp,
        string fieldName,
        List<string> errors,
        bool required)
    {
        if (timestamp is null)
        {
            if (required)
            {
                errors.Add($"{fieldName} timestamp is required.");
            }

            return;
        }

        try
        {
            _ = timestamp.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            errors.Add($"{fieldName} timestamp is invalid.");
        }
    }

    private static void RequireText(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required.");
        }
    }

    private static ContractValidationResult CreateResult(List<string> errors) =>
        errors.Count == 0
            ? ContractValidationResult.Success()
            : ContractValidationResult.Failure(errors);
}
