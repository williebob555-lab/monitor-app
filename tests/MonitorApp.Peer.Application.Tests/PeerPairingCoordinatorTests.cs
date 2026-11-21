using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

public sealed class PeerPairingCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-peer-app-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExplicitPairingIdIsIdempotentBoundAndUsesOneDurableChallenge()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var first = await fixture.Coordinator.BeginPairingAsync("pairing-fixed", fixture.Candidate());
        var retry = await fixture.Coordinator.BeginPairingAsync("pairing-fixed", fixture.Candidate());

        Assert.Equal(PairingOperationStatus.ApprovalRequired, first.Status);
        Assert.Equal(PairingOperationStatus.ApprovalRequired, retry.Status);
        Assert.Equal(first.ExpiresAt, retry.ExpiresAt);
        await fixture.Coordinator.ApprovePairingAsync("pairing-fixed");
        var challenge = await fixture.Coordinator.IssuePairingChallengeOnceAsync("pairing-fixed", "pairing:pairing-fixed");
        var challengeRetry = await fixture.Coordinator.IssuePairingChallengeOnceAsync("pairing-fixed", "ignored-retry-session");
        Assert.Equal(challenge.ChallengeId, challengeRetry.ChallengeId);
        Assert.Equal(challenge.Challenge.Challenge, challengeRetry.Challenge.Challenge);

        using var changedKey = EcdsaPeerIdentityKey.Create();
        var changed = fixture.Candidate() with
        {
            Identity = PeerPublicIdentity.Create(fixture.RemoteIdentity.PeerId, changedKey),
        };
        var mismatch = await fixture.Coordinator.BeginPairingAsync("pairing-fixed", changed);
        Assert.Equal(PairingOperationStatus.ReusedWithDifferentParameters, mismatch.Status);
    }

    [Fact]
    public async Task PairingRequiresExplicitApprovalBeforeChallengeExchange()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var started = await fixture.Coordinator.BeginPairingAsync(fixture.Candidate());

        Assert.Equal(PairingOperationStatus.ApprovalRequired, started.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.IssuePairingChallengeAsync(started.PairingId!, "session-1"));
    }

    [Fact]
    public async Task BilateralProofPersistsTrustedPeerAndNegotiatedCapabilities()
    {
        await using var fixture = await Fixture.CreateAsync(directory);

        var result = await fixture.PairAsync("session-success");

        Assert.Equal(PairingOperationStatus.Completed, result.Status);
        Assert.Equal(fixture.RemoteIdentity.PeerId, result.RemotePeerId);
        Assert.Equal(new PeerProtocolVersion(1, 2), result.SelectedVersion);
        Assert.Single(result.RemoteCapabilities);
        var trusted = await fixture.PeerStore.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId);
        Assert.NotNull(trusted);
        Assert.Equal(PeerTrustState.Active, trusted.TrustState);
        Assert.Equal(fixture.RemoteIdentity.PublicKeyFingerprint, trusted.PublicKeyFingerprint);
    }

    [Fact]
    public async Task PairingRejectsWrongRoleTranscriptAndKey()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var issued = await fixture.ApproveAndIssueAsync("session-role");
        await fixture.Coordinator.CreateLocalProofAsync(issued.ChallengeId);
        var valid = fixture.RemoteProof(issued);
        var wrongRole = valid with { SignerRole = issued.LocalRole };

        var wrongRoleResult = await fixture.Coordinator.CompletePairingAsync(
            fixture.PairingId!, issued.ChallengeId, wrongRole);

        Assert.Equal(PairingOperationStatus.InvalidProof, wrongRoleResult.Status);

        var transcriptIssued = await fixture.Coordinator.IssuePairingChallengeAsync(
            fixture.PairingId!, "session-transcript");
        await fixture.Coordinator.CreateLocalProofAsync(transcriptIssued.ChallengeId);
        var transcriptResult = await fixture.Coordinator.CompletePairingAsync(
            fixture.PairingId!, transcriptIssued.ChallengeId, valid);
        Assert.Equal(PairingOperationStatus.InvalidProof, transcriptResult.Status);

        var keyIssued = await fixture.Coordinator.IssuePairingChallengeAsync(
            fixture.PairingId!, "session-key");
        await fixture.Coordinator.CreateLocalProofAsync(keyIssued.ChallengeId);
        using var otherKey = EcdsaPeerIdentityKey.Create();
        var forgedChallenge = keyIssued.Challenge with
        {
            Responder = PeerPublicIdentity.Create(fixture.RemoteIdentity.PeerId, otherKey),
        };
        var wrongKeyProof = PeerProofService.CreateProof(
            otherKey,
            forgedChallenge,
            PeerProofPurpose.Pairing,
            Opposite(keyIssued.LocalRole));
        var wrongKeyResult = await fixture.Coordinator.CompletePairingAsync(
            fixture.PairingId!, keyIssued.ChallengeId, wrongKeyProof);
        Assert.Equal(PairingOperationStatus.InvalidProof, wrongKeyResult.Status);
    }

    [Fact]
    public async Task ExpiredChallengeIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var issued = await fixture.ApproveAndIssueAsync("session-expiry");
        await fixture.Coordinator.CreateLocalProofAsync(issued.ChallengeId);
        var proof = fixture.RemoteProof(issued);
        fixture.Clock.UtcNow = issued.Challenge.ExpiresAt.AddTicks(1);

        var result = await fixture.Coordinator.CompletePairingAsync(
            fixture.PairingId!, issued.ChallengeId, proof);

        Assert.Equal(PairingOperationStatus.Expired, result.Status);
    }

    [Fact]
    public async Task ExactReconnectProofReplayIsRejectedAcrossCoordinatorRestart()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        await fixture.PairAsync("session-pair");
        var issued = await fixture.BeginReconnectAsync("session-reconnect");
        await fixture.Coordinator.CreateLocalProofAsync(issued.ChallengeId);
        var proof = fixture.RemoteProof(issued);

        var first = await fixture.Coordinator.AuthenticateReconnectAsync(issued.ChallengeId, proof);
        var restarted = fixture.CreateCoordinator();
        await restarted.InitializeAsync();
        var replay = await restarted.AuthenticateReconnectAsync(issued.ChallengeId, proof);

        Assert.Equal(PairingOperationStatus.Accepted, first.Status);
        Assert.Equal(PairingOperationStatus.Replayed, replay.Status);
    }

    [Fact]
    public async Task ApprovedPairingCompletesAfterRestartUsingDurableChallengeState()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var issued = await fixture.ApproveAndIssueAsync("session-restart");
        await fixture.Coordinator.CreateLocalProofAsync(issued.ChallengeId);
        var proof = fixture.RemoteProof(issued);
        var restarted = fixture.CreateCoordinator();
        await restarted.InitializeAsync();

        var result = await restarted.CompletePairingAsync(
            fixture.PairingId!, issued.ChallengeId, proof);

        Assert.Equal(PairingOperationStatus.Completed, result.Status);
        Assert.NotNull(await fixture.PeerStore.ReadTrustedPeerAsync(fixture.RemoteIdentity.PeerId));
    }

    [Fact]
    public async Task RevokedPeerCannotReconnect()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        await fixture.PairAsync("session-pair");
        await fixture.PeerStore.RevokeTrustedPeerAsync(
            fixture.RemoteIdentity.PeerId,
            fixture.Clock.UtcNow);

        var reconnect = await fixture.Coordinator.BeginReconnectAsync(
            fixture.RemoteIdentity,
            "session-revoked",
            fixture.Protocol,
            fixture.Capabilities);

        Assert.Equal(PairingOperationStatus.Revoked, reconnect.Status);
        Assert.Null(reconnect.Challenge);
    }

    [Fact]
    public async Task IncompatibleMajorVersionIsRejectedBeforeApprovalStateIsCreated()
    {
        await using var fixture = await Fixture.CreateAsync(directory);
        var candidate = fixture.Candidate() with
        {
            SupportedProtocol = new PeerProtocolRange(2, 0, 0),
        };

        var result = await fixture.Coordinator.BeginPairingAsync(candidate);

        Assert.Equal(PairingOperationStatus.Incompatible, result.Status);
        Assert.Null(result.PairingId);
    }

    [Fact]
    public void SimultaneousConnectionsSelectSameWinnerRegardlessOfObservationOrder()
    {
        var outbound = new PeerSessionCandidate("session-z", "peer-a", "peer-b");
        var inbound = new PeerSessionCandidate("session-a", "peer-b", "peer-a");

        var first = PeerPairingCoordinator.SelectDuplicateSession(outbound, inbound);
        var second = PeerPairingCoordinator.SelectDuplicateSession(inbound, outbound);

        Assert.Equal(outbound, first);
        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static PeerProofRole Opposite(PeerProofRole role) =>
        role == PeerProofRole.Initiator ? PeerProofRole.Responder : PeerProofRole.Initiator;

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string databasePath,
            MutableClock clock,
            DeterministicRandom random,
            IdentityProvider local,
            EcdsaPeerIdentityKey remoteKey,
            PeerStore peerStore,
            PairingApplicationStore applicationStore,
            PeerPairingCoordinator coordinator)
        {
            DatabasePath = databasePath;
            Clock = clock;
            Random = random;
            Local = local;
            RemoteKey = remoteKey;
            RemoteIdentity = PeerPublicIdentity.Create("peer-remote", remoteKey);
            PeerStore = peerStore;
            ApplicationStore = applicationStore;
            Coordinator = coordinator;
        }

        public string DatabasePath { get; }
        public MutableClock Clock { get; }
        public DeterministicRandom Random { get; }
        public IdentityProvider Local { get; }
        public EcdsaPeerIdentityKey RemoteKey { get; }
        public PeerPublicIdentity RemoteIdentity { get; }
        public PeerStore PeerStore { get; }
        public PairingApplicationStore ApplicationStore { get; }
        public PeerPairingCoordinator Coordinator { get; }
        public string? PairingId { get; private set; }
        public PeerProtocolRange Protocol { get; } = new(1, 0, 2);
        public IReadOnlyList<PeerCapability> Capabilities { get; } =
            [new("telemetry", "1.0.0", Enumerable.Repeat((byte)0x5a, 32).ToArray())];

        public static async Task<Fixture> CreateAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "peer.db");
            var clock = new MutableClock { UtcNow = StartTime };
            var random = new DeterministicRandom();
            var local = new IdentityProvider("peer-local");
            var remoteKey = EcdsaPeerIdentityKey.Create();
            var peerStore = new PeerStore(PeerStoreOptions.Create(databasePath));
            var appStore = new PairingApplicationStore(PairingApplicationStoreOptions.Create(databasePath));
            var coordinator = CreateCoordinator(peerStore, appStore, local, clock, random);
            await coordinator.InitializeAsync();
            return new Fixture(
                databasePath, clock, random, local, remoteKey, peerStore, appStore, coordinator);
        }

        public PairingCandidate Candidate() => new(
            RemoteIdentity,
            "Remote peer",
            PeerProofRole.Initiator,
            Protocol,
            Capabilities);

        public async Task<IssuedPeerChallenge> ApproveAndIssueAsync(string sessionId)
        {
            var started = await Coordinator.BeginPairingAsync(Candidate());
            PairingId = started.PairingId;
            await Coordinator.ApprovePairingAsync(PairingId!);
            return await Coordinator.IssuePairingChallengeAsync(PairingId!, sessionId);
        }

        public async Task<SessionAdmissionResult> PairAsync(string sessionId)
        {
            var issued = await ApproveAndIssueAsync(sessionId);
            await Coordinator.CreateLocalProofAsync(issued.ChallengeId);
            return await Coordinator.CompletePairingAsync(
                PairingId!, issued.ChallengeId, RemoteProof(issued));
        }

        public async Task<IssuedPeerChallenge> BeginReconnectAsync(string sessionId)
        {
            var result = await Coordinator.BeginReconnectAsync(
                RemoteIdentity, sessionId, Protocol, Capabilities);
            Assert.Equal(PairingOperationStatus.Accepted, result.Status);
            return Assert.IsType<IssuedPeerChallenge>(result.Challenge);
        }

        public PeerProof RemoteProof(IssuedPeerChallenge issued) =>
            PeerProofService.CreateProof(
                RemoteKey,
                issued.Challenge,
                issued.Purpose,
                Opposite(issued.LocalRole));

        public PeerPairingCoordinator CreateCoordinator() => CreateCoordinator(
            new PeerStore(PeerStoreOptions.Create(DatabasePath)),
            new PairingApplicationStore(PairingApplicationStoreOptions.Create(DatabasePath)),
            Local,
            Clock,
            Random);

        public ValueTask DisposeAsync()
        {
            RemoteKey.Dispose();
            Local.Dispose();
            return ValueTask.CompletedTask;
        }

        private static PeerPairingCoordinator CreateCoordinator(
            PeerStore peerStore,
            PairingApplicationStore applicationStore,
            IdentityProvider local,
            MutableClock clock,
            DeterministicRandom random) => new(
                peerStore,
                applicationStore,
                local,
                clock,
                random,
                new PeerPairingCoordinatorOptions(
                    new PeerProtocolRange(1, 0, 2),
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(2)));
    }

    private sealed class MutableClock : IPeerClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class DeterministicRandom : IPeerRandomSource
    {
        private int nextId;
        private byte nextByte;

        public string CreateId() => $"generated-{Interlocked.Increment(ref nextId)}";

        public byte[] GetBytes(int count)
        {
            var value = unchecked(++nextByte);
            return Enumerable.Repeat(value, count).ToArray();
        }
    }

    private sealed class IdentityProvider : ILocalPeerIdentityProvider, IDisposable
    {
        public IdentityProvider(string peerId)
        {
            IdentityKey = EcdsaPeerIdentityKey.Create();
            PublicIdentity = PeerPublicIdentity.Create(peerId, IdentityKey);
        }

        public PeerPublicIdentity PublicIdentity { get; }
        public IPeerIdentityKey IdentityKey { get; }

        public void Dispose() => IdentityKey.Dispose();
    }
}
