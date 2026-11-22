using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using CorePeerProof = MonitorApp.Peer.Core.PeerProof;

namespace MonitorApp.Peer.Service;

public sealed record PeerPairingServiceOptions(string LocalDisplayName)
{
    public static PeerPairingServiceOptions Default { get; } = new("Monitor App peer");
}

public sealed class ProductionPeerPairingCoordinator(
    PeerPairingCoordinator pairing,
    ILocalPeerIdentityProvider localIdentity,
    PeerPairingServiceOptions options,
    IPeerClock? clock = null) : IPeerPairingCoordinator
{
    public async ValueTask<IReadOnlyList<PairingFrame>> HandleAsync(
        PeerPairingSessionContext context,
        PairingFrame frame,
        CancellationToken cancellationToken) => frame.MessageCase switch
        {
            PairingFrame.MessageOneofCase.Hello =>
                await HandleHelloAsync(context, frame.Hello, cancellationToken).ConfigureAwait(false),
            PairingFrame.MessageOneofCase.Proof =>
                await HandleProofAsync(context, frame.Proof, cancellationToken).ConfigureAwait(false),
            _ => [Error(ProtocolErrorCode.InvalidMessage, "Only pairing hello and proof requests are accepted.")],
        };

    private async ValueTask<IReadOnlyList<PairingFrame>> HandleHelloAsync(
        PeerPairingSessionContext context,
        PairingHello hello,
        CancellationToken cancellationToken)
    {
        var result = await pairing.BeginPairingAsync(
            hello.PairingId,
            new PairingCandidate(
                new PeerPublicIdentity(
                    hello.PeerId,
                    hello.PublicKeySpki.ToByteArray(),
                    hello.PublicKeySha256.ToByteArray()),
                string.IsNullOrWhiteSpace(hello.DisplayName) ? null : hello.DisplayName,
                PeerProofRole.Responder,
                new PeerProtocolRange(
                    hello.ProtocolVersion.Major,
                    hello.ProtocolVersion.Minor,
                    hello.ProtocolVersion.Minor),
                []),
            cancellationToken).ConfigureAwait(false);

        if (result.Status == PairingOperationStatus.ReusedWithDifferentParameters)
        {
            return [Error(ProtocolErrorCode.InvalidIdentity, "Pairing id is bound to different parameters.")];
        }

        if (result.Status == PairingOperationStatus.Revoked)
        {
            return [Error(ProtocolErrorCode.Unauthorized, "Peer trust has been revoked.")];
        }

        if (result.Status == PairingOperationStatus.Incompatible)
        {
            return
            [
                new PairingFrame
                {
                    Incompatibility = PeerPairingGrpcService.Incompatibility(hello.ProtocolVersion),
                },
            ];
        }

        context.PairingId = hello.PairingId;
        context.RemotePeerId = hello.PeerId;
        context.RemotePublicKeyFingerprint = hello.PublicKeySha256.ToByteArray();
        context.PairingExpiresAt = result.ExpiresAt;
        var localHello = new PairingFrame { Hello = LocalHello(hello.PairingId, result.SelectedVersion) };
        if (result.Status == PairingOperationStatus.ApprovalRequired)
        {
            return
            [
                localHello,
                Approval(hello.PairingId, PairingApprovalDisposition.AwaitingLocalApproval,
                    result.ExpiresAt, "Local approval is pending."),
            ];
        }

        if (result.Status == PairingOperationStatus.Rejected)
        {
            return
            [
                localHello,
                Approval(hello.PairingId, PairingApprovalDisposition.Rejected,
                    result.ExpiresAt, "Local approval was rejected."),
            ];
        }

        if (result.Status == PairingOperationStatus.Expired)
        {
            return
            [
                localHello,
                Approval(hello.PairingId, PairingApprovalDisposition.Expired,
                    result.ExpiresAt, "Pairing approval has expired."),
            ];
        }

        if (result.Status == PairingOperationStatus.Completed)
        {
            context.Completed = true;
            return
            [
                localHello,
                Confirmation(hello.PairingId, hello.PeerId, hello.PublicKeySha256),
                Approval(hello.PairingId, PairingApprovalDisposition.Completed,
                    result.ExpiresAt, "Pairing is complete."),
            ];
        }

        if (result.Status != PairingOperationStatus.Accepted)
        {
            return [Error(ProtocolErrorCode.Internal, "Pairing could not be continued.")];
        }

        var issued = await pairing.IssuePairingChallengeOnceAsync(
            hello.PairingId,
            PairingSessionId(hello.PairingId),
            cancellationToken).ConfigureAwait(false);
        if (Now() > issued.Challenge.ExpiresAt)
        {
            return
            [
                localHello,
                Approval(hello.PairingId, PairingApprovalDisposition.Expired,
                    result.ExpiresAt, "Pairing challenge has expired."),
            ];
        }

        var localProof = await pairing.CreateLocalProofAsync(issued.ChallengeId, cancellationToken)
            .ConfigureAwait(false);
        context.ChallengeId = issued.ChallengeId;
        context.ChallengeSessionId = issued.Challenge.SessionId;
        return
        [
            localHello,
            Approval(hello.PairingId, PairingApprovalDisposition.Approved,
                result.ExpiresAt, "Local approval has been granted."),
            new PairingFrame { Challenge = ToContract(issued.Challenge) },
            new PairingFrame
            {
                Proof = ToContract(
                    localProof,
                    issued.Challenge,
                    localIdentity.PublicIdentity.PeerId,
                    hello.PeerId),
            },
        ];
    }

    private async ValueTask<IReadOnlyList<PairingFrame>> HandleProofAsync(
        PeerPairingSessionContext context,
        MonitorApp.Contracts.V1.PeerProof proof,
        CancellationToken cancellationToken)
    {
        if (context.Completed)
        {
            return [Error(ProtocolErrorCode.InvalidIdentity, "Pairing proof has already been consumed.")];
        }

        if (context.PairingId is null || context.RemotePeerId is null || context.ChallengeId is null ||
            proof.SessionId != context.ChallengeSessionId ||
            proof.ChallengeId != context.ChallengeId ||
            proof.SignerPeerId != context.RemotePeerId ||
            proof.VerifierPeerId != localIdentity.PublicIdentity.PeerId)
        {
            return [Error(ProtocolErrorCode.InvalidIdentity, "Pairing proof does not match the active request.")];
        }

        var result = await pairing.CompletePairingAsync(
            context.PairingId,
            context.ChallengeId,
            new CorePeerProof(
                PeerProofPurpose.Pairing,
                PeerProofRole.Initiator,
                proof.ProofSignature.ToByteArray()),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsAuthenticated)
        {
            if (result.Status == PairingOperationStatus.Expired)
            {
                return
                [
                    Approval(context.PairingId, PairingApprovalDisposition.Expired,
                        context.PairingExpiresAt, "Pairing challenge has expired."),
                ];
            }

            return [Error(ProtocolErrorCode.InvalidIdentity, SafeFailure(result.Status))];
        }

        context.Completed = true;
        return
        [
            Confirmation(
                context.PairingId,
                context.RemotePeerId,
                context.RemotePublicKeyFingerprint ?? []),
            Approval(context.PairingId, PairingApprovalDisposition.Completed,
                context.PairingExpiresAt, "Pairing is complete."),
        ];
    }

    private PairingHello LocalHello(string pairingId, PeerProtocolVersion? selectedVersion)
    {
        var local = localIdentity.PublicIdentity;
        return new PairingHello
        {
            PairingId = pairingId,
            PeerId = local.PeerId,
            DisplayName = options.LocalDisplayName,
            PublicKeySpki = ByteString.CopyFrom(local.PublicKeySpki),
            PublicKeySha256 = ByteString.CopyFrom(local.PublicKeyFingerprint),
            ProtocolVersion = new ProtocolVersion
            {
                Major = selectedVersion?.Major ?? PeerContractRules.ProtocolMajor,
                Minor = selectedVersion?.Minor ?? PeerContractRules.BilateralSessionProofMinor,
            },
        };
    }

    private static PairingFrame Approval(
        string pairingId,
        PairingApprovalDisposition disposition,
        DateTimeOffset? expiresAt,
        string safeMessage) => new()
        {
            Approval = new PairingApproval
            {
                PairingId = pairingId,
                Disposition = disposition,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt ?? DateTimeOffset.UtcNow),
                SafeMessage = safeMessage,
            },
        };

    private PairingFrame Confirmation(string pairingId, string remotePeerId, ByteString fingerprint)
    {
        var remoteFingerprint = fingerprint.IsEmpty
            ? ByteString.CopyFrom(new byte[PeerContractRules.Sha256Size])
            : fingerprint;
        return new PairingFrame
        {
            Confirmation = new PairingConfirmation
            {
                PairingId = pairingId,
                LocalPeerId = localIdentity.PublicIdentity.PeerId,
                RemotePeerId = remotePeerId,
                RemotePublicKeySha256 = remoteFingerprint,
                PairedAt = Timestamp.FromDateTimeOffset(Now()),
            },
        };
    }

    private static PeerChallenge ToContract(PeerProofChallenge challenge) => new()
    {
        SessionId = challenge.SessionId,
        ChallengeId = challenge.ChallengeId,
        ChallengerPeerId = challenge.Responder.PeerId,
        ChallengedPeerId = challenge.Initiator.PeerId,
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

    private PairingFrame Confirmation(string pairingId, string remotePeerId, byte[] fingerprint) =>
        Confirmation(pairingId, remotePeerId, ByteString.CopyFrom(fingerprint));

    private static PairingFrame Error(ProtocolErrorCode code, string safeMessage) => new()
    {
        Error = PeerPairingGrpcService.Error(code, safeMessage, retryable: false),
    };

    private static string PairingSessionId(string pairingId) => $"pairing:{pairingId}";

    private DateTimeOffset Now()
    {
        var value = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        return value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException("Peer clock must return UTC timestamps.");
    }

    private static string SafeFailure(PairingOperationStatus status) => status switch
    {
        PairingOperationStatus.Replayed => "Pairing proof has already been used.",
        PairingOperationStatus.Revoked => "Peer trust has been revoked.",
        PairingOperationStatus.Expired => "Pairing challenge has expired.",
        _ => "Pairing proof is invalid.",
    };
}

public static class ProductionPeerPairingRegistration
{
    public static IServiceCollection AddProductionPeerPairingCoordinator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(PeerPairingServiceOptions.Default);
        services.AddSingleton<IPeerPairingCoordinator, ProductionPeerPairingCoordinator>();
        return services;
    }
}
