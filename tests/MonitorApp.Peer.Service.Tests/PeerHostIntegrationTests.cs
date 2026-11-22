using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Service;
using Xunit;

namespace MonitorApp.Peer.Service.Tests;

public sealed class PeerHostIntegrationTests
{
    [Fact]
    public void BindPolicyRejectsWildcardPublicAndImplicitLoopback()
    {
        Assert.Throws<InvalidOperationException>(() => PeerBindPolicy.Validate(IPAddress.Any, false));
        Assert.Throws<InvalidOperationException>(() => PeerBindPolicy.Validate(IPAddress.Parse("8.8.8.8"), false));
        Assert.Throws<InvalidOperationException>(() => PeerBindPolicy.Validate(IPAddress.Loopback, false));
        PeerBindPolicy.Validate(IPAddress.Parse("192.168.1.40"), false);
        PeerBindPolicy.Validate(IPAddress.Loopback, true);
    }

    [Fact]
    public async Task CapabilityBeforeProofIsRejected()
    {
        await using var host = await TestHost.StartAsync(new TestCoordinator());
        using var call = host.Client.PeerSession();
        await WriteHelloAsync(call);
        await ReadHandshakeAsync(call);
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            CapabilityExchange = Capabilities("remote.module"),
        });

        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(ProtocolErrorCode.Unauthorized, call.ResponseStream.Current.Error.Code);
    }

    [Fact]
    public async Task UnsupportedMajorReturnsCompatibilityFailure()
    {
        await using var host = await TestHost.StartAsync(new TestCoordinator());
        using var call = host.Client.PeerSession();
        var hello = RemoteHello();
        hello.ProtocolVersion.Major = 2;
        await call.RequestStream.WriteAsync(new PeerSessionRequest { Hello = hello });

        Assert.True(await call.ResponseStream.MoveNext());
        var failure = call.ResponseStream.Current.Incompatibility;
        Assert.Equal(1u, failure.Supported.MinimumMajor);
        Assert.Equal(2u, failure.Received.Major);
        Assert.DoesNotContain("peer-remote", failure.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionWithoutBilateralServerProofSupportFailsCompatibility()
    {
        await using var host = await TestHost.StartAsync(new TestCoordinator());
        using var call = host.Client.PeerSession();
        var hello = RemoteHello();
        hello.ProtocolVersion.Minor = 0;
        await call.RequestStream.WriteAsync(new PeerSessionRequest { Hello = hello });

        Assert.True(await call.ResponseStream.MoveNext());
        var failure = call.ResponseStream.Current.Incompatibility;
        Assert.Equal(PeerContractRules.BilateralSessionProofMinor, failure.Supported.MinimumMinor);
        Assert.Equal(0u, failure.Received.Minor);
    }

    [Fact]
    public async Task AuthenticatedCapabilityAndHeartbeatSucceed()
    {
        await using var host = await TestHost.StartAsync(new TestCoordinator());
        using var call = host.Client.PeerSession();
        await AuthenticateAsync(call);

        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            CapabilityExchange = Capabilities("remote.module"),
        });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal("local.module", call.ResponseStream.Current.CapabilityExchange.Capabilities[0].ModuleId);

        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
        });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(PeerSessionResponse.MessageOneofCase.Heartbeat, call.ResponseStream.Current.MessageCase);
    }

    [Fact]
    public async Task ClientCancellationReachesCoordinator()
    {
        var coordinator = new TestCoordinator { BlockHeartbeat = true };
        await using var host = await TestHost.StartAsync(coordinator);
        using var cancellation = new CancellationTokenSource();
        using var call = host.Client.PeerSession(cancellationToken: cancellation.Token);
        await AuthenticateAsync(call);
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
        });
        await coordinator.HeartbeatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await coordinator.HeartbeatCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AuthenticateAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call)
    {
        await WriteHelloAsync(call);
        var (_, challenge, _) = await ReadHandshakeAsync(call);
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Proof = new PeerProof
            {
                SessionId = "session-1",
                ChallengeId = challenge.ChallengeId,
                SignerPeerId = "peer-remote",
                VerifierPeerId = "peer-local",
                SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                ProofSignature = ByteString.CopyFrom(new byte[64]),
            },
        });
    }

    private static Task WriteHelloAsync(AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call) =>
        call.RequestStream.WriteAsync(new PeerSessionRequest { Hello = RemoteHello() });

    private static async Task<(PeerHello Hello, PeerChallenge Challenge, PeerProof Proof)> ReadHandshakeAsync(
        AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call)
    {
        Assert.True(await call.ResponseStream.MoveNext());
        var hello = call.ResponseStream.Current.Hello;
        Assert.True(await call.ResponseStream.MoveNext());
        var challenge = call.ResponseStream.Current.Challenge;
        Assert.True(await call.ResponseStream.MoveNext());
        return (hello, challenge, call.ResponseStream.Current.Proof);
    }

    private static PeerHello RemoteHello() => new()
    {
        ProtocolVersion = new ProtocolVersion { Major = 1, Minor = PeerContractRules.BilateralSessionProofMinor },
        PeerId = "peer-remote",
        PublicKeySha256 = ByteString.CopyFrom(new byte[32]),
        SessionId = "session-1",
    };

    private static CapabilityExchange Capabilities(string moduleId)
    {
        var exchange = new CapabilityExchange();
        exchange.Capabilities.Add(new CapabilityDescriptor
        {
            ModuleId = moduleId,
            ModuleVersion = "1.0.0",
            DeclarationSha256 = ByteString.CopyFrom(new byte[32]),
        });
        return exchange;
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly GrpcChannel channel;

        private TestHost(WebApplication app, GrpcChannel channel)
        {
            this.app = app;
            this.channel = channel;
            Client = new PeerService.PeerServiceClient(channel);
        }

        public PeerService.PeerServiceClient Client { get; }

        public static async Task<TestHost> StartAsync(TestCoordinator coordinator)
        {
            var port = FreePort();
            var app = await PeerHostApplication.CreateAsync(
            [
                "--MonitorApp:Peer:BindAddress=127.0.0.1",
                $"--MonitorApp:Peer:Port={port}",
                "--MonitorApp:Peer:AllowLoopback=true",
                "--MonitorApp:Peer:CoordinatorTimeoutSeconds=10",
            ], services =>
            {
                services.AddSingleton<IPeerSessionCoordinator>(coordinator);
                services.AddSingleton<IPeerPairingCoordinator, EmptyPairingCoordinator>();
            });
            await app.StartAsync();
            return new TestHost(app, GrpcChannel.ForAddress($"http://127.0.0.1:{port}"));
        }

        public async ValueTask DisposeAsync()
        {
            channel.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class EmptyPairingCoordinator : IPeerPairingCoordinator
    {
        public ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(
            PeerPairingSessionContext context,
            PairingFrame frame,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PairingFrame>>([]);
    }

    private sealed class TestCoordinator : IPeerSessionCoordinator
    {
        public bool BlockHeartbeat { get; init; }

        public TaskCompletionSource HeartbeatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HeartbeatCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new PeerSessionOpenResult(
                new PeerHello
                {
                    ProtocolVersion = new ProtocolVersion { Major = 1, Minor = PeerContractRules.BilateralSessionProofMinor },
                    PeerId = "peer-local",
                    PublicKeySha256 = ByteString.CopyFrom(new byte[32]),
                    SessionId = remoteHello.SessionId,
                },
                new PeerChallenge
                {
                    SessionId = remoteHello.SessionId,
                    ChallengeId = "challenge-1",
                    ChallengerPeerId = "peer-local",
                    ChallengedPeerId = remoteHello.PeerId,
                    ProofChallenge = ByteString.CopyFrom(new byte[32]),
                    SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                    CreatedAt = Timestamp.FromDateTimeOffset(now),
                    ExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
                },
                new PeerProof
                {
                    SessionId = remoteHello.SessionId,
                    ChallengeId = "challenge-1",
                    SignerPeerId = "peer-local",
                    VerifierPeerId = remoteHello.PeerId,
                    SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                    ProofSignature = ByteString.CopyFrom(new byte[64]),
                }));
        }

        public ValueTask<PeerAuthenticationResult> AuthenticateAsync(
            PeerHello remoteHello,
            PeerProof proof,
            CancellationToken cancellationToken) => ValueTask.FromResult(new PeerAuthenticationResult(true));

        public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(
            string remotePeerId,
            CapabilityExchange remoteCapabilities,
            CancellationToken cancellationToken) => ValueTask.FromResult(Capabilities("local.module"));

        public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(
            string remotePeerId,
            OriginRangeRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<OriginAck> AcceptOriginBatchAsync(
            string remotePeerId,
            OriginRecordBatch batch,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AcceptOriginAckAsync(
            string remotePeerId,
            OriginAck acknowledgement,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<PeerHeartbeat> HeartbeatAsync(
            string remotePeerId,
            PeerHeartbeat heartbeat,
            CancellationToken cancellationToken)
        {
            HeartbeatStarted.TrySetResult();
            if (BlockHeartbeat)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    HeartbeatCancelled.TrySetResult();
                    throw;
                }
            }

            return new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        }
    }
}
