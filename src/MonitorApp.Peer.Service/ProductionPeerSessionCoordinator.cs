using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Service;

public sealed class EmptyPeerAdvertisementProvider : IPeerAdvertisementProvider
{
    public ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CapabilityDescriptor>>([]);

    public ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<OriginCursor>>([]);
}

public sealed record PeerAdvertisementOptions(
    int MaximumCapabilities,
    int MaximumRemoteOriginStreams)
{
    public static PeerAdvertisementOptions Default { get; } = new(256, 256);

    public void Validate()
    {
        if (MaximumCapabilities is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCapabilities));
        }

        if (MaximumRemoteOriginStreams is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRemoteOriginStreams));
        }
    }
}

public sealed class PeerStoreAdvertisementProvider(
    PeerStore store,
    PeerAdvertisementOptions options,
    TimeProvider? timeProvider = null) : IPeerAdvertisementProvider
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var stored = await store.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return stored
            .Select(capability => new CapabilityDescriptor
            {
                ModuleId = NamespacedId(capability.Namespace, capability.CapabilityId),
                ModuleVersion = capability.Version,
                DeclarationSha256 = ByteString.CopyFrom(SHA256.HashData(capability.Descriptor)),
            })
            .OrderBy(capability => capability.ModuleId, StringComparer.Ordinal)
            .ThenBy(capability => capability.ModuleVersion, StringComparer.Ordinal)
            .Take(options.MaximumCapabilities)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteHello);
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        var cursors = new List<OriginCursor>(options.MaximumRemoteOriginStreams + 1);
        var local = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            cursors.Add(new OriginCursor
            {
                OriginPeerId = local.PeerId,
                OriginStreamId = local.OriginStreamId,
                AcceptedThroughSequence = local.NextSequence - 1,
                UpdatedAt = Timestamp.FromDateTimeOffset(RequireUtc(clock.GetUtcNow())),
            });
        }

        var remoteStreams = remoteHello.Cursors
            .Where(cursor => string.Equals(
                cursor.OriginPeerId,
                remoteHello.PeerId,
                StringComparison.Ordinal))
            .Select(cursor => cursor.OriginStreamId)
            .Where(streamId => !string.IsNullOrWhiteSpace(streamId) && streamId.Length <= 512)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(streamId => streamId, StringComparer.Ordinal)
            .Take(options.MaximumRemoteOriginStreams);
        foreach (var streamId in remoteStreams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await store.ReadReplicationCursorAsync(
                remoteHello.PeerId,
                remoteHello.PeerId,
                streamId,
                cancellationToken).ConfigureAwait(false);
            if (state.Cursor is not null)
            {
                cursors.Add(new OriginCursor
                {
                    OriginPeerId = state.Cursor.OriginPeerId,
                    OriginStreamId = state.Cursor.OriginStreamId,
                    AcceptedThroughSequence = state.Cursor.AcceptedThrough,
                    UpdatedAt = Timestamp.FromDateTimeOffset(state.Cursor.UpdatedAt),
                });
            }
        }

        return cursors
            .OrderBy(cursor => cursor.OriginPeerId, StringComparer.Ordinal)
            .ThenBy(cursor => cursor.OriginStreamId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NamespacedId(string @namespace, string capabilityId)
    {
        var prefix = @namespace.TrimEnd('.');
        return capabilityId.StartsWith(prefix + ".", StringComparison.Ordinal)
            ? capabilityId
            : prefix + "." + capabilityId;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException("Advertisement clock must return UTC.");
}

public sealed class PeerSessionOperationException(
    ProtocolErrorCode code,
    string safeMessage,
    bool retryable = false,
    PrunedRange? prunedRange = null) : Exception(safeMessage)
{
    /// <summary>Set with <see cref="ProtocolErrorCode.RangePruned"/>: the range the origin removed.</summary>
    public PrunedRange? PrunedRange { get; } = prunedRange;

    public ProtocolErrorCode Code { get; } = code;

    public string SafeMessage { get; } = safeMessage;

    public bool Retryable { get; } = retryable;
}

public sealed class ProductionPeerSessionCoordinator(
    PeerPairingCoordinator pairing,
    PeerStore store,
    OriginReplicationCoordinator replication,
    ILocalPeerIdentityProvider localIdentity,
    IPeerAdvertisementProvider advertisement,
    LiveInterestRegistry? interests = null,
    AlertDeliveryService? alerts = null,
    ControlService? control = null) : IPeerSessionCoordinator
{
    public IPeerLiveSession? OpenLiveSession(string remotePeerId) => new PeerLiveSession(new PeerLiveSessionOptions(
        store,
        remotePeerId,
        (request, token) => ReadOriginRangeForPushAsync(remotePeerId, request, token),
        async token => await RequireActiveAsync(remotePeerId, token).ConfigureAwait(false),
        interests,
        RequestRemotePush: false));

    public async ValueTask<PeerSessionOpenResult> OpenAsync(
        PeerHello remoteHello,
        CancellationToken cancellationToken)
    {
        var trusted = await store.ReadTrustedPeerAsync(remoteHello.PeerId, cancellationToken)
            .ConfigureAwait(false);
        if (trusted is null)
        {
            return Failure(ProtocolErrorCode.Unauthorized, "Peer is not paired.");
        }

        if (trusted.TrustState != PeerTrustState.Active)
        {
            return Failure(ProtocolErrorCode.Unauthorized, "Peer trust has been revoked.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                trusted.PublicKeyFingerprint,
                remoteHello.PublicKeySha256.Span))
        {
            return Failure(ProtocolErrorCode.InvalidIdentity, "Presented peer identity does not match pinned trust.");
        }

        var capabilities = remoteHello.Capabilities.Select(PeerCapability.FromContract).ToArray();
        var begin = await pairing.BeginReconnectAsync(
            new PeerPublicIdentity(trusted.PeerId, trusted.PublicKeySpki, remoteHello.PublicKeySha256.ToByteArray()),
            remoteHello.SessionId,
            new PeerProtocolRange(
                remoteHello.ProtocolVersion.Major,
                remoteHello.ProtocolVersion.Minor,
                remoteHello.ProtocolVersion.Minor),
            capabilities,
            cancellationToken).ConfigureAwait(false);
        if (begin.Status != PairingOperationStatus.Accepted || begin.Challenge is null)
        {
            return Failure(Map(begin.Status), SafeReason(begin.Status));
        }

        var issued = begin.Challenge;
        var localCoreProof = await pairing.CreateLocalProofAsync(issued.ChallengeId, cancellationToken)
            .ConfigureAwait(false);
        var advertisedCapabilities = await advertisement.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        var advertisedCursors = await advertisement.GetCursorsAsync(remoteHello, cancellationToken)
            .ConfigureAwait(false);
        var local = localIdentity.PublicIdentity;
        var hello = new PeerHello
        {
            ProtocolVersion = new ProtocolVersion
            {
                Major = issued.Challenge.ProtocolVersion.Major,
                Minor = issued.Challenge.ProtocolVersion.Minor,
            },
            PeerId = local.PeerId,
            PublicKeySha256 = ByteString.CopyFrom(local.PublicKeyFingerprint),
            SessionId = remoteHello.SessionId,
        };
        hello.Capabilities.Add(advertisedCapabilities.Select(item => item.Clone()));
        hello.Cursors.Add(advertisedCursors.Select(item => item.Clone()));

        return new PeerSessionOpenResult(
            hello,
            ToContract(issued.Challenge, local.PeerId, remoteHello.PeerId),
            ToContract(localCoreProof, issued.Challenge, local.PeerId, remoteHello.PeerId));
    }

    public async ValueTask<PeerAuthenticationResult> AuthenticateAsync(
        PeerHello remoteHello,
        MonitorApp.Contracts.V1.PeerProof proof,
        CancellationToken cancellationToken)
    {
        var localRole = string.CompareOrdinal(localIdentity.PublicIdentity.PeerId, remoteHello.PeerId) < 0
            ? PeerProofRole.Initiator
            : PeerProofRole.Responder;
        var remoteRole = localRole == PeerProofRole.Initiator
            ? PeerProofRole.Responder
            : PeerProofRole.Initiator;
        var result = await pairing.AuthenticateReconnectAsync(
            proof.ChallengeId,
            new CorePeerProof(PeerProofPurpose.Reconnect, remoteRole, proof.ProofSignature.ToByteArray()),
            cancellationToken).ConfigureAwait(false);
        return result.IsAuthenticated
            ? new PeerAuthenticationResult(true)
            : new PeerAuthenticationResult(false, Error(Map(result.Status), SafeReason(result.Status)));
    }

    public async ValueTask<CapabilityExchange> ExchangeCapabilitiesAsync(
        string remotePeerId,
        CapabilityExchange remoteCapabilities,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        var response = new CapabilityExchange();
        response.Capabilities.Add((await advertisement.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false)).Select(item => item.Clone()));
        return response;
    }

    private async ValueTask<OriginRecordBatch> ReadOriginRangeForPushAsync(
        string remotePeerId,
        OriginRangeRequest request,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.OriginPeerId, localIdentity.PublicIdentity.PeerId, StringComparison.Ordinal))
        {
            throw new PeerSessionOperationException(
                ProtocolErrorCode.Unauthorized,
                "Relayed origin range requests are not supported.");
        }

        try
        {
            return await replication.ReadForPushAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OriginReplicationLimitException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.MessageTooLarge, "Origin range exceeds configured limits.");
        }
        catch (OriginReplicationValidationException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Origin range is invalid.");
        }
    }

    public async ValueTask<OriginRecordBatch> ReadOriginRangeAsync(
        string remotePeerId,
        OriginRangeRequest request,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.OriginPeerId, localIdentity.PublicIdentity.PeerId, StringComparison.Ordinal))
        {
            throw new PeerSessionOperationException(
                ProtocolErrorCode.Unauthorized,
                "Relayed origin range requests are not supported.");
        }

        try
        {
            return await replication.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OriginRangePrunedException pruned)
        {
            throw new PeerSessionOperationException(
                ProtocolErrorCode.RangePruned,
                pruned.Message,
                false,
                pruned.Range is null
                    ? null
                    : new PrunedRange
                    {
                        OriginPeerId = pruned.Range.OriginPeerId,
                        OriginStreamId = pruned.Range.OriginStreamId,
                        FirstSequence = pruned.Range.FirstSequence,
                        LastSequence = pruned.Range.LastSequence,
                    });
        }
        catch (OriginReplicationLimitException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.MessageTooLarge, "Origin range exceeds configured limits.");
        }
        catch (OriginReplicationValidationException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Origin range is invalid.");
        }
    }

    public async ValueTask<OriginAck> AcceptOriginBatchAsync(
        string remotePeerId,
        OriginRecordBatch batch,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        try
        {
            return (await replication.ApplyAsync(remotePeerId, batch, cancellationToken)
                .ConfigureAwait(false)).Acknowledgement;
        }
        catch (OriginReplicationTrustException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.Unauthorized, "Origin batch is not authorized.");
        }
        catch (OriginReplicationLimitException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.MessageTooLarge, "Origin batch exceeds configured limits.");
        }
        catch (OriginReplicationValidationException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Origin batch is invalid.");
        }
        catch (PeerPersistenceConflictException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Origin sequence conflicts with retained history.");
        }
    }

    public async ValueTask AcceptOriginAckAsync(
        string remotePeerId,
        OriginAck acknowledgement,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        var cursor = acknowledgement.Cursor;
        if (!string.Equals(cursor.OriginPeerId, localIdentity.PublicIdentity.PeerId, StringComparison.Ordinal))
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Acknowledgement origin is invalid.");
        }

        var existing = await store.ReadReplicationCursorAsync(
            remotePeerId,
            cursor.OriginPeerId,
            cursor.OriginStreamId,
            cancellationToken).ConfigureAwait(false);
        if (existing.Cursor is not null && cursor.AcceptedThroughSequence < existing.Cursor.AcceptedThrough)
        {
            // A stale acknowledgement (for example from a second concurrent session delivering the same batch)
            // never lowers a durable cursor; it is simply superseded.
            return;
        }

        await store.UpdateReplicationCursorAsync(
            new ReplicationCursor(
                remotePeerId,
                cursor.OriginPeerId,
                cursor.OriginStreamId,
                cursor.AcceptedThroughSequence,
                "raw",
                DateTimeOffset.UtcNow),
            existing.Gaps,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PeerHeartbeat> HeartbeatAsync(
        string remotePeerId,
        PeerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        return new PeerHeartbeat { SentAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
    }

    public async ValueTask<AlertBatch> PullAlertsAsync(
        string remotePeerId,
        AlertPull pull,
        CancellationToken cancellationToken)
    {
        var service = alerts ?? throw new PeerSessionOperationException(
            ProtocolErrorCode.InvalidMessage, "Alerts are not supported by this peer.");
        try
        {
            return await service.PullAsync(remotePeerId, pull, cancellationToken).ConfigureAwait(false);
        }
        catch (AlertDeliveryUnauthorizedException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.Unauthorized, "Peer trust is not active.");
        }
    }

    public async ValueTask<AlertBatch?> WaitForAlertPushAsync(
        string remotePeerId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var service = alerts ?? throw new PeerSessionOperationException(
            ProtocolErrorCode.InvalidMessage, "Alerts are not supported by this peer.");
        try
        {
            return await service.WaitForPushAsync(remotePeerId, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (AlertDeliveryUnauthorizedException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.Unauthorized, "Peer trust is not active.");
        }
    }

    public async ValueTask AcceptAlertAckAsync(
        string remotePeerId,
        AlertAck acknowledgement,
        CancellationToken cancellationToken)
    {
        var service = alerts ?? throw new PeerSessionOperationException(
            ProtocolErrorCode.InvalidMessage, "Alerts are not supported by this peer.");
        try
        {
            await service.AcknowledgeAsync(remotePeerId, acknowledgement, cancellationToken).ConfigureAwait(false);
        }
        catch (AlertDeliveryUnauthorizedException)
        {
            throw new PeerSessionOperationException(ProtocolErrorCode.Unauthorized, "Peer trust is not active.");
        }
    }

    public async ValueTask<ControlResult> ExecuteControlAsync(
        string remotePeerId,
        string sessionId,
        ControlCommand command,
        CancellationToken cancellationToken)
    {
        var service = control ?? throw new PeerSessionOperationException(
            ProtocolErrorCode.InvalidMessage, "Remote control is not supported by this peer.");
        // Refusals (including untrusted or revoked issuers) are ControlResults and are audited by the service.
        return await service.ExecuteAsync(remotePeerId, sessionId, command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PendingPairingsResponse> ReadPendingPairingsAsync(
        string remotePeerId,
        PendingPairingsRequest request,
        CancellationToken cancellationToken)
    {
        var service = control ?? throw new PeerSessionOperationException(
            ProtocolErrorCode.InvalidMessage, "Remote control is not supported by this peer.");
        await RequireActiveAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        return await service.ReadPendingPairingsAsync(remotePeerId, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireActiveAsync(string remotePeerId, CancellationToken cancellationToken)
    {
        var trusted = await store.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null || trusted.TrustState != PeerTrustState.Active)
        {
            // A peer that is no longer trusted must stop holding this origin in fast sampling.
            interests?.WithdrawRequester(remotePeerId);
            throw new PeerSessionOperationException(ProtocolErrorCode.Unauthorized, "Peer trust is not active.");
        }
    }

    private static PeerChallenge ToContract(
        PeerProofChallenge challenge,
        string localPeerId,
        string remotePeerId) => new()
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            ChallengerPeerId = localPeerId,
            ChallengedPeerId = remotePeerId,
            ProofChallenge = ByteString.CopyFrom(challenge.Challenge),
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            CreatedAt = Timestamp.FromDateTimeOffset(challenge.CreatedAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(challenge.ExpiresAt),
        };

    private static MonitorApp.Contracts.V1.PeerProof ToContract(
        CorePeerProof proof,
        PeerProofChallenge challenge,
        string localPeerId,
        string remotePeerId) => new()
        {
            SessionId = challenge.SessionId,
            ChallengeId = challenge.ChallengeId,
            SignerPeerId = localPeerId,
            VerifierPeerId = remotePeerId,
            SignatureAlgorithm = SignatureAlgorithm.EcdsaP256Sha256,
            ProofSignature = ByteString.CopyFrom(proof.Signature),
        };

    private static PeerSessionOpenResult Failure(ProtocolErrorCode code, string safeMessage) =>
        new(null, null, null, Error(code, safeMessage));

    private static ProtocolError Error(ProtocolErrorCode code, string safeMessage) => new()
    {
        Code = code,
        SafeMessage = safeMessage,
        Retryable = false,
    };

    private static ProtocolErrorCode Map(PairingOperationStatus status) => status switch
    {
        PairingOperationStatus.Revoked or PairingOperationStatus.Untrusted => ProtocolErrorCode.Unauthorized,
        PairingOperationStatus.Incompatible => ProtocolErrorCode.UnsupportedVersion,
        PairingOperationStatus.Replayed or PairingOperationStatus.InvalidProof => ProtocolErrorCode.InvalidIdentity,
        PairingOperationStatus.Expired => ProtocolErrorCode.InvalidIdentity,
        _ => ProtocolErrorCode.Internal,
    };

    private static string SafeReason(PairingOperationStatus status) => status switch
    {
        PairingOperationStatus.Revoked => "Peer trust has been revoked.",
        PairingOperationStatus.Untrusted => "Peer is not paired.",
        PairingOperationStatus.Incompatible => "Peer protocol version is incompatible.",
        PairingOperationStatus.Replayed => "Peer proof has already been used.",
        PairingOperationStatus.InvalidProof => "Peer proof is invalid.",
        PairingOperationStatus.Expired => "Peer challenge has expired.",
        _ => "Peer session could not be established.",
    };
}

public static class ProductionPeerServiceRegistration
{
    public static IServiceCollection AddProductionPeerSessionCoordinator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(PeerAdvertisementOptions.Default);
        services.TryAddSingleton<IPeerAdvertisementProvider, PeerStoreAdvertisementProvider>();
        services.AddSingleton<IPeerSessionCoordinator, ProductionPeerSessionCoordinator>();
        return services;
    }
}
