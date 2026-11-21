using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Service;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Transport.Tests;

public sealed class PeerPairingClientFailureTests : IDisposable
{
    private readonly EcdsaPeerIdentityKey clientKey = EcdsaPeerIdentityKey.Create();
    private readonly EcdsaPeerIdentityKey serverKey = EcdsaPeerIdentityKey.Create();

    [Theory]
    [InlineData(FailureMode.KeyMismatch)]
    [InlineData(FailureMode.PairingIdMismatch)]
    [InlineData(FailureMode.Rejected)]
    [InlineData(FailureMode.Expired)]
    [InlineData(FailureMode.WrongProof)]
    public async Task PairingFailureNeverPersistsTrust(FailureMode mode)
    {
        var server = new FailurePairingCoordinator(ServerIdentity, ClientIdentity, serverKey, mode);
        await using var host = await TestHost.StartAsync(server);
        var trust = new RecordingTrustSink();
        var local = new P256PeerInitialPairingCoordinator(ClientIdentity, clientKey, "Client",
            new ApproveCandidate(), trust);
        await using var client = new PeerPairingClient(Options(host.Address), local,
            new PeerInitialPairingOptions(2, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)));

        await Assert.ThrowsAsync<PeerAuthenticationException>(() =>
            client.PairAsync(new PeerInitialPairingRequest("pair-1", true)));
        Assert.Equal(0, trust.Persisted);
        if (mode == FailureMode.WrongProof) Assert.False(server.ReceivedClientProof);
    }

    [Fact]
    public async Task PairingRequiresExplicitInitiatingApprovalBeforeNetworkUse()
    {
        var server = new FailurePairingCoordinator(ServerIdentity, ClientIdentity, serverKey, FailureMode.Rejected);
        await using var host = await TestHost.StartAsync(server);
        var local = new P256PeerInitialPairingCoordinator(ClientIdentity, clientKey, "Client",
            new ApproveCandidate(), new RecordingTrustSink());
        await using var client = new PeerPairingClient(Options(host.Address), local);

        await Assert.ThrowsAsync<PeerAuthenticationException>(() =>
            client.PairAsync(new PeerInitialPairingRequest("pair-1", false)));
        Assert.Equal(0, server.FramesReceived);
    }

    [Fact]
    public async Task ReplayedRemotePairingProofIsRejected()
    {
        var trust = new RecordingTrustSink();
        var coordinator = new P256PeerInitialPairingCoordinator(ClientIdentity, clientKey, "Client", new ApproveCandidate(), trust);
        var localHello = coordinator.CreateLocalHello("pair-1");
        var remoteHello = new PairingHello
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 1 },
            PairingId = "pair-1",
            PeerId = ServerIdentity.PeerId,
            DisplayName = "Server",
            PublicKeySpki = ByteString.CopyFrom(ServerIdentity.PublicKeySpki),
            PublicKeySha256 = ByteString.CopyFrom(ServerIdentity.PublicKeyFingerprint),
        };
        var now = DateTimeOffset.UtcNow;
        var challenge = PeerProofService.CreateChallenge("pairing:pair-1", "challenge-1", ClientIdentity, ServerIdentity,
            new PeerProtocolVersion(1, 1), now, TimeSpan.FromMinutes(1));
        var remoteProof = PeerProofService.CreateProof(serverKey, challenge, PeerProofPurpose.Pairing, PeerProofRole.Responder);
        var wireChallenge = new PeerChallenge
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            ChallengerPeerId = ServerIdentity.PeerId,
            ChallengedPeerId = ClientIdentity.PeerId,
            ProofChallenge = ByteString.CopyFrom(challenge.Challenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(challenge.CreatedAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(challenge.ExpiresAt),
        };
        var wireProof = new MonitorApp.Contracts.V1.PeerProof
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            SignerPeerId = ServerIdentity.PeerId,
            VerifierPeerId = ClientIdentity.PeerId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(remoteProof.Signature),
        };

        _ = await coordinator.VerifyRemoteAndCreateLocalProofAsync(localHello, remoteHello, wireChallenge, wireProof, CancellationToken.None);
        await Assert.ThrowsAsync<PeerAuthenticationException>(async () =>
            await coordinator.VerifyRemoteAndCreateLocalProofAsync(localHello, remoteHello, wireChallenge, wireProof, CancellationToken.None));
        Assert.Equal(0, trust.Persisted);
    }

    public void Dispose() { clientKey.Dispose(); serverKey.Dispose(); }

    public enum FailureMode { KeyMismatch, PairingIdMismatch, Rejected, Expired, WrongProof }

    private PeerPublicIdentity ClientIdentity => PeerPublicIdentity.Create("a-client", clientKey);
    private PeerPublicIdentity ServerIdentity => PeerPublicIdentity.Create("z-server", serverKey);

    private static PeerTransportOptions Options(Uri address) => new(
        new PeerTransportEndpoint(address, "z-server", PeerTransportSecurity.LoopbackDevelopment),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), 1024 * 1024);

    private sealed class ApproveCandidate : IRemotePairingApproval
    {
        public ValueTask<bool> ApproveAsync(PairingRemoteCandidate candidate, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class RecordingTrustSink : IPeerTrustSink
    {
        public int Persisted { get; private set; }
        public ValueTask PersistTrustedPeerAsync(PairingRemoteCandidate candidate, DateTimeOffset pairedAt, CancellationToken cancellationToken)
        { Persisted++; return ValueTask.CompletedTask; }
    }

    private sealed class FailurePairingCoordinator(
        PeerPublicIdentity server,
        PeerPublicIdentity client,
        IPeerIdentityKey serverKey,
        FailureMode mode) : IPeerPairingCoordinator
    {
        public bool ReceivedClientProof { get; private set; }
        public int FramesReceived { get; private set; }

        public ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(PeerPairingSessionContext context, PairingFrame frame,
            CancellationToken cancellationToken)
        {
            FramesReceived++;
            if (frame.MessageCase == PairingFrame.MessageOneofCase.Proof)
            {
                ReceivedClientProof = true;
                return ValueTask.FromResult<IReadOnlyList<PairingFrame>>([]);
            }

            var now = DateTimeOffset.UtcNow;
            var hello = new PairingHello
            {
                ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 1 },
                PairingId = mode == FailureMode.PairingIdMismatch ? "different-pair" : frame.Hello.PairingId,
                PeerId = mode == FailureMode.KeyMismatch ? "other-server" : server.PeerId,
                DisplayName = "Server",
                PublicKeySpki = ByteString.CopyFrom(server.PublicKeySpki),
                PublicKeySha256 = ByteString.CopyFrom(server.PublicKeyFingerprint),
            };
            if (mode is FailureMode.Rejected or FailureMode.Expired or FailureMode.KeyMismatch or FailureMode.PairingIdMismatch)
            {
                return ValueTask.FromResult<IReadOnlyList<PairingFrame>>([
                    new() { Hello = hello },
                    new() { Approval = new PairingApproval
                    {
                        PairingId = frame.Hello.PairingId,
                        Disposition = mode == FailureMode.Rejected ? PairingApprovalDisposition.Rejected :
                            mode == FailureMode.Expired ? PairingApprovalDisposition.Expired : PairingApprovalDisposition.AwaitingLocalApproval,
                        ExpiresAt = Timestamp.FromDateTimeOffset(mode == FailureMode.Expired ? now.AddSeconds(-1) : now.AddMinutes(1)),
                        SafeMessage = "Pairing unavailable.",
                    } },
                ]);
            }

            var challenge = PeerProofService.CreateChallenge(frame.Hello.PairingId, "challenge-1", client, server,
                new PeerProtocolVersion(1, 1), now, TimeSpan.FromMinutes(1));
            var proof = PeerProofService.CreateProof(serverKey, challenge, PeerProofPurpose.Pairing, PeerProofRole.Responder);
            proof.Signature[0] ^= 1;
            return ValueTask.FromResult<IReadOnlyList<PairingFrame>>([
                new() { Hello = hello },
                new() { Approval = new PairingApproval { PairingId = frame.Hello.PairingId,
                    Disposition = PairingApprovalDisposition.Approved, ExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(1)), SafeMessage = "Approved." } },
                new() { Challenge = new PeerChallenge { SessionId = challenge.SessionId, ChallengeId = challenge.ChallengeId,
                    ChallengerPeerId = server.PeerId, ChallengedPeerId = client.PeerId, ProofChallenge = ByteString.CopyFrom(challenge.Challenge),
                    SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256, CreatedAt = Timestamp.FromDateTimeOffset(challenge.CreatedAt),
                    ExpiresAt = Timestamp.FromDateTimeOffset(challenge.ExpiresAt) } },
                new() { Proof = new MonitorApp.Contracts.V1.PeerProof { SessionId = challenge.SessionId, ChallengeId = challenge.ChallengeId,
                    SignerPeerId = server.PeerId, VerifierPeerId = client.PeerId, SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                    ProofSignature = ByteString.CopyFrom(proof.Signature) } },
            ]);
        }
    }

    private sealed class EmptySessionCoordinator : IPeerSessionCoordinator
    {
        public ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerAuthenticationResult> AuthenticateAsync(PeerHello remoteHello, MonitorApp.Contracts.V1.PeerProof proof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(string remotePeerId, OriginRangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerHeartbeat> HeartbeatAsync(string remotePeerId, PeerHeartbeat heartbeat, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public static async Task<TestHost> StartAsync(IPeerPairingCoordinator coordinator)
        {
            var port = FreePort();
            var app = await PeerHostApplication.CreateAsync(["--MonitorApp:Peer:BindAddress=127.0.0.1", $"--MonitorApp:Peer:Port={port}", "--MonitorApp:Peer:AllowLoopback=true"], services =>
            { services.AddSingleton(coordinator); services.AddSingleton<IPeerPairingCoordinator>(coordinator); services.AddSingleton<IPeerSessionCoordinator, EmptySessionCoordinator>(); });
            await app.StartAsync();
            return new TestHost(app, new Uri($"http://127.0.0.1:{port}"));
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
        private static int FreePort()
        { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    }
}
