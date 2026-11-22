using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Control;

/// <summary>
/// Desktop side of remote monitoring control. Every command arriving on an authenticated PeerSession is verified
/// (issuer = the session's peer, target = this desktop, session binding, active trust, identity signature, window,
/// nonce/command-id replay, per-peer grant, user-presence signature for elevated/high risk), applied through the
/// existing owners of each effect, stored durably (idempotent resend), and audited, whatever the outcome.
/// </summary>
public sealed class ControlService(
    ControlStore store,
    PeerStore peers,
    PairingApplicationStore pairingStore,
    PeerPairingCoordinator pairing,
    AlertStore alerts,
    MonitoringControlState monitoring,
    ILocalPeerIdentityProvider local,
    TimeProvider? timeProvider = null,
    IRemoteUpdateGateway? updates = null) : IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim serialize = new(1, 1);

    public ControlStore Store => store;

    public void Dispose() => serialize.Dispose();

    public async Task<ControlResult> ExecuteAsync(
        string remotePeerId,
        string sessionId,
        ControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePeerId);
        ArgumentNullException.ThrowIfNull(command);
        // Commands are rare and their effects are not all commutative (pairing approval), so run them one at a time.
        await serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(remotePeerId, sessionId, command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            serialize.Release();
        }
    }

    /// <summary>Read-only pending pairing requests for a trusted phone that may approve pairings.</summary>
    public async Task<PendingPairingsResponse> ReadPendingPairingsAsync(
        string remotePeerId,
        PendingPairingsRequest request,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var trusted = await peers.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        var grant = await store.ReadGrantAsync(remotePeerId, cancellationToken).ConfigureAwait(false) ??
            ControlGrant.Default(remotePeerId);
        var response = new PendingPairingsResponse { RequestId = request.RequestId };
        if (trusted is not { TrustState: PeerTrustState.Active } || !grant.PairingApproval)
        {
            return response;
        }

        foreach (var pending in await pairingStore.ReadAwaitingApprovalAsync(now, 50, cancellationToken).ConfigureAwait(false))
        {
            response.Pairings.Add(new PendingPairingSummary
            {
                PairingId = pending.PairingId,
                RemotePeerId = pending.RemoteIdentity.PeerId,
                DisplayName = pending.DisplayName ?? string.Empty,
                RemotePublicKeySha256 = ByteString.CopyFrom(pending.RemoteIdentity.PublicKeyFingerprint),
                CreatedAt = Timestamp.FromDateTimeOffset(pending.CreatedAt),
                ExpiresAt = Timestamp.FromDateTimeOffset(pending.ExpiresAt),
            });
        }

        return response;
    }

    /// <summary>
    /// Read-only update state for a trusted phone holding the update-approval grant. Without the grant (or trust) nothing
    /// is revealed. Every read, granted or refused, is audited.
    /// </summary>
    public async Task<UpdateStatusResponse> ReadUpdateStatusAsync(
        string remotePeerId,
        UpdateStatusRequest request,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var response = new UpdateStatusResponse { RequestId = request.RequestId };
        var trusted = await peers.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        var grant = await store.ReadGrantAsync(remotePeerId, cancellationToken).ConfigureAwait(false) ??
            ControlGrant.Default(remotePeerId);
        var permitted = trusted is { TrustState: PeerTrustState.Active } && grant.UpdateApproval;
        string reason;
        if (!permitted)
        {
            reason = trusted is not { TrustState: PeerTrustState.Active } ? "untrusted" : "not_permitted";
        }
        else
        {
            response.Permitted = true;
            var status = updates?.GetStatus();
            reason = "applied";
            if (status is null || !status.Available)
            {
                reason = "updates_unavailable";
            }
            else
            {
                response.Available = true;
                response.InstalledVersion = status.InstalledVersion;
                response.State = status.State;
                response.LastError = status.LastError ?? string.Empty;
                response.ApprovalPending = status.ApprovalPending;
                if (status.StagedVersion is { Length: > 0 } && TryDecodeDigest(status.StagedDigestHex, out var digest))
                {
                    response.StagedVersion = status.StagedVersion;
                    response.StagedArtifactSha256 = ByteString.CopyFrom(digest);
                }
            }
        }

        await store.AppendAuditAsync(new ControlAuditEntry(
            0, now, remotePeerId, Truncate(request.RequestId ?? string.Empty), "read_update_status", ControlRiskTier.Normal.ToString(),
            permitted ? "applied" : "refused", reason, string.Empty, false,
            response.StagedVersion.Length > 0 ? "staged=" + Truncate(response.StagedVersion) : string.Empty), cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    private static bool TryDecodeDigest(string? hex, out byte[] digest)
    {
        digest = [];
        if (hex is not { Length: 64 })
        {
            return false;
        }

        try
        {
            digest = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Local administrator decision on a presence-key enrollment; audited like any other control action.</summary>
    public async Task<PresenceEnrollment?> DecideEnrollmentAsync(
        string enrollmentId, bool approve, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var decided = await store.DecideEnrollmentAsync(enrollmentId, approve, now, cancellationToken).ConfigureAwait(false);
        await store.AppendAuditAsync(new ControlAuditEntry(
            0, now, decided?.PeerId ?? "unknown", "local:" + enrollmentId,
            approve ? "local_approve_presence_enrollment" : "local_reject_presence_enrollment",
            ControlRiskTier.High.ToString(), decided is null ? "refused" : "applied",
            decided is null ? "enrollment_not_pending" : "local_admin", string.Empty, false,
            decided is null ? string.Empty : "fingerprint=" + Convert.ToHexString(decided.Fingerprint)), cancellationToken)
            .ConfigureAwait(false);
        return decided;
    }

    /// <summary>Local administrator change of a peer's control grant.</summary>
    public async Task SetGrantAsync(ControlGrant grant, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await store.SetGrantAsync(grant, now, cancellationToken).ConfigureAwait(false);
        await store.AppendAuditAsync(new ControlAuditEntry(
            0, now, grant.PeerId, "local:grant", "local_set_grant", ControlRiskTier.High.ToString(), "applied", "local_admin",
            string.Empty, false,
            string.Create(CultureInfo.InvariantCulture, $"monitoring={grant.MonitoringControls} pairing={grant.PairingApproval} update={grant.UpdateApproval}")),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ControlResult> ExecuteCoreAsync(
        string remotePeerId,
        string sessionId,
        ControlCommand command,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var action = ControlPolicy.ActionName(command);
        var tier = command.PayloadCase == ControlCommand.PayloadOneofCase.None
            ? ControlRiskTier.Normal
            : ControlPolicy.Classify(command);
        var presenceVerified = false;

        async Task<ControlResult> RefuseAsync(string reason, string message, bool persist)
        {
            var result = Result(command.CommandId, ControlStatus.Refused, reason, message, now, replayed: false);
            if (persist)
            {
                await PersistAsync(command, action, result, cancellationToken).ConfigureAwait(false);
            }

            await AuditAsync(command, remotePeerId, sessionId, action, tier, "refused", reason, presenceVerified, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        // 1. Binding of the command to this authenticated session and this desktop.
        if (!string.Equals(command.IssuerPeerId, remotePeerId, StringComparison.Ordinal))
        {
            return await RefuseAsync("issuer_mismatch", "Command issuer is not the authenticated peer.", false).ConfigureAwait(false);
        }

        if (!string.Equals(command.TargetPeerId, local.PublicIdentity.PeerId, StringComparison.Ordinal))
        {
            return await RefuseAsync("wrong_target", "Command is addressed to a different peer.", false).ConfigureAwait(false);
        }

        if (!string.Equals(command.SessionId, sessionId, StringComparison.Ordinal))
        {
            return await RefuseAsync("session_mismatch", "Command is bound to a different session.", false).ConfigureAwait(false);
        }

        // 2. Trust: only an actively trusted peer is considered at all.
        var trusted = await peers.ReadTrustedPeerAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            return await RefuseAsync("untrusted", "Peer is not paired.", false).ConfigureAwait(false);
        }

        if (trusted.TrustState != PeerTrustState.Active)
        {
            return await RefuseAsync("revoked", "Peer trust has been revoked.", false).ConfigureAwait(false);
        }

        // 3. Structure, then the identity signature over the canonical encoding.
        if (command.PayloadCase == ControlCommand.PayloadOneofCase.None ||
            string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > ControlPolicy.MaximumIdLength ||
            command.Nonce.Length is < ControlPolicy.MinimumNonceBytes or > ControlPolicy.MaximumNonceBytes ||
            command.IssuedAt is null || command.ExpiresAt is null)
        {
            return await RefuseAsync("invalid_command", "Command is malformed.", false).ConfigureAwait(false);
        }

        if (!ControlCanonical.Verify(trusted.PublicKeySpki, ControlCanonical.IdentitySigningInput(command), command.IssuerSignature.Span))
        {
            return await RefuseAsync("bad_signature", "Command signature is invalid.", false).ConfigureAwait(false);
        }

        // 4. Idempotency: an identical command already decided returns its stored result, whatever it was.
        var payloadHash = ControlCanonical.PayloadHash(command);
        var stored = await store.ReadCommandAsync(remotePeerId, command.CommandId, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            if (CryptographicOperations.FixedTimeEquals(stored.PayloadHash, payloadHash))
            {
                var replay = Result(command.CommandId, stored.Status, stored.ReasonCode, stored.Message, stored.DecidedAt, replayed: true);
                await AuditAsync(command, remotePeerId, sessionId, action, tier, "replayed", stored.ReasonCode, false, cancellationToken)
                    .ConfigureAwait(false);
                return replay;
            }

            return await RefuseAsync("command_id_reuse", "Command id was already used for a different command.", false).ConfigureAwait(false);
        }

        // 5. Freshness. Expired commands are not recorded, so a legitimate retry may use a new window.
        var issued = command.IssuedAt.ToDateTimeOffset();
        var expires = command.ExpiresAt.ToDateTimeOffset();
        if (expires <= issued || expires - issued > ControlPolicy.MaximumCommandLifetime)
        {
            return await RefuseAsync("invalid_window", "Command validity window is invalid.", false).ConfigureAwait(false);
        }

        if (now > expires)
        {
            return await RefuseAsync("expired", "Command has expired.", false).ConfigureAwait(false);
        }

        if (issued > now + ControlPolicy.AllowedClockSkew)
        {
            return await RefuseAsync("not_yet_valid", "Command is not valid yet; check the phone clock.", false).ConfigureAwait(false);
        }

        // From here the command is authentic and fresh: its decision (including a refusal) is recorded for replay safety.
        if (await store.NonceSeenAsync(remotePeerId, command.Nonce.ToByteArray(), cancellationToken).ConfigureAwait(false))
        {
            return await RefuseAsync("nonce_reused", "Command nonce was already used.", false).ConfigureAwait(false);
        }

        var invalid = ControlPolicy.ValidatePayload(command);
        if (invalid is not null)
        {
            return await RefuseAsync(invalid, "Command parameters are out of bounds.", true).ConfigureAwait(false);
        }

        // 6. Authorisation: the desktop's grant for this peer.
        var grant = await store.ReadGrantAsync(remotePeerId, cancellationToken).ConfigureAwait(false) ??
            ControlGrant.Default(remotePeerId);
        var permitted = command.PayloadCase switch
        {
            ControlCommand.PayloadOneofCase.ResolvePairing => grant.PairingApproval,
            ControlCommand.PayloadOneofCase.EnrollPresenceKey => grant.AllowsAnything,
            ControlCommand.PayloadOneofCase.ApproveUpdate => grant.UpdateApproval,
            _ => grant.MonitoringControls,
        };
        if (!permitted)
        {
            return await RefuseAsync("not_permitted", "This peer is not permitted to do that on this desktop.", true).ConfigureAwait(false);
        }

        // 7. Elevated and high-risk commands need the person's biometric attestation from the enrolled presence key.
        if (ControlPolicy.RequiresPresence(command))
        {
            var key = await store.ReadActivePresenceKeyAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return await RefuseAsync("presence_not_enrolled",
                    "A biometric user-presence key must be enrolled and approved on this desktop first.", true).ConfigureAwait(false);
            }

            if (command.PresenceSignature.IsEmpty)
            {
                return await RefuseAsync("presence_required", "This command requires biometric verification.", true).ConfigureAwait(false);
            }

            if (!ControlCanonical.Verify(key.PublicKeySpki, ControlCanonical.PresenceSigningInput(command), command.PresenceSignature.Span))
            {
                return await RefuseAsync("presence_invalid", "Biometric verification is invalid.", true).ConfigureAwait(false);
            }

            presenceVerified = true;
        }

        // 8. Effect, store, audit.
        ControlResult outcome;
        try
        {
            outcome = await ApplyAsync(command, remotePeerId, now, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            outcome = Result(command.CommandId, ControlStatus.Refused, "pairing_not_pending", "Pairing is no longer pending.", now, false);
        }

        await PersistAsync(command, action, outcome, cancellationToken).ConfigureAwait(false);
        await AuditAsync(
            command, remotePeerId, sessionId, action, tier,
            outcome.Status switch
            {
                ControlStatus.Applied => "applied",
                ControlStatus.PendingLocalApproval => "pending_local_approval",
                _ => "refused",
            },
            outcome.ReasonCode, presenceVerified, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async Task<ControlResult> ApplyAsync(
        ControlCommand command, string remotePeerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ControlResult Ok(string message) => Result(command.CommandId, ControlStatus.Applied, "applied", message, now, false);
        ControlResult Refused(string code, string message) => Result(command.CommandId, ControlStatus.Refused, code, message, now, false);

        switch (command.PayloadCase)
        {
            case ControlCommand.PayloadOneofCase.PauseCollection:
                {
                    var pause = command.PauseCollection;
                    monitoring.Pause(pause.CapabilityId, pause.DurationSeconds == 0 ? null : TimeSpan.FromSeconds(pause.DurationSeconds));
                    return Ok(pause.DurationSeconds == 0 ? "Collection paused until resumed." : "Collection paused.");
                }
            case ControlCommand.PayloadOneofCase.ResumeCollection:
                monitoring.Resume(command.ResumeCollection.CapabilityId);
                return Ok("Collection resumed.");
            case ControlCommand.PayloadOneofCase.SetSamplingInterval:
                {
                    var interval = command.SetSamplingInterval;
                    monitoring.SetInterval(
                        interval.IntervalSeconds == 0 ? null : TimeSpan.FromSeconds(interval.IntervalSeconds),
                        interval.DurationSeconds == 0 ? null : TimeSpan.FromSeconds(interval.DurationSeconds));
                    return Ok(interval.IntervalSeconds == 0 ? "Sampling interval restored." : "Sampling interval set.");
                }
            case ControlCommand.PayloadOneofCase.AcknowledgeAlert:
                return await alerts.AcknowledgeByOperatorAsync(command.AcknowledgeAlert.AlertId, remotePeerId, now, cancellationToken)
                    .ConfigureAwait(false)
                    ? Ok("Alert acknowledged.")
                    : Refused("alert_not_found", "No such alert.");
            case ControlCommand.PayloadOneofCase.MuteAlerts:
                {
                    var mute = command.MuteAlerts;
                    await alerts.MuteAsync(
                        string.IsNullOrWhiteSpace(mute.RuleId) ? "*" : mute.RuleId,
                        now + TimeSpan.FromSeconds(mute.DurationSeconds), remotePeerId, now, cancellationToken).ConfigureAwait(false);
                    return Ok("Alerts muted.");
                }
            case ControlCommand.PayloadOneofCase.UnmuteAlerts:
                _ = await alerts.UnmuteAsync(
                    string.IsNullOrWhiteSpace(command.UnmuteAlerts.RuleId) ? "*" : command.UnmuteAlerts.RuleId, cancellationToken)
                    .ConfigureAwait(false);
                return Ok("Alerts unmuted.");
            case ControlCommand.PayloadOneofCase.ResolvePairing:
                return await ResolvePairingAsync(command, remotePeerId, now, cancellationToken).ConfigureAwait(false);
            case ControlCommand.PayloadOneofCase.EnrollPresenceKey:
                return await EnrollAsync(command, remotePeerId, now, cancellationToken).ConfigureAwait(false);
            case ControlCommand.PayloadOneofCase.ApproveUpdate:
                return await ApproveUpdateAsync(command, now, cancellationToken).ConfigureAwait(false);
            default:
                return Refused("invalid_command", "Command is malformed.");
        }
    }

    private async Task<ControlResult> ApproveUpdateAsync(ControlCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ControlResult Refused(string code, string message) => Result(command.CommandId, ControlStatus.Refused, code, message, now, false);

        var status = updates?.GetStatus();
        if (status is null || !status.Available)
        {
            return Refused("updates_unavailable", "This desktop has no update agent configured.");
        }

        var request = command.ApproveUpdate;
        if (!status.ApprovalPending || status.StagedVersion is not { Length: > 0 } ||
            !TryDecodeDigest(status.StagedDigestHex, out var stagedDigest))
        {
            return Refused("no_staged_update", "No update is staged and awaiting approval on this desktop.");
        }

        if (!string.Equals(status.StagedVersion, request.Version, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(stagedDigest, request.ArtifactSha256.Span))
        {
            return Refused("staged_update_changed", "The staged update differs from the one that was approved; review it again.");
        }

        // The gateway re-checks version and digest atomically with starting the install, closing the race with a new staging.
        return await updates!.ApproveAsync(request.Version, Convert.ToHexStringLower(stagedDigest), cancellationToken).ConfigureAwait(false)
            ? Result(command.CommandId, ControlStatus.Applied, "applied", "Update approved; installation started.", now, false)
            : Refused("staged_update_changed", "The staged update changed before it could be approved.");
    }

    private async Task<ControlResult> ResolvePairingAsync(
        ControlCommand command, string remotePeerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var request = command.ResolvePairing;
        var snapshot = await pairingStore.ReadPairingAsync(request.PairingId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result(command.CommandId, ControlStatus.Refused, "pairing_not_found", "No such pairing request.", now, false);
        }

        if (string.Equals(snapshot.RemoteIdentity.PeerId, remotePeerId, StringComparison.Ordinal))
        {
            return Result(command.CommandId, ControlStatus.Refused, "self_approval_refused",
                "A peer cannot decide its own pairing.", now, false);
        }

        if (snapshot.State != PairingApprovalState.PendingApproval || snapshot.ExpiresAt <= now)
        {
            return Result(command.CommandId, ControlStatus.Refused, "pairing_not_pending", "Pairing is no longer pending.", now, false);
        }

        if (!CryptographicOperations.FixedTimeEquals(snapshot.RemoteIdentity.PublicKeyFingerprint, request.RemotePublicKeySha256.Span))
        {
            return Result(command.CommandId, ControlStatus.Refused, "fingerprint_mismatch",
                "The pairing request's identity differs from what was approved.", now, false);
        }

        if (request.Approve)
        {
            _ = await pairing.ApprovePairingAsync(request.PairingId, cancellationToken).ConfigureAwait(false);
            return Result(command.CommandId, ControlStatus.Applied, "applied", "Pairing approved.", now, false);
        }

        await pairing.RejectPairingAsync(request.PairingId, cancellationToken).ConfigureAwait(false);
        return Result(command.CommandId, ControlStatus.Applied, "applied", "Pairing rejected.", now, false);
    }

    private async Task<ControlResult> EnrollAsync(
        ControlCommand command, string remotePeerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var enrollment = command.EnrollPresenceKey;
        var spki = enrollment.PresencePublicKeySpki.ToByteArray();
        var statement = ControlCanonical.EnrollmentStatement(remotePeerId, command.TargetPeerId, spki);
        if (!ControlCanonical.Verify(spki, statement, enrollment.ProofOfPossession.Span))
        {
            return Result(command.CommandId, ControlStatus.Refused, "enrollment_proof_invalid",
                "The presence key did not prove possession.", now, false);
        }

        var active = await store.ReadActivePresenceKeyAsync(remotePeerId, cancellationToken).ConfigureAwait(false);
        if (active is not null && active.PublicKeySpki.AsSpan().SequenceEqual(spki))
        {
            return Result(command.CommandId, ControlStatus.Applied, "already_enrolled", "This presence key is already enrolled.", now, false);
        }

        // Never activated remotely: the key only becomes usable after the local administrator approves its fingerprint.
        _ = await store.RequestEnrollmentAsync(remotePeerId, spki, now, cancellationToken).ConfigureAwait(false);
        return Result(command.CommandId, ControlStatus.PendingLocalApproval, "pending_local_approval",
            "Approve the new biometric key on this desktop (compare its fingerprint with the phone).", now, false);
    }

    private Task<bool> PersistAsync(ControlCommand command, string action, ControlResult result, CancellationToken cancellationToken) =>
        store.TryRecordCommandAsync(new StoredControlResult(
            command.IssuerPeerId, command.CommandId, command.Nonce.ToByteArray(), ControlCanonical.PayloadHash(command), action,
            result.Status, result.ReasonCode, result.SafeMessage, result.DecidedAt.ToDateTimeOffset()), cancellationToken);

    private Task AuditAsync(
        ControlCommand command,
        string remotePeerId,
        string sessionId,
        string action,
        ControlRiskTier tier,
        string outcome,
        string reason,
        bool presenceVerified,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(new ControlAuditEntry(
            0, clock.GetUtcNow(), remotePeerId, Truncate(command.CommandId), action, tier.ToString(), outcome, reason,
            Truncate(sessionId), presenceVerified, Detail(command)), cancellationToken);

    private static string Detail(ControlCommand command) => command.PayloadCase switch
    {
        ControlCommand.PayloadOneofCase.PauseCollection => string.Create(CultureInfo.InvariantCulture,
            $"scope={Scope(command.PauseCollection.CapabilityId)} seconds={command.PauseCollection.DurationSeconds}"),
        ControlCommand.PayloadOneofCase.ResumeCollection => "scope=" + Scope(command.ResumeCollection.CapabilityId),
        ControlCommand.PayloadOneofCase.SetSamplingInterval => string.Create(CultureInfo.InvariantCulture,
            $"interval={command.SetSamplingInterval.IntervalSeconds} seconds={command.SetSamplingInterval.DurationSeconds}"),
        ControlCommand.PayloadOneofCase.AcknowledgeAlert => "alert=" + Truncate(command.AcknowledgeAlert.AlertId),
        ControlCommand.PayloadOneofCase.MuteAlerts => string.Create(CultureInfo.InvariantCulture,
            $"scope={Scope(command.MuteAlerts.RuleId)} seconds={command.MuteAlerts.DurationSeconds}"),
        ControlCommand.PayloadOneofCase.UnmuteAlerts => "scope=" + Scope(command.UnmuteAlerts.RuleId),
        ControlCommand.PayloadOneofCase.ResolvePairing => "pairing=" + Truncate(command.ResolvePairing.PairingId),
        ControlCommand.PayloadOneofCase.ApproveUpdate =>
            "version=" + Truncate(command.ApproveUpdate.Version) + " sha256=" + Convert.ToHexStringLower(command.ApproveUpdate.ArtifactSha256.Span),
        ControlCommand.PayloadOneofCase.EnrollPresenceKey =>
            "key=" + Convert.ToHexString(SHA256.HashData(command.EnrollPresenceKey.PresencePublicKeySpki.Span)),
        _ => string.Empty,
    };

    private static string Scope(string value) => string.IsNullOrWhiteSpace(value) ? "*" : Truncate(value);

    private static string Truncate(string value) => value.Length <= ControlPolicy.MaximumIdLength ? value : value[..ControlPolicy.MaximumIdLength];

    private static ControlResult Result(
        string commandId, ControlStatus status, string reason, string message, DateTimeOffset at, bool replayed) => new()
        {
            CommandId = Truncate(commandId ?? string.Empty),
            Status = status,
            ReasonCode = reason,
            SafeMessage = message,
            DecidedAt = Timestamp.FromDateTimeOffset(at),
            Replayed = replayed,
        };
}
