using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Peer.Node;

public sealed class NodePeerInvitationService(
    WindowsDpapiIdentityProvider identity,
    PeerStore store,
    NodePeerConnectionManager connections) : IPeerInvitationService
{
    private int invitationInProgress;

    public async ValueTask<PeerInvitationResult> InviteAsync(
        PeerInvitationCommand invitation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref invitationInProgress, 1, 0) != 0)
        {
            return new PeerInvitationResult(
                PeerInvitationStatus.Rejected,
                "Another peer invitation is already in progress.");
        }

        try
        {
            var security = invitation.SecurityPolicy switch
            {
                PeerInvitationSecurityPolicy.LoopbackPrototype => PeerTransportSecurity.LoopbackDevelopment,
                PeerInvitationSecurityPolicy.AuthenticatedPrivateTransport => PeerTransportSecurity.TailscaleEncrypted,
                _ => throw new InvalidOperationException("Unsupported peer invitation security policy."),
            };
            var endpoint = new UriBuilder(
                Uri.UriSchemeHttp,
                invitation.Address.ToString(),
                invitation.Port).Uri;
            var transport = PeerTransportOptions.Create(new PeerTransportEndpoint(
                endpoint,
                invitation.ExpectedPeerId,
                security));
            var pairing = new P256PeerInitialPairingCoordinator(
                identity.PublicIdentity,
                identity.IdentityKey,
                Environment.MachineName,
                new InitiatingUserApproval(),
                new PeerStoreTrustSink(store));
            await using var client = new PeerPairingClient(
                transport,
                pairing,
                new PeerInitialPairingOptions(
                    MaximumApprovalPolls: 90,
                    InitialPollDelay: TimeSpan.FromSeconds(1),
                    MaximumPollDelay: TimeSpan.FromSeconds(2)));
            var result = await client.PairAsync(
                new PeerInitialPairingRequest(
                    Guid.NewGuid().ToString("N"),
                    InitiatingUserApproved: true,
                    Convert.FromHexString(invitation.ExpectedFingerprintSha256)),
                cancellationToken).ConfigureAwait(false);
            await connections.TrackAsync(
                result.Remote.Identity.PeerId,
                invitation.Address,
                invitation.Port,
                cancellationToken).ConfigureAwait(false);
            return new PeerInvitationResult(
                PeerInvitationStatus.Dispatched,
                $"Pairing completed with {result.Remote.Identity.PeerId}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            PeerTransportException or
            TimeoutException or
            PeerPersistenceException or
            InvalidOperationException or
            FormatException)
        {
            return new PeerInvitationResult(
                PeerInvitationStatus.Rejected,
                "Pairing did not complete. Verify the peer address, identity, approval, and transport policy.");
        }
        finally
        {
            Volatile.Write(ref invitationInProgress, 0);
        }
    }

    private sealed class InitiatingUserApproval : IRemotePairingApproval
    {
        public ValueTask<bool> ApproveAsync(
            PairingRemoteCandidate candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }
    }

    private sealed class PeerStoreTrustSink(PeerStore store) : IPeerTrustSink
    {
        public async ValueTask PersistTrustedPeerAsync(
            PairingRemoteCandidate candidate,
            DateTimeOffset pairedAt,
            CancellationToken cancellationToken) =>
            await store.UpsertTrustedPeerAsync(new TrustedPeer(
                candidate.Identity.PeerId,
                candidate.Identity.PublicKeySpki,
                candidate.Identity.PublicKeyFingerprint,
                candidate.DisplayName,
                PeerTrustState.Active,
                pairedAt,
                null), cancellationToken).ConfigureAwait(false);
    }
}
