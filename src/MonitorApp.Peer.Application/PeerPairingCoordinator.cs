using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Application;

public sealed class PeerPairingCoordinator
{
    private readonly PeerStore peerStore;
    private readonly PairingApplicationStore applicationStore;
    private readonly ILocalPeerIdentityProvider localIdentity;
    private readonly IPeerClock clock;
    private readonly IPeerRandomSource random;
    private readonly PeerPairingCoordinatorOptions options;
    private readonly IPeerChallengeReplayGuard replayGuard;

    public PeerPairingCoordinator(
        PeerStore peerStore,
        PairingApplicationStore applicationStore,
        ILocalPeerIdentityProvider localIdentity,
        IPeerClock clock,
        IPeerRandomSource random,
        PeerPairingCoordinatorOptions options)
    {
        this.peerStore = peerStore ?? throw new ArgumentNullException(nameof(peerStore));
        this.applicationStore = applicationStore ?? throw new ArgumentNullException(nameof(applicationStore));
        this.localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.random = random ?? throw new ArgumentNullException(nameof(random));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        replayGuard = new SqlitePeerChallengeReplayGuard(applicationStore);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await peerStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await applicationStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PairingStartResult> BeginPairingAsync(
        PairingCandidate candidate,
        CancellationToken cancellationToken = default) =>
        await BeginPairingAsync(random.CreateId(), candidate, cancellationToken).ConfigureAwait(false);

    public async Task<PairingStartResult> BeginPairingAsync(
        string pairingId,
        PairingCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateRole(candidate.LocalRole);
        ValidateCapabilities(candidate.Capabilities);
        if (string.Equals(
                candidate.Identity.PeerId,
                localIdentity.PublicIdentity.PeerId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Cannot pair a peer with itself.", nameof(candidate));
        }

        var compatibility = PeerProtocolCompatibility.Negotiate(
            options.SupportedProtocol,
            candidate.SupportedProtocol);
        if (!compatibility.IsCompatible)
        {
            return new PairingStartResult(
                PairingOperationStatus.Incompatible,
                null,
                null,
                candidate.Capabilities,
                compatibility.Reason);
        }

        var now = UtcNow();
        var trusted = await peerStore.ReadTrustedPeerAsync(candidate.Identity.PeerId, cancellationToken)
            .ConfigureAwait(false);
        if (trusted?.TrustState == PeerTrustState.Revoked)
        {
            return new PairingStartResult(
                PairingOperationStatus.Revoked,
                pairingId,
                compatibility.SelectedVersion,
                candidate.Capabilities,
                "Peer trust has been revoked.");
        }

        var existing = await applicationStore.ReadPairingAsync(pairingId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!Matches(existing, candidate, compatibility.SelectedVersion!.Value))
            {
                return new PairingStartResult(
                    PairingOperationStatus.ReusedWithDifferentParameters,
                    pairingId,
                    existing.SelectedVersion,
                    [],
                    "Pairing id is already bound to different parameters.");
            }

            return Existing(existing, now);
        }

        var snapshot = new PairingSnapshot(
            pairingId,
            candidate.Identity,
            candidate.DisplayName,
            candidate.LocalRole,
            PairingApprovalState.PendingApproval,
            compatibility.SelectedVersion!.Value,
            candidate.Capabilities.ToArray(),
            now,
            now.Add(options.PairingApprovalLifetime));
        if (!await applicationStore.TryCreatePairingAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            var raced = await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
            return Matches(raced, candidate, compatibility.SelectedVersion!.Value)
                ? Existing(raced, now)
                : new PairingStartResult(
                    PairingOperationStatus.ReusedWithDifferentParameters,
                    pairingId,
                    raced.SelectedVersion,
                    [],
                    "Pairing id is already bound to different parameters.",
                    raced.ExpiresAt);
        }

        await peerStore.CreatePairingSessionAsync(
            new PairingSession(pairingId, candidate.Identity.PeerId, now, snapshot.ExpiresAt, false),
            cancellationToken).ConfigureAwait(false);
        return new PairingStartResult(
            PairingOperationStatus.ApprovalRequired,
            pairingId,
            snapshot.SelectedVersion,
            snapshot.RemoteCapabilities,
            "Local approval is required before proof exchange.",
            snapshot.ExpiresAt);
    }

    public async Task<PairingSnapshot> ApprovePairingAsync(
        string pairingId,
        CancellationToken cancellationToken = default)
    {
        var pairing = await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
        EnsureNotExpired(pairing);
        if (pairing.State != PairingApprovalState.PendingApproval ||
            !await applicationStore.TransitionPairingAsync(
                pairingId,
                PairingApprovalState.PendingApproval,
                PairingApprovalState.Approved,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Pairing is not pending approval.");
        }

        return (await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false)) with
        {
            State = PairingApprovalState.Approved,
        };
    }

    public async Task RejectPairingAsync(
        string pairingId,
        CancellationToken cancellationToken = default)
    {
        if (!await applicationStore.TransitionPairingAsync(
                pairingId,
                PairingApprovalState.PendingApproval,
                PairingApprovalState.Rejected,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Pairing is not pending approval.");
        }
    }

    public async Task<IssuedPeerChallenge> IssuePairingChallengeAsync(
        string pairingId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var pairing = await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
        EnsureApproved(pairing);
        var challenge = CreateChallenge(
            sessionId,
            pairing.RemoteIdentity,
            pairing.LocalRole,
            pairing.SelectedVersion);
        await applicationStore.CreateChallengeAsync(
            ToStoredChallenge(challenge, pairingId, pairing.RemoteCapabilities),
            cancellationToken).ConfigureAwait(false);
        return new IssuedPeerChallenge(
            challenge.ChallengeId,
            challenge,
            PeerProofPurpose.Pairing,
            pairing.LocalRole,
            pairing.RemoteCapabilities);
    }

    public async Task<IssuedPeerChallenge> IssuePairingChallengeOnceAsync(
        string pairingId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var pairing = await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
        EnsureApproved(pairing);
        var existing = await applicationStore.ReadPairingChallengeAsync(pairingId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await IssuedAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        var challenge = CreateChallenge(
            sessionId,
            pairing.RemoteIdentity,
            pairing.LocalRole,
            pairing.SelectedVersion);
        var stored = ToStoredChallenge(challenge, pairingId, pairing.RemoteCapabilities);
        if (await applicationStore.TryCreatePairingChallengeAsync(stored, cancellationToken).ConfigureAwait(false))
        {
            return new IssuedPeerChallenge(
                challenge.ChallengeId,
                challenge,
                PeerProofPurpose.Pairing,
                pairing.LocalRole,
                pairing.RemoteCapabilities);
        }

        existing = await applicationStore.ReadPairingChallengeAsync(pairingId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Pairing challenge was not retained.");
        return await IssuedAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IssuedPeerChallenge> IssuedAsync(
        StoredChallenge stored,
        CancellationToken cancellationToken)
    {
        var challenge = await RehydrateChallengeAsync(stored, cancellationToken).ConfigureAwait(false);
        return new IssuedPeerChallenge(
            stored.ChallengeId,
            challenge,
            stored.Purpose,
            stored.LocalRole,
            stored.RemoteCapabilities);
    }

    public async Task<PeerProof> CreateLocalProofAsync(
        string challengeId,
        CancellationToken cancellationToken = default)
    {
        var stored = await RequireChallengeAsync(challengeId, cancellationToken).ConfigureAwait(false);
        var challenge = await RehydrateChallengeAsync(stored, cancellationToken).ConfigureAwait(false);
        if (UtcNow() > stored.ExpiresAt)
        {
            throw new InvalidOperationException("Peer challenge has expired.");
        }

        if (stored.PairingId is not null)
        {
            EnsureApproved(await RequirePairingAsync(stored.PairingId, cancellationToken).ConfigureAwait(false));
        }

        var proof = PeerProofService.CreateProof(
            localIdentity.IdentityKey,
            challenge,
            stored.Purpose,
            stored.LocalRole);
        await applicationStore.MarkLocalProofCreatedAsync(challengeId, cancellationToken).ConfigureAwait(false);
        return proof;
    }

    public async Task<SessionAdmissionResult> CompletePairingAsync(
        string pairingId,
        string challengeId,
        PeerProof remoteProof,
        CancellationToken cancellationToken = default)
    {
        var pairing = await RequirePairingAsync(pairingId, cancellationToken).ConfigureAwait(false);
        EnsureApproved(pairing);
        var stored = await RequireChallengeAsync(challengeId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored.PairingId, pairingId, StringComparison.Ordinal) ||
            stored.Purpose != PeerProofPurpose.Pairing)
        {
            return Failure(PairingOperationStatus.InvalidProof, "Challenge does not belong to this pairing.");
        }

        var verification = await VerifyRemoteProofAsync(stored, remoteProof, cancellationToken).ConfigureAwait(false);
        if (!verification.IsAuthenticated)
        {
            return verification;
        }

        var now = UtcNow();
        await peerStore.UpsertTrustedPeerAsync(
            new TrustedPeer(
                pairing.RemoteIdentity.PeerId,
                pairing.RemoteIdentity.PublicKeySpki,
                pairing.RemoteIdentity.PublicKeyFingerprint,
                pairing.DisplayName,
                PeerTrustState.Active,
                now,
                null),
            cancellationToken).ConfigureAwait(false);
        await peerStore.CompletePairingSessionAsync(pairingId, now, cancellationToken).ConfigureAwait(false);
        if (!await applicationStore.TransitionPairingAsync(
                pairingId,
                PairingApprovalState.Approved,
                PairingApprovalState.Completed,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Pairing state changed before completion.");
        }

        return verification with { Status = PairingOperationStatus.Completed };
    }

    private static PairingStartResult Existing(PairingSnapshot pairing, DateTimeOffset now)
    {
        var status = pairing.State switch
        {
            PairingApprovalState.Completed => PairingOperationStatus.Completed,
            PairingApprovalState.Rejected => PairingOperationStatus.Rejected,
            PairingApprovalState.Expired => PairingOperationStatus.Expired,
            _ when now > pairing.ExpiresAt => PairingOperationStatus.Expired,
            _ => pairing.State switch
            {
                PairingApprovalState.PendingApproval => PairingOperationStatus.ApprovalRequired,
                PairingApprovalState.Approved => PairingOperationStatus.Accepted,
                _ => PairingOperationStatus.Rejected,
            },
        };
        return new PairingStartResult(
            status,
            pairing.PairingId,
            pairing.SelectedVersion,
            pairing.RemoteCapabilities,
            status switch
            {
                PairingOperationStatus.ApprovalRequired => "Local approval is required before proof exchange.",
                PairingOperationStatus.Accepted => "Local approval has been granted.",
                PairingOperationStatus.Completed => "Pairing is complete.",
                PairingOperationStatus.Expired => "Pairing approval has expired.",
                _ => "Pairing was rejected.",
            },
            pairing.ExpiresAt);
    }

    private static bool Matches(
        PairingSnapshot pairing,
        PairingCandidate candidate,
        PeerProtocolVersion selectedVersion) =>
        pairing.RemoteIdentity.PeerId == candidate.Identity.PeerId &&
        pairing.RemoteIdentity.PublicKeySpki.AsSpan().SequenceEqual(candidate.Identity.PublicKeySpki) &&
        pairing.RemoteIdentity.PublicKeyFingerprint.AsSpan().SequenceEqual(candidate.Identity.PublicKeyFingerprint) &&
        pairing.DisplayName == candidate.DisplayName &&
        pairing.LocalRole == candidate.LocalRole &&
        pairing.SelectedVersion == selectedVersion &&
        pairing.RemoteCapabilities.Count == candidate.Capabilities.Count &&
        pairing.RemoteCapabilities.Zip(candidate.Capabilities).All(pair =>
            pair.First.ModuleId == pair.Second.ModuleId &&
            pair.First.ModuleVersion == pair.Second.ModuleVersion &&
            pair.First.DeclarationSha256.AsSpan().SequenceEqual(pair.Second.DeclarationSha256));

    public async Task<(PairingOperationStatus Status, IssuedPeerChallenge? Challenge, string Reason)> BeginReconnectAsync(
        PeerPublicIdentity presentedRemoteIdentity,
        string sessionId,
        PeerProtocolRange remoteProtocol,
        IReadOnlyList<PeerCapability> remoteCapabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentedRemoteIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateCapabilities(remoteCapabilities);
        var trusted = await peerStore.ReadTrustedPeerAsync(
            presentedRemoteIdentity.PeerId,
            cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            return (PairingOperationStatus.Untrusted, null, "Peer is not paired.");
        }

        if (trusted.TrustState == PeerTrustState.Revoked)
        {
            return (PairingOperationStatus.Revoked, null, "Peer trust has been revoked.");
        }

        if (!trusted.PublicKeyFingerprint.AsSpan().SequenceEqual(
                presentedRemoteIdentity.PublicKeyFingerprint))
        {
            return (PairingOperationStatus.Untrusted, null, "Presented peer key does not match pinned trust.");
        }

        var compatibility = PeerProtocolCompatibility.Negotiate(options.SupportedProtocol, remoteProtocol);
        if (!compatibility.IsCompatible)
        {
            return (PairingOperationStatus.Incompatible, null, compatibility.Reason);
        }

        var localRole = string.CompareOrdinal(
                localIdentity.PublicIdentity.PeerId,
                presentedRemoteIdentity.PeerId) < 0
            ? PeerProofRole.Initiator
            : PeerProofRole.Responder;
        var challenge = CreateChallenge(
            sessionId,
            presentedRemoteIdentity,
            localRole,
            compatibility.SelectedVersion!.Value);
        var stored = ToStoredChallenge(challenge, null, remoteCapabilities);
        await applicationStore.CreateChallengeAsync(stored, cancellationToken).ConfigureAwait(false);
        return (
            PairingOperationStatus.Accepted,
            new IssuedPeerChallenge(
                challenge.ChallengeId,
                challenge,
                PeerProofPurpose.Reconnect,
                localRole,
                remoteCapabilities),
            string.Empty);
    }

    public async Task<SessionAdmissionResult> AuthenticateReconnectAsync(
        string challengeId,
        PeerProof remoteProof,
        CancellationToken cancellationToken = default)
    {
        var stored = await RequireChallengeAsync(challengeId, cancellationToken).ConfigureAwait(false);
        if (stored.Purpose != PeerProofPurpose.Reconnect)
        {
            return Failure(PairingOperationStatus.InvalidProof, "Challenge is not a reconnect challenge.");
        }

        var trusted = await peerStore.ReadTrustedPeerAsync(stored.RemotePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            return Failure(PairingOperationStatus.Untrusted, "Peer is not paired.");
        }

        if (trusted.TrustState == PeerTrustState.Revoked)
        {
            return Failure(PairingOperationStatus.Revoked, "Peer trust has been revoked.");
        }

        return await VerifyRemoteProofAsync(stored, remoteProof, cancellationToken).ConfigureAwait(false);
    }

    public static PeerSessionCandidate SelectDuplicateSession(
        PeerSessionCandidate first,
        PeerSessionCandidate second) => DuplicateSessionSelector.Select(first, second);

    private PeerProofChallenge CreateChallenge(
        string sessionId,
        PeerPublicIdentity remote,
        PeerProofRole localRole,
        PeerProtocolVersion version)
    {
        var local = localIdentity.PublicIdentity;
        var initiator = localRole == PeerProofRole.Initiator ? local : remote;
        var responder = localRole == PeerProofRole.Responder ? local : remote;
        return PeerProofService.CreateChallenge(
            sessionId,
            random.CreateId(),
            initiator,
            responder,
            version,
            UtcNow(),
            options.ChallengeLifetime,
            random.GetBytes(PeerProofRules.ChallengeSize));
    }

    private StoredChallenge ToStoredChallenge(
        PeerProofChallenge challenge,
        string? pairingId,
        IReadOnlyList<PeerCapability> capabilities) => new(
            challenge.ChallengeId,
            pairingId,
            challenge.Initiator.PeerId == localIdentity.PublicIdentity.PeerId
                ? challenge.Responder.PeerId
                : challenge.Initiator.PeerId,
            pairingId is null ? PeerProofPurpose.Reconnect : PeerProofPurpose.Pairing,
            challenge.Initiator.PeerId == localIdentity.PublicIdentity.PeerId
                ? PeerProofRole.Initiator
                : PeerProofRole.Responder,
            challenge.SessionId,
            challenge.ProtocolVersion,
            challenge.Challenge.ToArray(),
            capabilities.ToArray(),
            challenge.CreatedAt,
            challenge.ExpiresAt,
            false);

    private async Task<SessionAdmissionResult> VerifyRemoteProofAsync(
        StoredChallenge stored,
        PeerProof remoteProof,
        CancellationToken cancellationToken)
    {
        if (!stored.LocalProofCreated)
        {
            return Failure(PairingOperationStatus.ApprovalRequired, "Local proof has not been created.");
        }

        var remoteRole = stored.LocalRole == PeerProofRole.Initiator
            ? PeerProofRole.Responder
            : PeerProofRole.Initiator;
        if (remoteProof.SignerRole != remoteRole || remoteProof.Purpose != stored.Purpose)
        {
            return Failure(PairingOperationStatus.InvalidProof, "Proof role or purpose is invalid.");
        }

        var challenge = await RehydrateChallengeAsync(stored, cancellationToken).ConfigureAwait(false);
        var result = PeerProofService.VerifyAndConsume(challenge, remoteProof, UtcNow(), replayGuard);
        return result.Status switch
        {
            PeerProofVerificationStatus.Trusted => new SessionAdmissionResult(
                PairingOperationStatus.Accepted,
                stored.RemotePeerId,
                stored.ProtocolVersion,
                stored.RemoteCapabilities,
                string.Empty),
            PeerProofVerificationStatus.Expired or PeerProofVerificationStatus.NotYetValid =>
                Failure(PairingOperationStatus.Expired, result.Reason),
            PeerProofVerificationStatus.Replayed =>
                Failure(PairingOperationStatus.Replayed, result.Reason),
            _ => Failure(PairingOperationStatus.InvalidProof, result.Reason),
        };
    }

    private async Task<PeerProofChallenge> RehydrateChallengeAsync(
        StoredChallenge stored,
        CancellationToken cancellationToken)
    {
        PeerPublicIdentity remote;
        if (stored.PairingId is not null)
        {
            remote = (await RequirePairingAsync(stored.PairingId, cancellationToken).ConfigureAwait(false)).RemoteIdentity;
        }
        else
        {
            var trusted = await peerStore.ReadTrustedPeerAsync(stored.RemotePeerId, cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("Trusted peer was not found.");
            remote = new PeerPublicIdentity(
                trusted.PeerId,
                trusted.PublicKeySpki,
                trusted.PublicKeyFingerprint);
        }

        var local = localIdentity.PublicIdentity;
        return new PeerProofChallenge(
            stored.SessionId,
            stored.ChallengeId,
            stored.LocalRole == PeerProofRole.Initiator ? local : remote,
            stored.LocalRole == PeerProofRole.Responder ? local : remote,
            stored.ProtocolVersion,
            stored.ChallengeBytes.ToArray(),
            stored.CreatedAt,
            stored.ExpiresAt);
    }

    private async Task<PairingSnapshot> RequirePairingAsync(
        string pairingId,
        CancellationToken cancellationToken) =>
        await applicationStore.ReadPairingAsync(pairingId, cancellationToken).ConfigureAwait(false) ??
        throw new InvalidOperationException("Pairing request was not found.");

    private async Task<StoredChallenge> RequireChallengeAsync(
        string challengeId,
        CancellationToken cancellationToken) =>
        await applicationStore.ReadChallengeAsync(challengeId, cancellationToken).ConfigureAwait(false) ??
        throw new InvalidOperationException("Peer challenge was not found.");

    private void EnsureApproved(PairingSnapshot pairing)
    {
        EnsureNotExpired(pairing);
        if (pairing.State != PairingApprovalState.Approved)
        {
            throw new InvalidOperationException("Pairing requires explicit local approval.");
        }
    }

    private void EnsureNotExpired(PairingSnapshot pairing)
    {
        if (UtcNow() > pairing.ExpiresAt)
        {
            throw new InvalidOperationException("Pairing approval has expired.");
        }
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Peer clock must return UTC timestamps.");
        }

        return now;
    }

    private static void ValidateRole(PeerProofRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    private static void ValidateCapabilities(IReadOnlyList<PeerCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var capability in capabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capability.ModuleId);
            ArgumentException.ThrowIfNullOrWhiteSpace(capability.ModuleVersion);
            if (capability.DeclarationSha256.Length != 32)
            {
                throw new ArgumentException("Capability declaration hash must contain 32 bytes.", nameof(capabilities));
            }
        }
    }

    private static SessionAdmissionResult Failure(PairingOperationStatus status, string reason) =>
        new(status, null, null, [], reason);
}
