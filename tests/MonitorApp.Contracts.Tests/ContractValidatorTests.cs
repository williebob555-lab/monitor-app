using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts.Tests;

public sealed class ContractValidatorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ExplicitZeroIsAValidMetricValue()
    {
        var result = ContractValidator.Validate(CreateMetricEnvelope(
            sequence: 1,
            quality: ObservationQuality.Valid,
            value: new MetricValue { UnsignedIntegerValue = 0 }));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidMetricWithoutValueIsRejected()
    {
        var result = ContractValidator.Validate(CreateMetricEnvelope(
            sequence: 1,
            quality: ObservationQuality.Valid,
            value: null));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("typed value", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnavailableMetricWithValueIsRejectedInsteadOfBecomingZero()
    {
        var result = ContractValidator.Validate(CreateMetricEnvelope(
            sequence: 1,
            quality: ObservationQuality.Unavailable,
            value: new MetricValue { DoubleValue = 0 }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("must not contain", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnknownMetricUnitIsRejected()
    {
        var envelope = CreateMetricEnvelope(
            sequence: 1,
            quality: ObservationQuality.Valid,
            value: new MetricValue { DoubleValue = 0.5 });
        envelope.Metric.Unit = (MetricUnit)999;

        var result = ContractValidator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unit", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BatchRequiresStrictlyContiguousSequenceNumbers()
    {
        var batch = new TelemetryBatch
        {
            Envelopes =
            {
                CreateMetricEnvelope(8, ObservationQuality.Valid, new MetricValue { DoubleValue = 0.2 }),
                CreateMetricEnvelope(10, ObservationQuality.Valid, new MetricValue { DoubleValue = 0.3 }),
            },
        };

        var result = ContractValidator.Validate(batch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("immediately follow", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnvelopeRejectsInvalidTimestampAndMissingIdentity()
    {
        var envelope = CreateMetricEnvelope(
            sequence: 1,
            quality: ObservationQuality.Valid,
            value: new MetricValue { DoubleValue = 0.5 });
        envelope.AgentId = " ";
        envelope.ObservedAt = new Timestamp { Seconds = long.MaxValue };

        var result = ContractValidator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("agent id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("timestamp is invalid", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CompleteEnrollmentRequiresSizedSecretP256KeyAndSignature()
    {
        var request = new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = "enrollment-1",
            Platform = Platform.Ubuntu,
            DisplayName = "server",
        };

        var result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            $"Enrollment secret must contain exactly {EnrollmentContractRules.EnrollmentSecretSize} bytes.",
            result.Errors);
        Assert.Contains("Public key SPKI is required.", result.Errors);
        Assert.Contains(
            $"Proof signature must contain exactly {EnrollmentContractRules.EcdsaP256SignatureSize} bytes.",
            result.Errors);
        Assert.Contains(
            "Signature algorithm must be ECDSA P-256 with SHA-256.",
            result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CreateEnrollmentResponseRequiresExactChallengeAndValidTimeWindow()
    {
        var createdAt = new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);
        var response = new CreateEnrollmentResponse
        {
            EnrollmentId = "enrollment-1",
            EnrollmentSecret = ByteString.CopyFrom(new byte[EnrollmentContractRules.EnrollmentSecretSize]),
            ProofChallenge = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.ProofChallengeSize - 1]),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(createdAt),
        };

        var result = ContractValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains(
            $"Proof challenge must contain exactly {EnrollmentContractRules.ProofChallengeSize} bytes.",
            result.Errors);
        Assert.Contains("Enrollment expiration must be later than creation.", result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CompleteEnrollmentRejectsNonP256PublicKey()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = CreateCompleteEnrollmentRequest();
        request.PublicKeySpki = ByteString.CopyFrom(p384.ExportSubjectPublicKeyInfo());

        var result = ContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(
            "Public key SPKI must contain one ECDSA P-256 public key.",
            result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DeviceChallengeRequiresExactChallengeAndExpiringWindow()
    {
        var createdAt = new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero);
        var response = new CreateDeviceChallengeResponse
        {
            AgentId = "agent-1",
            ChallengeId = "challenge-1",
            ProofChallenge = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.ProofChallengeSize]),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(createdAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(createdAt.AddMinutes(2)),
        };

        var result = ContractValidator.Validate(response);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CompleteEnrollmentResponseRequiresFingerprintNotCredential()
    {
        var response = new CompleteEnrollmentResponse
        {
            AgentId = "agent-1",
            NodeId = "node-1",
            PublicKeySha256 = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.PublicKeySha256Size - 1]),
            CompletedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero)),
        };

        var result = ContractValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Contains(
            $"Public key fingerprint must contain exactly {EnrollmentContractRules.PublicKeySha256Size} bytes.",
            result.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EnrollmentValidationErrorsNeverEchoSecretsOrSignatures()
    {
        var secret = Enumerable.Repeat((byte)0xA5, 7).ToArray();
        var signature = Enumerable.Repeat((byte)0x5A, 11).ToArray();
        var challenge = Enumerable.Repeat((byte)0x3C, 13).ToArray();
        var request = CreateCompleteEnrollmentRequest();
        request.EnrollmentSecret = ByteString.CopyFrom(secret);
        request.ProofSignature = ByteString.CopyFrom(signature);
        var response = new CreateEnrollmentResponse
        {
            EnrollmentId = "enrollment-1",
            EnrollmentSecret = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.EnrollmentSecretSize]),
            ProofChallenge = ByteString.CopyFrom(challenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
        };

        var result = ContractValidator.Validate(request);
        var responseResult = ContractValidator.Validate(response);
        var safeErrors = string.Join(
            Environment.NewLine,
            result.Errors.Concat(responseResult.Errors));

        Assert.False(result.IsValid);
        Assert.False(responseResult.IsValid);
        Assert.DoesNotContain(Convert.ToHexString(secret), safeErrors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(secret), safeErrors, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(signature), safeErrors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(signature), safeErrors, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(challenge), safeErrors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(challenge), safeErrors, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AgentHelloAcceptsAndroidAndRejectsInvalidResumeWindow()
    {
        var hello = new AgentHello
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            AgentId = "agent-1",
            BootId = "boot-1",
            AgentVersion = "0.1.0",
            Platform = Platform.Android,
            LowestPendingSequence = 12,
            NextSequence = 11,
        };

        var result = ContractValidator.Validate(hello);

        Assert.False(result.IsValid);
        Assert.DoesNotContain(result.Errors, error => error.Contains("Platform", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Lowest pending", StringComparison.Ordinal));
    }

    private static TelemetryEnvelope CreateMetricEnvelope(
        ulong sequence,
        ObservationQuality quality,
        MetricValue? value) =>
        new()
        {
            AgentId = "agent-1",
            BootId = "boot-1",
            Sequence = sequence,
            ObservedAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero)),
            CollectorId = "system.cpu",
            Metric = new MetricSample
            {
                MetricId = "cpu.utilization",
                Unit = MetricUnit.Ratio,
                Quality = quality,
                Value = value,
            },
        };

    private static CompleteEnrollmentRequest CreateCompleteEnrollmentRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = "enrollment-1",
            EnrollmentSecret = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.EnrollmentSecretSize]),
            Platform = Platform.Windows,
            DisplayName = "desktop",
            PublicKeySpki = ByteString.CopyFrom(key.ExportSubjectPublicKeyInfo()),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.EcdsaP256SignatureSize]),
        };
    }
}
