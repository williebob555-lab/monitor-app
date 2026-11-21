using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;
using Xunit;
using ContractPeerProof = MonitorApp.Contracts.V1.PeerProof;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Transport.Tests;

public sealed class PeerGrpcConnectorTests : IDisposable
{
    private readonly EcdsaPeerIdentityKey clientKey = EcdsaPeerIdentityKey.Create();
    private readonly EcdsaPeerIdentityKey serverKey = EcdsaPeerIdentityKey.Create();

    [Fact]
    public void EndpointPolicyFailsClosedForPublicOrImplicitlyInsecureEndpoints()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorOptions(new Uri("http://8.8.8.8:5000"), PeerTransportSecurity.TailscaleEncrypted).ValidateForTest());
        Assert.Throws<InvalidOperationException>(() => ConnectorOptions(new Uri("http://192.168.1.20:5000"), PeerTransportSecurity.Tls).ValidateForTest());
        Assert.Throws<InvalidOperationException>(() => ConnectorOptions(new Uri("http://127.0.0.1:5000"), PeerTransportSecurity.TailscaleEncrypted).ValidateForTest());
    }

    [Fact]
    public async Task BilateralAuthenticationCapabilityAndReplicationDispatchSucceed()
    {
        var coordinator = Coordinator();
        await using var host = await TestHost.StartAsync(coordinator);
        var application = new TestApplication(includeRequests: true);
        await using var connector = Connector(host.Address, application);
        using var cancellation = new CancellationTokenSource();
        var run = connector.RunSessionAsync(cancellation.Token);

        await coordinator.TwoAcknowledgements.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(application.Authenticated.Task.IsCompletedSuccessfully);
        Assert.Equal(1, coordinator.RangeRequests);
        Assert.Equal(1, coordinator.Batches);
        Assert.Equal(1, application.AcceptedAcknowledgements);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task IncompatibleVersionFailsWithoutApplicationOperations()
    {
        var coordinator = Coordinator();
        await using var host = await TestHost.StartAsync(coordinator);
        var application = new TestApplication(false);
        var auth = new DelegatingAuthentication(Auth(), helloMajor: 99);
        await using var connector = new PeerGrpcConnector(ConnectorOptions(host.Address), auth, application);

        await Assert.ThrowsAsync<PeerCompatibilityException>(() => connector.RunSessionAsync(CancellationToken.None));
        Assert.False(application.Authenticated.Task.IsCompleted);
        Assert.Equal(0, coordinator.Operations);
    }

    [Fact]
    public async Task WrongClientProofIsRejectedAndNoOperationIsDispatched()
    {
        var coordinator = Coordinator();
        await using var host = await TestHost.StartAsync(coordinator);
        var application = new TestApplication(false);
        var auth = new DelegatingAuthentication(Auth(), corruptProof: true);
        await using var connector = new PeerGrpcConnector(ConnectorOptions(host.Address), auth, application);

        await Assert.ThrowsAsync<PeerAuthenticationException>(() => connector.RunSessionAsync(CancellationToken.None));
        Assert.False(application.Authenticated.Task.IsCompleted);
        Assert.Equal(0, coordinator.Operations);
    }

    [Fact]
    public async Task WrongServerProofIsRejectedBeforeClientProofOrOperations()
    {
        var coordinator = Coordinator(corruptServerProof: true);
        await using var host = await TestHost.StartAsync(coordinator);
        var application = new TestApplication(false);
        await using var connector = Connector(host.Address, application);

        await Assert.ThrowsAsync<PeerAuthenticationException>(() => connector.RunSessionAsync(CancellationToken.None));
        Assert.Equal(0, coordinator.AuthenticationAttempts);
        Assert.Equal(0, coordinator.Operations);
    }

    [Fact]
    public async Task CancellationStopsAnAuthenticatedHeartbeat()
    {
        var coordinator = Coordinator(blockHeartbeat: true);
        await using var host = await TestHost.StartAsync(coordinator);
        await using var connector = new PeerGrpcConnector(ConnectorOptions(host.Address) with { HeartbeatInterval = TimeSpan.FromMilliseconds(20) }, Auth(), new TestApplication(false));
        using var cancellation = new CancellationTokenSource();
        var run = connector.RunSessionAsync(cancellation.Token);
        await coordinator.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await coordinator.HeartbeatCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectBackoffThenAuthenticatesWhenHostAppears()
    {
        var port = FreePort();
        var delay = new ControlledDelay();
        var application = new TestApplication(false);
        await using var connector = new PeerGrpcConnector(ConnectorOptions(new Uri($"http://127.0.0.1:{port}")), Auth(), application, delay);
        using var cancellation = new CancellationTokenSource();
        var run = connector.RunAsync(cancellation.Token);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var host = await TestHost.StartAsync(Coordinator(), port);
        delay.Release.TrySetResult();
        await application.Authenticated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delay.Delays.Count >= 1);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PeerTransportState.Stopped, connector.State);
    }

    public void Dispose() { clientKey.Dispose(); serverKey.Dispose(); }

    private P256PeerSessionAuthenticationProvider Auth() => new(ClientIdentity, ServerIdentity, clientKey);
    private TestCoordinator Coordinator(bool corruptServerProof = false, bool blockHeartbeat = false) =>
        new(ClientIdentity, ServerIdentity, serverKey) { CorruptServerProof = corruptServerProof, BlockHeartbeat = blockHeartbeat };
    private PeerPublicIdentity ClientIdentity => PeerPublicIdentity.Create("a-client", clientKey);
    private PeerPublicIdentity ServerIdentity => PeerPublicIdentity.Create("z-server", serverKey);
    private PeerGrpcConnector Connector(Uri address, TestApplication application) => new(ConnectorOptions(address), Auth(), application);

    private static PeerTransportOptions ConnectorOptions(Uri address, PeerTransportSecurity security = PeerTransportSecurity.LoopbackDevelopment) =>
        new(new PeerTransportEndpoint(address, "z-server", security), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100), 1024 * 1024);

    private static CapabilityExchange Capabilities(string id)
    {
        var value = new CapabilityExchange();
        value.Capabilities.Add(new CapabilityDescriptor { ModuleId = id, ModuleVersion = "1", DeclarationSha256 = ByteString.CopyFrom(new byte[32]) });
        return value;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestApplication(bool includeRequests) : IPeerSessionApplication
    {
        public CapabilityExchange LocalCapabilities => Capabilities("client.module");
        public TaskCompletionSource Authenticated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AcceptedAcknowledgements { get; private set; }
        public ValueTask OnAuthenticatedAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken)
        { Assert.Equal("server.module", remoteCapabilities.Capabilities[0].ModuleId); Authenticated.TrySetResult(); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<PeerSessionRequest> GetPendingRequestsAsync(string remotePeerId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!includeRequests) yield break;
            yield return new PeerSessionRequest { OriginRangeRequest = new OriginRangeRequest { RequestId = "range", OriginPeerId = "a-client", OriginStreamId = "stream", FirstSequence = 1, MaximumRecordCount = 10, MaximumBatchBytes = 4096 } };
            yield return new PeerSessionRequest { OriginBatch = Batch("push") };
            yield return new PeerSessionRequest { OriginAck = Ack(2) };
            await Task.CompletedTask;
        }
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) => ValueTask.FromResult(Ack(1));
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken)
        { AcceptedAcknowledgements++; return ValueTask.CompletedTask; }
    }

    private sealed class TestCoordinator(PeerPublicIdentity client, PeerPublicIdentity server, IPeerIdentityKey serverKey) : IPeerSessionCoordinator
    {
        private PeerProofChallenge? challenge;
        private readonly InMemoryPeerChallengeReplayGuard guard = new();
        public bool CorruptServerProof { get; init; }
        public bool BlockHeartbeat { get; init; }
        public int AuthenticationAttempts { get; private set; }
        public int RangeRequests { get; private set; }
        public int Batches { get; private set; }
        public int Operations => RangeRequests + Batches;
        public TaskCompletionSource TwoAcknowledgements { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeartbeatCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int acknowledgements;

        public ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            challenge = PeerProofService.CreateChallenge(remoteHello.SessionId, Guid.NewGuid().ToString("N"), client, server,
                new PeerProtocolVersion(1, 1), now, TimeSpan.FromMinutes(1));
            var proof = PeerProofService.CreateProof(serverKey, challenge, PeerProofPurpose.Reconnect, PeerProofRole.Responder);
            if (CorruptServerProof) proof.Signature[0] ^= 1;
            return ValueTask.FromResult(new PeerSessionOpenResult(
                new PeerHello { ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 1 }, PeerId = server.PeerId, PublicKeySha256 = ByteString.CopyFrom(server.PublicKeyFingerprint), SessionId = remoteHello.SessionId },
                WireChallenge(challenge), WireProof(challenge, server.PeerId, client.PeerId, proof.Signature), null));
        }
        public ValueTask<PeerAuthenticationResult> AuthenticateAsync(PeerHello remoteHello, ContractPeerProof proof, CancellationToken cancellationToken)
        {
            AuthenticationAttempts++;
            var result = PeerProofService.VerifyAndConsume(challenge!, new CorePeerProof(PeerProofPurpose.Reconnect, PeerProofRole.Initiator, proof.ProofSignature.ToByteArray()), DateTimeOffset.UtcNow, guard);
            return ValueTask.FromResult(result.IsTrusted ? new PeerAuthenticationResult(true) : new PeerAuthenticationResult(false,
                new ProtocolError { Code = ProtocolErrorCode.InvalidIdentity, SafeMessage = "Invalid peer proof." }));
        }
        public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) => ValueTask.FromResult(Capabilities("server.module"));
        public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(string remotePeerId, OriginRangeRequest request, CancellationToken cancellationToken) { RangeRequests++; return ValueTask.FromResult(Batch(request.RequestId)); }
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) { Batches++; return ValueTask.FromResult(Ack(1)); }
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken)
        { if (Interlocked.Increment(ref acknowledgements) >= 2) TwoAcknowledgements.TrySetResult(); return ValueTask.CompletedTask; }
        public async ValueTask<PeerHeartbeat> HeartbeatAsync(string remotePeerId, PeerHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            HeartbeatStarted.TrySetResult();
            if (BlockHeartbeat) try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } catch (OperationCanceledException) { HeartbeatCancelled.TrySetResult(); throw; }
            return new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        }
    }

    private sealed class EmptyPairingCoordinator : IPeerPairingCoordinator
    {
        public ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(PeerPairingSessionContext context, PairingFrame frame, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PairingFrame>>([]);
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public static async Task<TestHost> StartAsync(TestCoordinator coordinator, int? requestedPort = null)
        {
            var port = requestedPort ?? FreePort();
            var app = await PeerHostApplication.CreateAsync(["--MonitorApp:Peer:BindAddress=127.0.0.1", $"--MonitorApp:Peer:Port={port}", "--MonitorApp:Peer:AllowLoopback=true"], services =>
            { services.AddSingleton<IPeerSessionCoordinator>(coordinator); services.AddSingleton<IPeerPairingCoordinator, EmptyPairingCoordinator>(); });
            await app.StartAsync(); return new TestHost(app, new Uri($"http://127.0.0.1:{port}"));
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }

    private sealed class ControlledDelay : IPeerReconnectDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        { Delays.Add(delay); Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }

    private sealed class DelegatingAuthentication(IPeerSessionAuthenticationProvider inner, uint? helloMajor = null, bool corruptProof = false) : IPeerSessionAuthenticationProvider
    {
        public async ValueTask<PeerHello> CreateHelloAsync(string sessionId, CancellationToken cancellationToken)
        { var hello = await inner.CreateHelloAsync(sessionId, cancellationToken); if (helloMajor.HasValue) hello.ProtocolVersion.Major = helloMajor.Value; return hello; }
        public async ValueTask<ContractPeerProof> VerifyServerAndCreateClientProofAsync(PeerHello sentHello, PeerHello remoteHello, PeerChallenge challenge,
            ContractPeerProof serverProof, string expectedPeerId, CancellationToken cancellationToken)
        {
            var proof = await inner.VerifyServerAndCreateClientProofAsync(sentHello, remoteHello, challenge, serverProof, expectedPeerId, cancellationToken);
            if (corruptProof)
            {
                var bytes = proof.ProofSignature.ToByteArray();
                bytes[0] ^= 1;
                proof.ProofSignature = ByteString.CopyFrom(bytes);
            }

            return proof;
        }
    }

    private static PeerChallenge WireChallenge(PeerProofChallenge value) => new()
    {
        SessionId = value.SessionId,
        ChallengeId = value.ChallengeId,
        ChallengerPeerId = value.Responder.PeerId,
        ChallengedPeerId = value.Initiator.PeerId,
        ProofChallenge = ByteString.CopyFrom(value.Challenge),
        SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
        CreatedAt = Timestamp.FromDateTimeOffset(value.CreatedAt),
        ExpiresAt = Timestamp.FromDateTimeOffset(value.ExpiresAt)
    };
    private static ContractPeerProof WireProof(PeerProofChallenge challenge, string signer, string verifier, byte[] signature) => new()
    {
        SessionId = challenge.SessionId,
        ChallengeId = challenge.ChallengeId,
        SignerPeerId = signer,
        VerifierPeerId = verifier,
        SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
        ProofSignature = ByteString.CopyFrom(signature)
    };
    private static OriginAck Ack(ulong sequence) => new() { Cursor = new OriginCursor { OriginPeerId = "a-client", OriginStreamId = "stream", AcceptedThroughSequence = sequence, UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) } };
    private static OriginRecordBatch Batch(string requestId)
    {
        var bytes = new byte[] { 1 };
        var batch = new OriginRecordBatch { RequestId = requestId };
        batch.Records.Add(new OriginRecord
        {
            OriginPeerId = "a-client",
            OriginStreamId = "stream",
            Sequence = 1,
            SubjectEntityId = "device",
            CapabilityId = "module",
            SignalId = "signal",
            RecordBytes = ByteString.CopyFrom(bytes),
            ContentSha256 = ByteString.CopyFrom(SHA256.HashData(bytes)),
            OriginSignature = ByteString.CopyFrom(new byte[64]),
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Quality = ObservationQuality.Valid,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256
        });
        return batch;
    }
}

internal static class PeerTransportOptionsTestExtensions
{
    public static void ValidateForTest(this PeerTransportOptions options) => _ = new PeerGrpcConnector(options, new NullAuthentication(), new NullApplication());
    private sealed class NullAuthentication : IPeerSessionAuthenticationProvider
    {
        public ValueTask<PeerHello> CreateHelloAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ContractPeerProof> VerifyServerAndCreateClientProofAsync(PeerHello sentHello, PeerHello remoteHello, PeerChallenge challenge, ContractPeerProof serverProof, string expectedPeerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class NullApplication : IPeerSessionApplication
    {
        public CapabilityExchange LocalCapabilities => new();
        public ValueTask OnAuthenticatedAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<PeerSessionRequest> GetPendingRequestsAsync(string remotePeerId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
