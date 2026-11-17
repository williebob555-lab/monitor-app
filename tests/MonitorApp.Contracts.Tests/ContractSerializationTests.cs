using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using System.Security.Cryptography;

namespace MonitorApp.Contracts.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void ValidZeroMetricRoundTripPreservesTypedValuePresence()
    {
        var original = CreateMetricEnvelope(17, 0);

        var parsed = TelemetryEnvelope.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(TelemetryEnvelope.PayloadOneofCase.Metric, parsed.PayloadCase);
        Assert.Equal(ObservationQuality.Valid, parsed.Metric.Quality);
        Assert.Equal(MetricValue.ValueOneofCase.DoubleValue, parsed.Metric.Value.ValueCase);
        Assert.Equal(0, parsed.Metric.Value.DoubleValue);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void AgentHelloRoundTripPreservesAndroidAndOneofSelection()
    {
        var original = new AgentSessionRequest
        {
            Hello = new AgentHello
            {
                ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                AgentId = "agent-android-1",
                BootId = "boot-7",
                AgentVersion = "0.1.0",
                Platform = Platform.Android,
                LowestPendingSequence = 18,
                NextSequence = 24,
                Capabilities =
                {
                    new Capability
                    {
                        Id = "device.battery",
                        State = CapabilityState.Available,
                    },
                },
            },
        };

        var parsed = AgentSessionRequest.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(AgentSessionRequest.MessageOneofCase.Hello, parsed.MessageCase);
        Assert.Equal(Platform.Android, parsed.Hello.Platform);
        Assert.Equal("device.battery", Assert.Single(parsed.Hello.Capabilities).Id);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void EnrollmentProofRoundTripVerifiesWithSerializedP256PublicKey()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeySpki = signer.ExportSubjectPublicKeyInfo();
        var challenge = CreateBytes(EnrollmentContractRules.ProofChallengeSize, start: 10);
        var proofPayload = ProofPayload.CreateEnrollment(
            "enrollment-1",
            challenge,
            publicKeySpki);
        var signature = signer.SignData(
            proofPayload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var original = new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = "enrollment-1",
            EnrollmentSecret = ByteString.CopyFrom(
                CreateBytes(EnrollmentContractRules.EnrollmentSecretSize, start: 70)),
            Platform = Platform.Fedora,
            DisplayName = "workstation",
            PublicKeySpki = ByteString.CopyFrom(publicKeySpki),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(signature),
        };

        var parsed = CompleteEnrollmentRequest.Parser.ParseFrom(original.ToByteArray());
        var parsedPayload = ProofPayload.CreateEnrollment(
            parsed.EnrollmentId,
            challenge,
            parsed.PublicKeySpki.Span);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(parsed.PublicKeySpki.Span, out var bytesRead);

        Assert.Equal(parsed.PublicKeySpki.Length, bytesRead);
        Assert.Equal(Platform.Fedora, parsed.Platform);
        Assert.True(ContractValidator.Validate(parsed).IsValid);
        Assert.True(verifier.VerifyData(
            parsedPayload,
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(verifier.VerifyData(
            ProofPayload.CreateEnrollment("enrollment-2", challenge, parsed.PublicKeySpki.Span),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void DeviceProofRoundTripVerifiesAgainstPreviouslyEnrolledKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challenge = CreateBytes(EnrollmentContractRules.ProofChallengeSize, start: 120);
        var payload = ProofPayload.CreateDevice("agent-1", "challenge-8", challenge);
        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var original = new VerifyDeviceProofRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            AgentId = "agent-1",
            ChallengeId = "challenge-8",
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(signature),
        };

        var parsed = VerifyDeviceProofRequest.Parser.ParseFrom(original.ToByteArray());
        var parsedPayload = ProofPayload.CreateDevice(
            parsed.AgentId,
            parsed.ChallengeId,
            challenge);

        Assert.True(ContractValidator.Validate(parsed).IsValid);
        Assert.True(key.VerifyData(
            parsedPayload,
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(key.VerifyData(
            ProofPayload.CreateDevice("agent-2", parsed.ChallengeId, challenge),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var substitutedChallenge = challenge.ToArray();
        substitutedChallenge[0] ^= 0xFF;
        Assert.False(key.VerifyData(
            ProofPayload.CreateDevice(parsed.AgentId, parsed.ChallengeId, substitutedChallenge),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ParserPreservesUnknownFieldsForForwardCompatibleRelay()
    {
        var version = new ProtocolVersion { Major = 1, Minor = 2 };
        using var stream = new MemoryStream();
        version.WriteTo(stream);
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(99, WireFormat.WireType.Varint);
            output.WriteUInt32(42);
        }

        var parsed = ProtocolVersion.Parser.ParseFrom(stream.ToArray());
        var relayed = parsed.ToByteArray();

        Assert.True(ContainsVarintField(relayed, 99, 42));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void AgentSessionServiceIsBidirectionalStreaming()
    {
        var method = Assert.Single(AgentService.Descriptor.Methods);

        Assert.True(method.IsClientStreaming);
        Assert.True(method.IsServerStreaming);
        Assert.Equal("AgentSession", method.Name);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void EnrollmentServiceExposesOnlyTheSixUnaryLifecycleMethods()
    {
        var methods = EnrollmentService.Descriptor.Methods;

        Assert.Equal(
            [
                "Create",
                "GetStatus",
                "Cancel",
                "Complete",
                "CreateDeviceChallenge",
                "VerifyDeviceProof",
            ],
            methods.Select(method => method.Name));
        Assert.All(methods, method =>
        {
            Assert.False(method.IsClientStreaming);
            Assert.False(method.IsServerStreaming);
        });
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CompleteEnrollmentResponseCannotCarryReusableRawCredential()
    {
        var fieldNames = CompleteEnrollmentResponse.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(field => field.Name);

        Assert.DoesNotContain("agent_credential", fieldNames);
        Assert.Contains("public_key_sha256", fieldNames);
    }

    private static TelemetryEnvelope CreateMetricEnvelope(ulong sequence, double value) =>
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
                Quality = ObservationQuality.Valid,
                Value = new MetricValue { DoubleValue = value },
            },
        };

    private static bool ContainsVarintField(byte[] message, int fieldNumber, ulong expectedValue)
    {
        var input = new CodedInputStream(message);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == fieldNumber)
            {
                return input.ReadUInt64() == expectedValue;
            }

            input.SkipLastField();
        }

        return false;
    }

    private static byte[] CreateBytes(int length, byte start) =>
        Enumerable.Range(start, length).Select(value => checked((byte)value)).ToArray();
}
