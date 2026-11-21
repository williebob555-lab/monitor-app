using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Service;

public interface IPeerAdvertisementProvider
{
    ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        string remotePeerId,
        CancellationToken cancellationToken);
}

public sealed class EmptyPeerAdvertisementProvider : IPeerAdvertisementProvider
{
    public ValueTask<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CapabilityDescriptor>>([]);

    public ValueTask<IReadOnlyList<OriginCursor>> GetCursorsAsync(
        string remotePeerId,
        CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<OriginCursor>>([]);
}

public sealed class PeerSessionOperationException(
    ProtocolErrorCode code,
    string safeMessage,
    bool retryable = false) : Exception(safeMessage)
{
    public ProtocolErrorCode Code { get; } = code;

    public string SafeMessage { get; } = safeMessage;

    public bool Retryable { get; } = retryable;
}

public sealed class ProductionPeerSessionCoordinator(
    PeerPairingCoordinator pairing,
    PeerStore store,
    OriginReplicationCoordinator replication,
    ILocalPeerIdentityProvider localIdentity,
    IPeerAdvertisementProvider advertisement) : IPeerSessionCoordinator
{
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
        var advertisedCursors = await advertisement.GetCursorsAsync(remoteHello.PeerId, cancellationToken)
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
            throw new PeerSessionOperationException(ProtocolErrorCode.InvalidMessage, "Acknowledgement would regress a durable cursor.");
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

    private async Task RequireActiveAsync(string remotePeerId, CancellationToken cancellationToken)
    {
        var trusted = await store.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null || trusted.TrustState != PeerTrustState.Active)
        {
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
        services.TryAddSingleton<IPeerAdvertisementProvider, EmptyPeerAdvertisementProvider>();
        services.AddSingleton<IPeerSessionCoordinator, ProductionPeerSessionCoordinator>();
        return services;
    }
}
