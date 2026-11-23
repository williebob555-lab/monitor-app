using System.Security.Cryptography;
using System.Text;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;

namespace MonitorApp.Peer.Mobile;

/// <summary>What a scanned mesh code is and who it names, for the confirmation screen.</summary>
public sealed record MeshCodePreview(bool IsJoinRequest, string PeerId, string DisplayName, string VerificationCode);

// Face-to-face mesh join (T085): phone to phone, phone to tablet, tablet to phone. Neither device needs to accept
// connections; the two QR codes carry everything, and the rest of the mesh learns the new device through the member.
public sealed partial class MobilePeerRuntime
{
    private (byte[] Nonce, DateTimeOffset Expires)? pendingJoin;

    /// <summary>This device's short verification code (both screens show the other device's).</summary>
    public string VerificationCode => MeshFaceToFace.VerificationCode(identity.PublicIdentity.PublicKeyFingerprint);

    /// <summary>Creates the code this device shows to join another device's mesh. Valid for ten minutes.</summary>
    public string CreateMeshJoinCode()
    {
        var now = DateTimeOffset.UtcNow;
        var request = MeshFaceToFace.CreateJoinRequest(identity.IdentityKey, identity.PublicIdentity, options.DisplayName, [], now);
        lock (gate)
        {
            pendingJoin = (request.Nonce.ToByteArray(), now + MeshFaceToFace.JoinLifetime);
        }

        return MeshFaceToFace.Encode(request);
    }

    /// <summary>Reads a scanned join or welcome code without acting on it. Throws <see cref="FormatException"/> when it is not usable.</summary>
    public MeshCodePreview PreviewMeshCode(string code)
    {
        if (code?.Trim().StartsWith(MeshFaceToFace.WelcomePrefix, StringComparison.Ordinal) == true)
        {
            var welcome = MeshFaceToFace.DecodeWelcome(code, identity.PublicIdentity.PeerId, RequirePendingNonce());
            var member = MeshFaceToFace.MemberIdentity(welcome);
            return new MeshCodePreview(false, member.PeerId, Name(welcome.MemberDisplayName), MeshFaceToFace.VerificationCode(member.PublicKeyFingerprint));
        }

        var request = DecodeForeignJoin(code ?? string.Empty);
        var joiner = MeshFaceToFace.JoinerIdentity(request);
        return new MeshCodePreview(true, joiner.PeerId, Name(request.DisplayName), MeshFaceToFace.VerificationCode(joiner.PublicKeyFingerprint));
    }

    /// <summary>
    /// Adds the device that showed <paramref name="joinCode"/> to the mesh after the owner's fingerprint, and returns the
    /// welcome code to show it. Every other member then learns the device from this one.
    /// </summary>
    public async Task<string> AdmitFromJoinCodeAsync(string joinCode, CancellationToken cancellationToken = default)
    {
        var request = DecodeForeignJoin(joinCode);
        var joiner = MeshFaceToFace.JoinerIdentity(request);
        var name = Name(request.DisplayName);
        await RequirePresenceAsync($"Add {name} to your devices",
            $"Check that {name} shows {MeshFaceToFace.VerificationCode(joiner.PublicKeyFingerprint)}.", joiner.PeerId, cancellationToken).ConfigureAwait(false);
        await TrustDirectlyAsync(joiner, name, request.SelfLocators, cancellationToken).ConfigureAwait(false);
        await Mesh.RefreshAsync(cancellationToken).ConfigureAwait(false);

        // This device's own admissions, devices that accept connections first, so the newcomer can reach them at once.
        var statements = (await Mesh.GetStatementsForExchangeAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.IssuerPeerId == identity.PublicIdentity.PeerId && item.Kind == MeshStatementKind.Admit &&
                           item.SubjectPeerId != joiner.PeerId)
            .OrderByDescending(item => item.SubjectLocators.Count)
            .ThenBy(item => item.SubjectPeerId, StringComparer.Ordinal)
            .Take(MeshFaceToFace.MaximumWelcomeStatements)
            .ToArray();
        return MeshFaceToFace.Encode(MeshFaceToFace.CreateWelcome(identity.IdentityKey, identity.PublicIdentity, options.DisplayName,
            [], request, statements));
    }

    /// <summary>Completes joining with the welcome code the member shows, after the owner's fingerprint. Returns the member's peer id.</summary>
    public async Task<string> JoinFromWelcomeAsync(string welcomeCode, CancellationToken cancellationToken = default)
    {
        var welcome = MeshFaceToFace.DecodeWelcome(welcomeCode, identity.PublicIdentity.PeerId, RequirePendingNonce());
        var member = MeshFaceToFace.MemberIdentity(welcome);
        var name = Name(welcome.MemberDisplayName);
        await RequirePresenceAsync($"Join {name}'s devices",
            $"Check that {name} shows {MeshFaceToFace.VerificationCode(member.PublicKeyFingerprint)}.", member.PeerId, cancellationToken).ConfigureAwait(false);
        await TrustDirectlyAsync(member, name, welcome.MemberLocators, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            pendingJoin = null;
        }

        // Vouch for the member first, so its statements have a trusted issuer, then learn the devices it vouches for.
        await Mesh.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await Mesh.ApplyAsync(member.PeerId, welcome.Statements, cancellationToken).ConfigureAwait(false);
        return member.PeerId;
    }

    private MeshJoinRequest DecodeForeignJoin(string code)
    {
        var request = MeshFaceToFace.DecodeJoin(code, DateTimeOffset.UtcNow);
        if (request.PeerId == identity.PublicIdentity.PeerId)
        {
            throw new FormatException("That is this device's own code. Scan the code on the other device.");
        }

        return request;
    }

    private byte[] RequirePendingNonce()
    {
        lock (gate)
        {
            if (pendingJoin is not { } pending || pending.Expires < DateTimeOffset.UtcNow)
            {
                throw new FormatException("Show your join code first (it lasts ten minutes), then scan the other device's reply.");
            }

            return pending.Nonce;
        }
    }

    private async Task RequirePresenceAsync(string title, string description, string subject, CancellationToken cancellationToken)
    {
        // Adding a device to the mesh is high risk: every device will trust it. Fail closed without a fingerprint reader.
        if (presence is not { IsAvailable: true } signer)
        {
            throw new UserPresenceDeniedException("Set up a fingerprint on this device first; adding devices needs it.");
        }

        var payload = Encoding.UTF8.GetBytes($"monitor-mesh-local-approval-v1|{identity.PublicIdentity.PeerId}|{subject}|{Guid.NewGuid():N}");
        await signer.SignAsync(payload, new UserPresenceChallenge(title, description), cancellationToken).ConfigureAwait(false);
    }

    private async Task TrustDirectlyAsync(PeerPublicIdentity peer, string name, IEnumerable<MeshLocator> locators, CancellationToken cancellationToken)
    {
        var existing = await store.ReadTrustedPeerAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.PublicKeyFingerprint, peer.PublicKeyFingerprint))
                throw new FormatException("A different device already uses that id here; it was not added.");
            if (existing.TrustState != PeerTrustState.Active)
                throw new FormatException($"{name} was removed from your devices, and a removed device cannot be added back.");
        }
        else
        {
            await store.UpsertTrustedPeerAsync(new TrustedPeer(peer.PeerId, peer.PublicKeySpki, peer.PublicKeyFingerprint, name,
                PeerTrustState.Active, DateTimeOffset.UtcNow, null), cancellationToken).ConfigureAwait(false);
        }

        foreach (var locator in locators)
        {
            await store.UpsertPeerLocatorAsync(new PeerLocator(peer.PeerId, locator.Host, (int)locator.Port, DateTimeOffset.UtcNow, null),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Name(string displayName) => string.IsNullOrWhiteSpace(displayName) ? "the other device" : displayName;
}
