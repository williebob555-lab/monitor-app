using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Service;
using Xunit;
using ContractPeerProof = MonitorApp.Contracts.V1.PeerProof;

namespace MonitorApp.Peer.Service.Tests;

public sealed class ProductionPeerPairingCoordinatorTests
{
    [Fact]
    public async Task PendingApprovalThenBilateralProofPersistsTrust()
    {
        await using var fixture = await PairingFixture.StartAsync();
        using var call = fixture.Client.Pair();
        var hello = fixture.RemoteHello("pairing-success");
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
        var pendingHello = (await ReadAsync(call)).Hello;
        Assert.Equal(fixture.LocalIdentity.PeerId, pendingHello.PeerId);
        Assert.Equal(PairingApprovalDisposition.AwaitingLocalApproval, (await ReadAsync(call)).Approval.Disposition);
        Assert.Null(await fixture.Store.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId));

        await fixture.Application.ApprovePairingAsync(hello.PairingId);
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
        var localHello = (await ReadAsync(call)).Hello;
        Assert.Equal(PairingApprovalDisposition.Approved, (await ReadAsync(call)).Approval.Disposition);
        var challenge = (await ReadAsync(call)).Challenge;
        var localProof = (await ReadAsync(call)).Proof;
        var transcript = fixture.Transcript(challenge, localHello.ProtocolVersion);
        Assert.True(fixture.VerifyLocalProof(transcript, localProof));

        var remoteProof = fixture.RemoteProof(transcript);
        await call.RequestStream.WriteAsync(new PairingFrame { Proof = remoteProof });
        var confirmation = (await ReadAsync(call)).Confirmation;
        Assert.Equal(hello.PairingId, confirmation.PairingId);
        Assert.Equal(fixture.RemoteIdentity.PublicKeyFingerprint, confirmation.RemotePublicKeySha256.ToByteArray());
        Assert.Equal(PairingApprovalDisposition.Completed, (await ReadAsync(call)).Approval.Disposition);
        var trusted = await fixture.Store.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId);
        Assert.NotNull(trusted);
        Assert.Equal(PeerTrustState.Active, trusted.TrustState);

        await call.RequestStream.WriteAsync(new PairingFrame { Proof = remoteProof });
        Assert.Equal(ProtocolErrorCode.InvalidIdentity, (await ReadAsync(call)).Error.Code);
    }

    [Fact]
    public async Task PairingIdReuseWithChangedIdentityFailsClosed()
    {
        await using var fixture = await PairingFixture.StartAsync();
        using var call = fixture.Client.Pair();
        var hello = fixture.RemoteHello("pairing-reuse");
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
        _ = await ReadAsync(call);
        _ = await ReadAsync(call);

        using var changedKey = EcdsaPeerIdentityKey.Create();
        var changedIdentity = PeerPublicIdentity.Create(fixture.RemoteIdentity.PeerId, changedKey);
        var changed = hello.Clone();
        changed.PublicKeySpki = ByteString.CopyFrom(changedIdentity.PublicKeySpki);
        changed.PublicKeySha256 = ByteString.CopyFrom(changedIdentity.PublicKeyFingerprint);
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = changed });

        var error = (await ReadAsync(call)).Error;
        Assert.Equal(ProtocolErrorCode.InvalidIdentity, error.Code);
        Assert.DoesNotContain(Convert.ToHexString(changedIdentity.PublicKeyFingerprint), error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await fixture.Store.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId));
    }

    [Fact]
    public async Task RemovedIdentityMayAskToPairAgainButWaitsForApproval()
    {
        await using var fixture = await PairingFixture.StartAsync();
        await fixture.Store.UpsertTrustedPeerAsync(new TrustedPeer(
            fixture.RemoteIdentity.PeerId,
            fixture.RemoteIdentity.PublicKeySpki,
            fixture.RemoteIdentity.PublicKeyFingerprint,
            "revoked",
            PeerTrustState.Revoked,
            fixture.Clock.UtcNow.AddMinutes(-1),
            fixture.Clock.UtcNow));
        using var call = fixture.Client.Pair();
        await call.RequestStream.WriteAsync(new PairingFrame
        {
            Hello = fixture.RemoteHello("pairing-revoked"),
        });

        // Like any new device: it waits for the owner's approval and nothing is trusted until then.
        Assert.NotNull((await ReadAsync(call)).Hello);
        Assert.Equal(PairingApprovalDisposition.AwaitingLocalApproval, (await ReadAsync(call)).Approval.Disposition);
        Assert.Equal(PeerTrustState.Revoked, (await fixture.Store.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId))?.TrustState);
    }

    [Fact]
    public async Task RejectionAndExpiryAreStructuredAndDoNotTrust()
    {
        await using var rejected = await PairingFixture.StartAsync();
        using (var call = rejected.Client.Pair())
        {
            var hello = rejected.RemoteHello("pairing-rejected");
            await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
            _ = await ReadAsync(call);
            _ = await ReadAsync(call);
            await rejected.Application.RejectPairingAsync(hello.PairingId);
            await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
            _ = await ReadAsync(call);
            Assert.Equal(PairingApprovalDisposition.Rejected, (await ReadAsync(call)).Approval.Disposition);
            Assert.Null(await rejected.Store.ReadTrustedPeerAsync(rejected.RemoteIdentity.PeerId));
        }

        await using var expired = await PairingFixture.StartAsync();
        using var expiredCall = expired.Client.Pair();
        var expiredHello = expired.RemoteHello("pairing-expired");
        await expiredCall.RequestStream.WriteAsync(new PairingFrame { Hello = expiredHello });
        _ = await ReadAsync(expiredCall);
        var pending = (await ReadAsync(expiredCall)).Approval;
        expired.Clock.UtcNow = pending.ExpiresAt.ToDateTimeOffset().AddTicks(1);
        await expiredCall.RequestStream.WriteAsync(new PairingFrame { Hello = expiredHello });
        _ = await ReadAsync(expiredCall);
        Assert.Equal(PairingApprovalDisposition.Expired, (await ReadAsync(expiredCall)).Approval.Disposition);
        Assert.Null(await expired.Store.ReadTrustedPeerAsync(expired.RemoteIdentity.PeerId));
    }

    [Fact]
    public async Task WrongProofDoesNotConsumeChallengeOrCreateTrust()
    {
        await using var fixture = await PairingFixture.StartAsync();
        using var call = fixture.Client.Pair();
        var hello = fixture.RemoteHello("pairing-proof");
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
        _ = await ReadAsync(call);
        _ = await ReadAsync(call);
        await fixture.Application.ApprovePairingAsync(hello.PairingId);
        await call.RequestStream.WriteAsync(new PairingFrame { Hello = hello });
        var localHello = (await ReadAsync(call)).Hello;
        _ = await ReadAsync(call);
        var challenge = (await ReadAsync(call)).Challenge;
        _ = await ReadAsync(call);
        var transcript = fixture.Transcript(challenge, localHello.ProtocolVersion);
        var wrong = fixture.RemoteProof(transcript);
        wrong.ProofSignature = ByteString.CopyFrom(new byte[64]);
        await call.RequestStream.WriteAsync(new PairingFrame { Proof = wrong });
        Assert.Equal(ProtocolErrorCode.InvalidIdentity, (await ReadAsync(call)).Error.Code);
        Assert.Null(await fixture.Store.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId));

        await call.RequestStream.WriteAsync(new PairingFrame { Proof = fixture.RemoteProof(transcript) });
        Assert.Equal(PairingFrame.MessageOneofCase.Confirmation, (await ReadAsync(call)).MessageCase);
        Assert.Equal(PairingApprovalDisposition.Completed, (await ReadAsync(call)).Approval.Disposition);
    }

    private static async Task<PairingFrame> ReadAsync(
        AsyncDuplexStreamingCall<PairingFrame, PairingFrame> call)
    {
        Assert.True(await call.ResponseStream.MoveNext());
        return call.ResponseStream.Current;
    }

    private sealed class PairingFixture : IAsyncDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 21, 14, 0, 0, TimeSpan.Zero);
        private readonly string directory;
        private readonly WebApplication app;
        private readonly GrpcChannel channel;
        private readonly EcdsaPeerIdentityKey localKey;

        private PairingFixture(
            string directory,
            WebApplication app,
            GrpcChannel channel,
            PeerStore store,
            PeerPairingCoordinator application,
            MutableClock clock,
            EcdsaPeerIdentityKey localKey,
            EcdsaPeerIdentityKey remoteKey,
            PeerPublicIdentity localIdentity,
            PeerPublicIdentity remoteIdentity)
        {
            this.directory = directory;
            this.app = app;
            this.channel = channel;
            this.localKey = localKey;
            Store = store;
            Application = application;
            Clock = clock;
            RemoteKey = remoteKey;
            LocalIdentity = localIdentity;
            RemoteIdentity = remoteIdentity;
            Client = new PeerPairingService.PeerPairingServiceClient(channel);
        }

        public PeerStore Store { get; }
        public PeerPairingCoordinator Application { get; }
        public MutableClock Clock { get; }
        public EcdsaPeerIdentityKey RemoteKey { get; }
        public PeerPublicIdentity LocalIdentity { get; }
        public PeerPublicIdentity RemoteIdentity { get; }
        public PeerPairingService.PeerPairingServiceClient Client { get; }

        public static async Task<PairingFixture> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "monitor-pairing-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "peer.db");
            var store = new PeerStore(PeerStoreOptions.Create(database));
            var localKey = EcdsaPeerIdentityKey.Create();
            var remoteKey = EcdsaPeerIdentityKey.Create();
            var local = PeerPublicIdentity.Create("peer-z-local", localKey);
            var remote = PeerPublicIdentity.Create("peer-a-remote", remoteKey);
            var identity = new IdentityProvider(local, localKey);
            var clock = new MutableClock { UtcNow = Start };
            var application = new PeerPairingCoordinator(
                store,
                new PairingApplicationStore(PairingApplicationStoreOptions.Create(database)),
                identity,
                clock,
                new CryptographicPeerRandomSource(),
                new PeerPairingCoordinatorOptions(
                    new PeerProtocolRange(1, 1, 1),
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(2)));
            await application.InitializeAsync();
            var coordinator = new ProductionPeerPairingCoordinator(
                application,
                identity,
                new PeerPairingServiceOptions("Local peer"),
                clock);
            var port = FreePort();
            var app = await PeerHostApplication.CreateAsync(
            [
                "--MonitorApp:Peer:BindAddress=127.0.0.1",
                $"--MonitorApp:Peer:Port={port}",
                "--MonitorApp:Peer:AllowLoopback=true",
            ], services =>
            {
                services.AddSingleton<IPeerPairingCoordinator>(coordinator);
                services.AddSingleton<IPeerSessionCoordinator, UnusedSessionCoordinator>();
            });
            await app.StartAsync();
            return new PairingFixture(
                directory,
                app,
                GrpcChannel.ForAddress($"http://127.0.0.1:{port}"),
                store,
                application,
                clock,
                localKey,
                remoteKey,
                local,
                remote);
        }

        public PairingHello RemoteHello(string pairingId) => new()
        {
            ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 1 },
            PairingId = pairingId,
            PeerId = RemoteIdentity.PeerId,
            DisplayName = "Remote peer",
            PublicKeySpki = ByteString.CopyFrom(RemoteIdentity.PublicKeySpki),
            PublicKeySha256 = ByteString.CopyFrom(RemoteIdentity.PublicKeyFingerprint),
        };

        public PeerProofChallenge Transcript(PeerChallenge challenge, ProtocolVersion version) => new(
            challenge.SessionId,
            challenge.ChallengeId,
            RemoteIdentity,
            LocalIdentity,
            new PeerProtocolVersion(version.Major, version.Minor),
            challenge.ProofChallenge.ToByteArray(),
            challenge.CreatedAt.ToDateTimeOffset(),
            challenge.ExpiresAt.ToDateTimeOffset());

        public bool VerifyLocalProof(PeerProofChallenge challenge, ContractPeerProof proof) =>
            PeerProofService.VerifyAndConsume(
                challenge,
                new MonitorApp.Peer.Core.PeerProof(
                    PeerProofPurpose.Pairing,
                    PeerProofRole.Responder,
                    proof.ProofSignature.ToByteArray()),
                Clock.UtcNow,
                new InMemoryPeerChallengeReplayGuard()).IsTrusted;

        public ContractPeerProof RemoteProof(PeerProofChallenge challenge)
        {
            var proof = PeerProofService.CreateProof(
                RemoteKey,
                challenge,
                PeerProofPurpose.Pairing,
                PeerProofRole.Initiator);
            return new ContractPeerProof
            {
                SessionId = challenge.SessionId,
                ChallengeId = challenge.ChallengeId,
                SignerPeerId = RemoteIdentity.PeerId,
                VerifierPeerId = LocalIdentity.PeerId,
                SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                ProofSignature = ByteString.CopyFrom(proof.Signature),
            };
        }

        public async ValueTask DisposeAsync()
        {
            channel.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            RemoteKey.Dispose();
            localKey.Dispose();
            Directory.Delete(directory, recursive: true);
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    public sealed class MutableClock : IPeerClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class IdentityProvider(PeerPublicIdentity publicIdentity, IPeerIdentityKey identityKey)
        : ILocalPeerIdentityProvider
    {
        public PeerPublicIdentity PublicIdentity { get; } = publicIdentity;
        public IPeerIdentityKey IdentityKey { get; } = identityKey;
    }

    private sealed class UnusedSessionCoordinator : IPeerSessionCoordinator
    {
        public ValueTask<PeerSessionOpenResult> OpenAsync(PeerHello remoteHello, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerAuthenticationResult> AuthenticateAsync(PeerHello remoteHello, ContractPeerProof proof, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(string remotePeerId, CapabilityExchange remoteCapabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginRecordBatch> ReadOriginRangeAsync(string remotePeerId, OriginRangeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OriginAck> AcceptOriginBatchAsync(string remotePeerId, OriginRecordBatch batch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AcceptOriginAckAsync(string remotePeerId, OriginAck acknowledgement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PeerHeartbeat> HeartbeatAsync(string remotePeerId, PeerHeartbeat heartbeat, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
