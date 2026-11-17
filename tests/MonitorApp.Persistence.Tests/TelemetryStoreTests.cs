using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Persistence;
using Xunit;

namespace MonitorApp.Persistence.Tests;

public sealed class TelemetryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "MonitorApp.Telemetry.Persistence.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task AuthenticationIngestReplayAndCurrentStateSurviveRestart()
    {
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "controller.db");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agentId = await EnrollAsync(database, key);
        var hello = Hello(agentId);
        var first = Envelope(agentId, 1, 10);
        var second = Envelope(agentId, 2, 20);

        var store = new TelemetryStore(database);
        await store.InitializeAsync();
        var challenge = await store.CreateSessionChallengeAsync(agentId, "session-a");
        var proof = Sign(challenge, key);
        await store.VerifySessionProofAsync(challenge, proof);
        var replay = await Assert.ThrowsAsync<TelemetryFailureException>(
            () => store.VerifySessionProofAsync(challenge, proof));
        Assert.Equal(TelemetryFailureReason.Replay, replay.Reason);

        var batch = new TelemetryBatch();
        batch.Envelopes.Add(first);
        batch.Envelopes.Add(second);
        Assert.Equal(2UL, await store.IngestAsync(hello, batch));

        var restarted = new TelemetryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal(2UL, await restarted.GetAcceptedThroughAsync(agentId));
        Assert.Equal(2UL, await restarted.IngestAsync(hello, batch));

        var conflicting = new TelemetryBatch();
        conflicting.Envelopes.Add(Envelope(agentId, 2, 99));
        var conflict = await Assert.ThrowsAsync<TelemetryFailureException>(
            () => restarted.IngestAsync(hello, conflicting));
        Assert.Equal(TelemetryFailureReason.SequenceConflict, conflict.Reason);

        var current = Assert.Single(await restarted.ReadCurrentMetricsAsync(agentId));
        Assert.Equal(2UL, current.Sequence);
        Assert.Equal((ulong)20, Assert.IsType<ulong>(current.Value));
        Assert.NotEqual(default, current.IngestedAt);
        Assert.Single(await restarted.ReadInventoryAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> EnrollAsync(string database, ECDsa key)
    {
        var enrollment = new EnrollmentStore(
            EnrollmentStoreOptions.Create(database, RandomNumberGenerator.GetBytes(32)));
        await enrollment.InitializeAsync();
        var created = await enrollment.CreateAsync(new CreateEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            Platform = Platform.Windows,
            DisplayName = "telemetry-node",
        });
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var payload = ProofPayload.CreateEnrollment(
            created.EnrollmentId,
            created.ProofChallenge.Span,
            publicKey);
        var completed = await enrollment.CompleteAsync(new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = created.EnrollmentId,
            EnrollmentSecret = created.EnrollmentSecret,
            Platform = Platform.Windows,
            DisplayName = "telemetry-node",
            PublicKeySpki = ByteString.CopyFrom(publicKey),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        });
        return completed.AgentId;
    }

    private static AgentSessionProof Sign(AgentSessionChallenge challenge, ECDsa key)
    {
        var payload = ProofPayload.CreateAgentSession(
            challenge.AgentId,
            challenge.SessionId,
            challenge.ChallengeId,
            challenge.ProofChallenge.Span);
        return new AgentSessionProof
        {
            AgentId = challenge.AgentId,
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        };
    }

    private static AgentHello Hello(string agentId) => new()
    {
        ProtocolVersion = new ProtocolVersion { Major = 1 },
        AgentId = agentId,
        BootId = "boot-1",
        AgentVersion = "0.1.0",
        Platform = Platform.Windows,
        LowestPendingSequence = 1,
        NextSequence = 3,
    };

    private static TelemetryEnvelope Envelope(string agentId, ulong sequence, ulong value) => new()
    {
        AgentId = agentId,
        BootId = "boot-1",
        CollectorId = "system",
        Sequence = sequence,
        ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Metric = new MetricSample
        {
            MetricId = "system.memory.used",
            ComponentId = "host",
            Unit = MetricUnit.Bytes,
            Quality = ObservationQuality.Valid,
            Value = new MetricValue { UnsignedIntegerValue = value },
        },
    };
}
