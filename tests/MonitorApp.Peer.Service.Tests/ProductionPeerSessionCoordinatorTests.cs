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
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Service;
using Xunit;
using ContractPeerProof = MonitorApp.Contracts.V1.PeerProof;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Service.Tests;

public sealed class ProductionPeerSessionCoordinatorTests
{
    [Fact]
    public async Task BilateralReconnectVerifiesServerThenDispatchesSignedReplication()
    {
        await using var fixture = await ProductionFixture.StartAsync();
        using var call = fixture.Client.PeerSession();
        var handshake = await fixture.OpenAndVerifyServerAsync(call, "session-production");
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Proof = fixture.CreateRemoteProof(handshake),
        });

        await fixture.SeedLocalOriginAsync();
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            OriginRangeRequest = new OriginRangeRequest
            {
                RequestId = "range-1",
                OriginPeerId = fixture.LocalIdentity.PeerId,
                OriginStreamId = "stream-local",
                FirstSequence = 1,
                LastSequence = 1,
                MaximumRecordCount = 10,
                MaximumBatchBytes = 65_536,
            },
        });
        Assert.True(await call.ResponseStream.MoveNext());
        var served = Assert.Single(call.ResponseStream.Current.OriginBatch.Records);
        Assert.True(OriginRecordCryptography.Verify(served, fixture.LocalIdentity));

        var record = OriginRecordCryptography.CreateSigned(
            fixture.RemoteKey,
            fixture.RemoteIdentity.PeerId,
            "stream-remote",
            1,
            "entity-1",
            "monitor.system",
            "cpu.utilization",
            ByteString.CopyFromUtf8("42"),
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ObservationQuality.Valid);
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            OriginBatch = new OriginRecordBatch { RequestId = "request-1", Records = { record } },
        });

        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal<ulong>(1, call.ResponseStream.Current.OriginAck.Cursor.AcceptedThroughSequence);
        var stored = await fixture.Store.ReadOriginRangeAsync(
            fixture.RemoteIdentity.PeerId,
            "stream-remote",
            1,
            1);
        Assert.Single(stored);
        Assert.Equal(RecordProvenance.Replicated, stored[0].Provenance);
        Assert.Equal(fixture.RemoteIdentity.PeerId, stored[0].ReceivedFromPeerId);

        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            OriginAck = new OriginAck
            {
                Cursor = new OriginCursor
                {
                    OriginPeerId = fixture.LocalIdentity.PeerId,
                    OriginStreamId = "stream-local",
                    AcceptedThroughSequence = 1,
                    UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
        });
        await call.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
        });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(PeerSessionResponse.MessageOneofCase.Heartbeat, call.ResponseStream.Current.MessageCase);
        var acknowledged = await fixture.Store.ReadReplicationCursorAsync(
            fixture.RemoteIdentity.PeerId,
            fixture.LocalIdentity.PeerId,
            "stream-local");
        Assert.Equal<ulong>(1, acknowledged.Cursor!.AcceptedThrough);

        var tampered = handshake.ServerProof.Clone();
        tampered.ProofSignature = ByteString.CopyFrom(new byte[64]);
        Assert.False(ProductionFixture.VerifyServerProof(handshake with { ServerProof = tampered }));
    }

    [Fact]
    public async Task WrongFingerprintAndRevocationFailBeforeChallenge()
    {
        await using var fixture = await ProductionFixture.StartAsync();
        using (var wrongCall = fixture.Client.PeerSession())
        {
            var hello = fixture.RemoteHello("wrong-fingerprint");
            hello.PublicKeySha256 = ByteString.CopyFrom(new byte[32]);
            await wrongCall.RequestStream.WriteAsync(new PeerSessionRequest { Hello = hello });
            Assert.True(await wrongCall.ResponseStream.MoveNext());
            Assert.Equal(ProtocolErrorCode.InvalidIdentity, wrongCall.ResponseStream.Current.Error.Code);
        }

        await fixture.Store.RevokeTrustedPeerAsync(fixture.RemoteIdentity.PeerId, DateTimeOffset.UtcNow);
        using var revokedCall = fixture.Client.PeerSession();
        await revokedCall.RequestStream.WriteAsync(new PeerSessionRequest
        {
            Hello = fixture.RemoteHello("revoked-session"),
        });
        Assert.True(await revokedCall.ResponseStream.MoveNext());
        Assert.Equal(ProtocolErrorCode.Unauthorized, revokedCall.ResponseStream.Current.Error.Code);
        Assert.DoesNotContain("fingerprint", revokedCall.ResponseStream.Current.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProofCannotBeReplayedIntoASecondDurableChallenge()
    {
        await using var fixture = await ProductionFixture.StartAsync();
        ContractPeerProof firstProof;
        using (var firstCall = fixture.Client.PeerSession())
        {
            var first = await fixture.OpenAndVerifyServerAsync(firstCall, "replay-session");
            firstProof = fixture.CreateRemoteProof(first);
            await firstCall.RequestStream.WriteAsync(new PeerSessionRequest { Proof = firstProof });
            await firstCall.RequestStream.WriteAsync(new PeerSessionRequest
            {
                Heartbeat = new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) },
            });
            Assert.True(await firstCall.ResponseStream.MoveNext());
            Assert.Equal(PeerSessionResponse.MessageOneofCase.Heartbeat, firstCall.ResponseStream.Current.MessageCase);
        }

        using var replayCall = fixture.Client.PeerSession();
        _ = await fixture.OpenAndVerifyServerAsync(replayCall, "replay-session");
        await replayCall.RequestStream.WriteAsync(new PeerSessionRequest { Proof = firstProof });
        Assert.True(await replayCall.ResponseStream.MoveNext());
        Assert.Equal(ProtocolErrorCode.InvalidIdentity, replayCall.ResponseStream.Current.Error.Code);
    }

    private sealed record Handshake(
        PeerHello ServerHello,
        PeerChallenge Challenge,
        ContractPeerProof ServerProof,
        PeerProofChallenge CoreChallenge,
        PeerProofRole ServerRole,
        PeerProofRole RemoteRole);

    private sealed class ProductionFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly WebApplication app;
        private readonly GrpcChannel channel;
        private readonly EcdsaPeerIdentityKey localKey;

        private ProductionFixture(
            string directory,
            WebApplication app,
            GrpcChannel channel,
            PeerStore store,
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
            RemoteKey = remoteKey;
            LocalIdentity = localIdentity;
            RemoteIdentity = remoteIdentity;
            Client = new PeerService.PeerServiceClient(channel);
        }

        public PeerStore Store { get; }

        public EcdsaPeerIdentityKey RemoteKey { get; }

        public PeerPublicIdentity LocalIdentity { get; }

        public PeerPublicIdentity RemoteIdentity { get; }

        public PeerService.PeerServiceClient Client { get; }

        public static async Task<ProductionFixture> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "monitor-peer-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "peer.db");
            var store = new PeerStore(PeerStoreOptions.Create(database));
            var localKey = EcdsaPeerIdentityKey.Create();
            var remoteKey = EcdsaPeerIdentityKey.Create();
            var local = PeerPublicIdentity.Create("peer-a-local", localKey);
            var remote = PeerPublicIdentity.Create("peer-z-remote", remoteKey);
            var identityProvider = new IdentityProvider(local, localKey);
            var pairing = new PeerPairingCoordinator(
                store,
                new PairingApplicationStore(PairingApplicationStoreOptions.Create(database)),
                identityProvider,
                new SystemPeerClock(),
                new CryptographicPeerRandomSource(),
                new PeerPairingCoordinatorOptions(
                    new PeerProtocolRange(
                        1,
                        PeerContractRules.BilateralSessionProofMinor,
                        PeerContractRules.BilateralSessionProofMinor),
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(2)));
            await pairing.InitializeAsync();
            await store.SetLocalIdentityAsync(new LocalPeerIdentity(
                local.PeerId,
                local.PublicKeySpki,
                local.PublicKeyFingerprint,
                "test-key",
                "stream-local",
                1,
                DateTimeOffset.UtcNow));
            await store.UpsertTrustedPeerAsync(new TrustedPeer(
                remote.PeerId,
                remote.PublicKeySpki,
                remote.PublicKeyFingerprint,
                "remote",
                PeerTrustState.Active,
                DateTimeOffset.UtcNow,
                null));
            var coordinator = new ProductionPeerSessionCoordinator(
                pairing,
                store,
                new OriginReplicationCoordinator(store),
                identityProvider,
                new AdvertisementProvider(local.PeerId));
            var port = FreePort();
            var app = await PeerHostApplication.CreateAsync(
            [
                "--MonitorApp:Peer:BindAddress=127.0.0.1",
                $"--MonitorApp:Peer:Port={port}",
                "--MonitorApp:Peer:AllowLoopback=true",
            ], services =>
            {
                services.AddSingleton<IPeerSessionCoordinator>(coordinator);
                services.AddSingleton<IPeerPairingCoordinator, EmptyPairingCoordinator>();
            });
            await app.StartAsync();
            return new ProductionFixture(
                directory,
                app,
                GrpcChannel.ForAddress($"http://127.0.0.1:{port}"),
                store,
                localKey,
                remoteKey,
                local,
                remote);
        }

        public PeerHello RemoteHello(string sessionId) => new()
        {
            ProtocolVersion = new ProtocolVersion
            {
                Major = 1,
                Minor = PeerContractRules.BilateralSessionProofMinor,
            },
            PeerId = RemoteIdentity.PeerId,
            PublicKeySha256 = ByteString.CopyFrom(RemoteIdentity.PublicKeyFingerprint),
            SessionId = sessionId,
        };

        public async Task<Handshake> OpenAndVerifyServerAsync(
            AsyncDuplexStreamingCall<PeerSessionRequest, PeerSessionResponse> call,
            string sessionId)
        {
            await call.RequestStream.WriteAsync(new PeerSessionRequest { Hello = RemoteHello(sessionId) });
            Assert.True(await call.ResponseStream.MoveNext());
            var hello = call.ResponseStream.Current.Hello;
            Assert.Equal(LocalIdentity.PeerId, hello.PeerId);
            Assert.Equal(LocalIdentity.PublicKeyFingerprint, hello.PublicKeySha256.ToByteArray());
            Assert.Single(hello.Capabilities);
            Assert.Single(hello.Cursors);
            Assert.True(await call.ResponseStream.MoveNext());
            var challenge = call.ResponseStream.Current.Challenge;
            Assert.True(await call.ResponseStream.MoveNext());
            var proof = call.ResponseStream.Current.Proof;
            var serverRole = string.CompareOrdinal(LocalIdentity.PeerId, RemoteIdentity.PeerId) < 0
                ? PeerProofRole.Initiator
                : PeerProofRole.Responder;
            var remoteRole = serverRole == PeerProofRole.Initiator
                ? PeerProofRole.Responder
                : PeerProofRole.Initiator;
            var coreChallenge = new PeerProofChallenge(
                sessionId,
                challenge.ChallengeId,
                serverRole == PeerProofRole.Initiator ? LocalIdentity : RemoteIdentity,
                serverRole == PeerProofRole.Responder ? LocalIdentity : RemoteIdentity,
                new PeerProtocolVersion(hello.ProtocolVersion.Major, hello.ProtocolVersion.Minor),
                challenge.ProofChallenge.ToByteArray(),
                challenge.CreatedAt.ToDateTimeOffset(),
                challenge.ExpiresAt.ToDateTimeOffset());
            var handshake = new Handshake(hello, challenge, proof, coreChallenge, serverRole, remoteRole);
            Assert.True(VerifyServerProof(handshake));
            return handshake;
        }

        public static bool VerifyServerProof(Handshake handshake)
        {
            var proof = new CorePeerProof(
                PeerProofPurpose.Reconnect,
                handshake.ServerRole,
                handshake.ServerProof.ProofSignature.ToByteArray());
            return PeerProofService.VerifyAndConsume(
                handshake.CoreChallenge,
                proof,
                DateTimeOffset.UtcNow,
                new InMemoryPeerChallengeReplayGuard()).IsTrusted;
        }

        public ContractPeerProof CreateRemoteProof(Handshake handshake)
        {
            var proof = PeerProofService.CreateProof(
                RemoteKey,
                handshake.CoreChallenge,
                PeerProofPurpose.Reconnect,
                handshake.RemoteRole);
            return new ContractPeerProof
            {
                SessionId = handshake.CoreChallenge.SessionId,
                ChallengeId = handshake.CoreChallenge.ChallengeId,
                SignerPeerId = RemoteIdentity.PeerId,
                VerifierPeerId = LocalIdentity.PeerId,
                SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
                ProofSignature = ByteString.CopyFrom(proof.Signature),
            };
        }

        public async Task SeedLocalOriginAsync()
        {
            var record = OriginRecordCryptography.CreateSigned(
                localKey,
                LocalIdentity.PeerId,
                "stream-local",
                1,
                "entity-local",
                "monitor.system",
                "memory.utilization",
                ByteString.CopyFromUtf8("24"),
                Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ObservationQuality.Valid);
            await Store.AppendOriginRecordAsync(new OriginRecordWrite(
                record.OriginPeerId,
                record.OriginStreamId,
                record.Sequence,
                record.SubjectEntityId,
                record.CapabilityId,
                record.SignalId,
                record.RecordBytes.ToByteArray(),
                record.OriginSignature.ToByteArray(),
                record.ObservedAt.ToDateTimeOffset(),
                DateTimeOffset.UtcNow,
                null,
                RecordProvenance.LocalOrigin,
                record.Quality.ToString()));
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

    private sealed class IdentityProvider(PeerPublicIdentity publicIdentity, IPeerIdentityKey identityKey)
        : ILocalPeerIdentityProvider
    {
        public PeerPublicIdentity PublicIdentity { get; } = publicIdentity;

        public IPeerIdentityKey IdentityKey { get; } = identityKey;
    }

    private sealed class AdvertisementProvider(string localPeerId) : IPeerAdvertisementProvider
    {
        public ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CapabilityDescriptor>>(
            [
                new CapabilityDescriptor
                {
                    ModuleId = "monitor.system",
                    ModuleVersion = "1.0.0",
                    DeclarationSha256 = ByteString.CopyFrom(new byte[32]),
                },
            ]);

        public ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
            string remotePeerId,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<OriginCursor>>(
            [
                new OriginCursor
                {
                    OriginPeerId = localPeerId,
                    OriginStreamId = "stream-local",
                    AcceptedThroughSequence = 1,
                    UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            ]);
    }

    private sealed class EmptyPairingCoordinator : IPeerPairingCoordinator
    {
        public ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(
            PeerPairingSessionContext context,
            PairingFrame frame,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<PairingFrame>>([]);
    }
}
