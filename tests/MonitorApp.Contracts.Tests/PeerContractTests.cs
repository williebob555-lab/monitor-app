using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts.Tests;

public sealed class PeerContractTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Contract")]
    public void PeerServicesExposeOnlyBidirectionalPairingAndSessionStreams()
    {
        var pairing = Assert.Single(PeerPairingService.Descriptor.Methods);
        var session = Assert.Single(PeerService.Descriptor.Methods);

        Assert.Equal("Pair", pairing.Name);
        Assert.True(pairing.IsClientStreaming);
        Assert.True(pairing.IsServerStreaming);
        Assert.Equal("PeerSession", session.Name);
        Assert.True(session.IsClientStreaming);
        Assert.True(session.IsServerStreaming);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PairingHelloRoundTripCarriesP256IdentityAndPreservesUnknownFields()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var original = new PairingHello
        {
            ProtocolVersion = CurrentVersion(),
            PairingId = "pairing-1",
            PeerId = "peer-a",
            DisplayName = "workstation",
            PublicKeySpki = ByteString.CopyFrom(spki),
            PublicKeySha256 = ByteString.CopyFrom(SHA256.HashData(spki)),
        };
        using var stream = new MemoryStream();
        original.WriteTo(stream);
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(99, WireFormat.WireType.Varint);
            output.WriteUInt32(42);
        }

        var parsed = PairingHello.Parser.ParseFrom(stream.ToArray());

        Assert.True(PeerContractValidator.Validate(parsed).IsValid);
        Assert.True(ContainsVarintField(parsed.ToByteArray(), 99, 42));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PairingHelloRejectsFingerprintThatDoesNotMatchPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hello = new PairingHello
        {
            ProtocolVersion = CurrentVersion(),
            PairingId = "pairing-1",
            PeerId = "peer-a",
            PublicKeySpki = ByteString.CopyFrom(key.ExportSubjectPublicKeyInfo()),
            PublicKeySha256 = ByteString.CopyFrom(new byte[PeerContractRules.Sha256Size]),
        };

        var result = PeerContractValidator.Validate(hello);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PeerProofRoundTripBindsBothPeersSessionChallengeAndFreshBytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var challengeBytes = Enumerable.Range(1, EnrollmentContractRules.ProofChallengeSize)
            .Select(value => checked((byte)value))
            .ToArray();
        var payload = ProofPayload.CreatePeerSession(
            "session-1",
            "challenge-1",
            "peer-a",
            "peer-b",
            challengeBytes);
        var proof = new PeerProof
        {
            SessionId = "session-1",
            ChallengeId = "challenge-1",
            SignerPeerId = "peer-a",
            VerifierPeerId = "peer-b",
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        };

        var parsed = PeerProof.Parser.ParseFrom(proof.ToByteArray());

        Assert.True(PeerContractValidator.Validate(parsed).IsValid);
        Assert.True(key.VerifyData(
            ProofPayload.CreatePeerSession(
                parsed.SessionId,
                parsed.ChallengeId,
                parsed.SignerPeerId,
                parsed.VerifierPeerId,
                challengeBytes),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(key.VerifyData(
            ProofPayload.CreatePeerSession(
                parsed.SessionId,
                parsed.ChallengeId,
                parsed.SignerPeerId,
                "peer-c",
                challengeBytes),
            parsed.ProofSignature.Span,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChallengeRequiresDifferentPeersExactNonceAndExpiringWindow()
    {
        var challenge = new PeerChallenge
        {
            SessionId = "session-1",
            ChallengeId = "challenge-1",
            ChallengerPeerId = "peer-a",
            ChallengedPeerId = "peer-a",
            ProofChallenge = ByteString.CopyFrom(
                new byte[EnrollmentContractRules.ProofChallengeSize - 1]),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(Now),
            ExpiresAt = Timestamp.FromDateTimeOffset(Now),
        };

        var result = PeerContractValidator.Validate(challenge);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("different", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("exactly 32", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("later than creation", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RangeRequestRequiresIdentityOrderedBoundsAndReceiverLimits()
    {
        var request = new OriginRangeRequest
        {
            RequestId = "request-1",
            OriginPeerId = "peer-a",
            OriginStreamId = "stream-a",
            FirstSequence = 8,
            LastSequence = 7,
        };

        var result = PeerContractValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Last sequence", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("nonzero", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void OriginBatchRoundTripPreservesContiguousOriginIdentityAndContentHash()
    {
        var batch = new OriginRecordBatch
        {
            RequestId = "request-1",
            HasMore = true,
            Records =
            {
                CreateRecord(8, "first"u8.ToArray()),
                CreateRecord(9, "second"u8.ToArray()),
            },
        };

        var parsed = OriginRecordBatch.Parser.ParseFrom(batch.ToByteArray());

        Assert.True(PeerContractValidator.Validate(parsed).IsValid);
        Assert.Equal((ulong)8, parsed.Records[0].Sequence);
        Assert.Equal((ulong)9, parsed.Records[1].Sequence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OriginBatchRejectsGapMixedStreamAndChangedContent()
    {
        var first = CreateRecord(8, "first"u8.ToArray());
        var second = CreateRecord(10, "second"u8.ToArray());
        second.OriginStreamId = "stream-b";
        second.RecordBytes = ByteString.CopyFrom("changed"u8);
        var batch = new OriginRecordBatch
        {
            RequestId = "request-1",
            Records = { first, second },
        };

        var result = PeerContractValidator.Validate(batch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("one origin stream", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("strictly contiguous", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Content hash", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void PeerHelloCarriesCapabilitiesAndDurableCursors()
    {
        var hello = new PeerHello
        {
            ProtocolVersion = CurrentVersion(),
            PeerId = "peer-b",
            SessionId = "session-1",
            PublicKeySha256 = ByteString.CopyFrom(new byte[PeerContractRules.Sha256Size]),
            Capabilities =
            {
                new CapabilityDescriptor
                {
                    ModuleId = "monitor.system",
                    ModuleVersion = "1.0.0",
                    DeclarationSha256 = ByteString.CopyFrom(new byte[PeerContractRules.Sha256Size]),
                },
            },
            Cursors =
            {
                new OriginCursor
                {
                    OriginPeerId = "peer-a",
                    OriginStreamId = "stream-a",
                    AcceptedThroughSequence = 18,
                    UpdatedAt = Timestamp.FromDateTimeOffset(Now),
                },
            },
        };

        var parsed = PeerHello.Parser.ParseFrom(hello.ToByteArray());

        Assert.True(PeerContractValidator.Validate(parsed).IsValid);
        Assert.Equal((ulong)18, Assert.Single(parsed.Cursors).AcceptedThroughSequence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CompatibilityFailureCanSafelyReportUnsupportedMajor()
    {
        var failure = new CompatibilityFailure
        {
            Supported = new ProtocolCompatibility
            {
                MinimumMajor = 1,
                MaximumMajor = 1,
                MaximumMinor = 2,
            },
            Received = new ProtocolVersion { Major = 2 },
            SafeMessage = "Peer protocol major is unsupported.",
        };

        var result = PeerContractValidator.Validate(failure);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.DoesNotContain("key", failure.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void CompatibilityFailureCannotCarryIdentityOrProofSecrets()
    {
        var fields = CompatibilityFailure.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(field => field.Name)
            .ToArray();

        Assert.Equal(["supported", "received", "safe_message"], fields);
        Assert.DoesNotContain(fields, field =>
            field.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("proof", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OriginRecordRequiresDeclaredP256AlgorithmAndFixedSignature()
    {
        var record = CreateRecord(1, "record"u8.ToArray());
        record.SignatureAlgorithm = SignatureAlgorithm.Unspecified;
        record.OriginSignature = ByteString.CopyFrom(
            new byte[EnrollmentContractRules.EcdsaP256SignatureSize - 1]);
        var batch = new OriginRecordBatch
        {
            RequestId = "request-1",
            Records = { record },
        };

        var result = PeerContractValidator.Validate(batch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Signature algorithm", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Origin signature", StringComparison.Ordinal));
    }

    private static OriginRecord CreateRecord(ulong sequence, byte[] bytes) =>
        new()
        {
            OriginPeerId = "peer-a",
            OriginStreamId = "stream-a",
            Sequence = sequence,
            SubjectEntityId = "entity-1",
            CapabilityId = "monitor.system",
            SignalId = "cpu.utilization",
            RecordBytes = ByteString.CopyFrom(bytes),
            ContentSha256 = ByteString.CopyFrom(SHA256.HashData(bytes)),
            OriginSignature = ByteString.CopyFrom(new byte[EnrollmentContractRules.EcdsaP256SignatureSize]),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(sequence)),
            Quality = ObservationQuality.Valid,
        };

    private static ProtocolVersion CurrentVersion() =>
        new() { Major = PeerContractRules.ProtocolMajor };

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
}
