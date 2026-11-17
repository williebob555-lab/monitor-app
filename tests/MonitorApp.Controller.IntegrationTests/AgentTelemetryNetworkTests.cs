using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Controller;
using MonitorApp.Persistence;
using Xunit;

namespace MonitorApp.Controller.IntegrationTests;

public sealed class AgentTelemetryNetworkTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "MonitorApp.Telemetry.Network.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string adminToken = new('a', 32);
    private readonly byte[] pepper = RandomNumberGenerator.GetBytes(32);
    private ECDsa? deviceKey;
    private string? agentId;
    private string? database;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        database = Path.Combine(directory, "controller.db");
        deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        agentId = await EnrollAsync(database, deviceKey);
    }

    public Task DisposeAsync()
    {
        deviceKey?.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "Controller")]
    public async Task ProductionTransportUploadsAndOwnerReadsDurableCurrentState()
    {
        await using var host = await StartAsync();
        var spoolPath = Path.Combine(directory, "agent-spool.db");
        await using var spool = await SqliteTelemetrySpool.OpenAsync(
            new TelemetrySpoolOptions(spoolPath, 100, 1_000_000));
        var submitted = await spool.EnqueueAsync(Envelope(agentId!, 0, 42));
        await using var transport = new GrpcAgentSessionTransport(host.Address, deviceKey!);
        var coordinator = new AgentSessionCoordinator(spool, transport);

        Assert.Equal(1, await coordinator.FlushOnceAsync(
            agentId!,
            "boot-1",
            "0.1.0",
            Platform.Windows,
            [new Capability { Id = "memory", State = CapabilityState.Available }]));
        Assert.Equal(0, (await spool.GetStateAsync()).PendingCount);

        using var client = Http2Client(host.Address);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/inventory")).StatusCode);
        client.DefaultRequestHeaders.Add("x-monitor-admin-token", adminToken);
        var inventory = await client.GetStringAsync("api/inventory");
        var metrics = await client.GetStringAsync("api/metrics/current");
        Assert.Contains(agentId!, inventory, StringComparison.Ordinal);
        Assert.Contains("system.memory.used", metrics, StringComparison.Ordinal);
        Assert.Contains("42", metrics, StringComparison.Ordinal);

        await host.DisposeAsync();
        await using var restarted = await StartAsync();
        await using var afterRestart = new GrpcAgentSessionTransport(restarted.Address, deviceKey!);
        var controllerHello = await afterRestart.OpenAsync(Hello(agentId!, 2), CancellationToken.None);
        Assert.Equal(1UL, controllerHello.AcceptedThroughSequence);

        var duplicate = new TelemetryBatch();
        duplicate.Envelopes.Add(submitted);
        Assert.Equal(1UL, (await afterRestart.SendAsync(duplicate, CancellationToken.None)).AcceptedThroughSequence);

        var conflict = new TelemetryBatch();
        conflict.Envelopes.Add(Envelope(agentId!, 1, 99));
        var rejected = await Assert.ThrowsAsync<RpcException>(
            () => afterRestart.SendAsync(conflict, CancellationToken.None));
        Assert.Equal(StatusCode.FailedPrecondition, rejected.StatusCode);
    }

    [Fact]
    [Trait("Category", "Controller")]
    public async Task ChallengeProofIsBoundToItsExactStreamAndErrorsRedactSignature()
    {
        await using var host = await StartAsync();
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new AgentService.AgentServiceClient(channel);
        using var streamA = client.AgentSession();
        using var streamB = client.AgentSession();
        await streamA.RequestStream.WriteAsync(new AgentSessionRequest { Hello = Hello(agentId!, 1) });
        await streamB.RequestStream.WriteAsync(new AgentSessionRequest { Hello = Hello(agentId!, 1) });
        var challengeA = await ReadChallengeAsync(streamA);
        _ = await ReadChallengeAsync(streamB);
        var proofA = Sign(challengeA, deviceKey!);

        await streamB.RequestStream.WriteAsync(new AgentSessionRequest { Proof = proofA });
        var mismatch = await Assert.ThrowsAsync<RpcException>(
            () => streamB.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unauthenticated, mismatch.StatusCode);
        Assert.DoesNotContain(
            Convert.ToBase64String(proofA.ProofSignature.ToByteArray()),
            mismatch.Status.Detail,
            StringComparison.Ordinal);

        await streamA.RequestStream.WriteAsync(new AgentSessionRequest { Proof = proofA });
        Assert.True(await streamA.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(
            AgentSessionResponse.MessageOneofCase.Hello,
            streamA.ResponseStream.Current.MessageCase);

        using var streamC = client.AgentSession();
        await streamC.RequestStream.WriteAsync(new AgentSessionRequest { Hello = Hello(agentId!, 1) });
        _ = await ReadChallengeAsync(streamC);
        await streamC.RequestStream.WriteAsync(new AgentSessionRequest { Proof = proofA });
        var replay = await Assert.ThrowsAsync<RpcException>(
            () => streamC.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unauthenticated, replay.StatusCode);

        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var streamD = client.AgentSession();
        await streamD.RequestStream.WriteAsync(new AgentSessionRequest { Hello = Hello(agentId!, 1) });
        var challengeD = await ReadChallengeAsync(streamD);
        await streamD.RequestStream.WriteAsync(new AgentSessionRequest
        {
            Proof = Sign(challengeD, wrongKey),
        });
        var wrong = await Assert.ThrowsAsync<RpcException>(
            () => streamD.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unauthenticated, wrong.StatusCode);
    }

    private async Task<TestHost> StartAsync()
    {
        var port = FreePort();
        var app = await ControllerApplication.CreateAsync(
        [
            $"--MonitorApp:DatabasePath={database}",
            $"--MonitorApp:SecretPepper={Convert.ToBase64String(pepper)}",
            $"--MonitorApp:AdminToken={adminToken}",
            $"--MonitorApp:InsecureHttp2Port={port}",
        ]);
        await app.StartAsync();
        return new TestHost(app, new Uri($"http://127.0.0.1:{port}"));
    }

    private async Task<string> EnrollAsync(string path, ECDsa key)
    {
        var store = new EnrollmentStore(EnrollmentStoreOptions.Create(path, pepper));
        await store.InitializeAsync();
        var created = await store.CreateAsync(new CreateEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            Platform = Platform.Windows,
            DisplayName = "network-node",
        });
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var payload = ProofPayload.CreateEnrollment(
            created.EnrollmentId,
            created.ProofChallenge.Span,
            publicKey);
        var completed = await store.CompleteAsync(new CompleteEnrollmentRequest
        {
            ProtocolVersion = new ProtocolVersion { Major = 1 },
            EnrollmentId = created.EnrollmentId,
            EnrollmentSecret = created.EnrollmentSecret,
            Platform = Platform.Windows,
            DisplayName = "network-node",
            PublicKeySpki = ByteString.CopyFrom(publicKey),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        });
        return completed.AgentId;
    }

    private static async Task<AgentSessionChallenge> ReadChallengeAsync(
        AsyncDuplexStreamingCall<AgentSessionRequest, AgentSessionResponse> stream)
    {
        Assert.True(await stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(
            AgentSessionResponse.MessageOneofCase.Challenge,
            stream.ResponseStream.Current.MessageCase);
        return stream.ResponseStream.Current.Challenge;
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

    private static AgentHello Hello(string agentId, ulong next) => new()
    {
        ProtocolVersion = new ProtocolVersion { Major = 1 },
        AgentId = agentId,
        BootId = "boot-1",
        AgentVersion = "0.1.0",
        Platform = Platform.Windows,
        LowestPendingSequence = next,
        NextSequence = next,
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

    private static HttpClient Http2Client(Uri address) => new()
    {
        BaseAddress = address,
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        private bool disposed;

        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
