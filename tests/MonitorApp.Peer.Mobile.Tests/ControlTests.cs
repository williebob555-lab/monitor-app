using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Sync;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>
/// Remote monitoring control over the real authenticated loopback PeerSession gRPC stream, real SQLite files, the
/// real pairing coordinator, alert store, and collection scheduler. Only the biometric prompt is scripted.
/// </summary>
public sealed class ControlTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static MobilePeerOptions Options(string dir) => new(
        dir, "control-phone", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private sealed class Phone : IAsyncDisposable
    {
        private readonly string root;

        public Phone(string root, SoftwareIdentity identity, SoftwarePresenceSigner presence, MobilePeerRuntime runtime)
        {
            this.root = root;
            Identity = identity;
            Presence = presence;
            Runtime = runtime;
        }

        public SoftwareIdentity Identity { get; }

        public SoftwarePresenceSigner Presence { get; }

        public MobilePeerRuntime Runtime { get; private set; }

        public string PeerId => Runtime.Identity.PeerId;

        /// <summary>A restart of the phone app on the same files with another biometric key (a stolen-key scenario).</summary>
        public async Task<Phone> RestartWithAsync(SoftwarePresenceSigner other)
        {
            await Runtime.DisposeAsync();
            var runtime = await MobilePeerRuntime.CreateAsync(Options(root), Identity, new ScriptedDeviceReader(), other);
            return new Phone(root, Identity, other, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Identity.Dispose();
            Presence.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Pairs a phone; by default also performs the administrator's explicit grant, since an ungranted peer may do nothing.</summary>
    private static async Task<Phone> PairPhoneAsync(DesktopPeer desktop, string pairingId, bool grant = true)
    {
        var root = TestDirectories.Create();
        var identity = new SoftwareIdentity();
        var presence = new SoftwarePresenceSigner();
        var runtime = await MobilePeerRuntime.CreateAsync(Options(root), identity, new ScriptedDeviceReader(), presence);
        var pairing = runtime.PairAsync(desktop.Invitation(), pairingId);
        await desktop.ApproveWhenPendingAsync(pairingId);
        await pairing.WaitAsync(Deadline);
        if (grant)
        {
            // Test setup writes the grant directly so audit assertions only see what each test exercises.
            await desktop.ControlStore.SetGrantAsync(new ControlGrant(runtime.Identity.PeerId, MonitoringControls: true, PairingApproval: true, null), DateTimeOffset.UtcNow);
        }

        return new Phone(root, identity, presence, runtime);
    }

    /// <summary>Enrolls the phone's presence key and performs the desktop administrator's local approval.</summary>
    private static async Task EnrollPresenceAsync(DesktopPeer desktop, Phone phone)
    {
        var enrolled = await phone.Runtime.SendControlAsync(desktop.Identity.PeerId, ControlRequest.EnrollPresenceKey());
        Assert.Equal(ControlStatus.PendingLocalApproval, enrolled.Status);
        var enrollment = Assert.Single(await desktop.ControlStore.ReadEnrollmentsAsync(), item => item.PeerId == phone.PeerId && item.State == PresenceEnrollmentState.Pending);
        Assert.Equal(await phone.Runtime.GetPresenceKeyFingerprintAsync(), Convert.ToHexString(enrollment.Fingerprint));
        Assert.NotNull(await desktop.Control.DecideEnrollmentAsync(enrollment.EnrollmentId, approve: true, default));
    }

    private static async Task<ControlResult> RawAsync(
        DesktopPeer desktop, Phone phone, Func<string, CancellationToken, Task<ControlCommand>> make)
    {
        var endpoint = desktop.Invitation().ToEndpoint();
        await using var connector = new PeerGrpcConnector(
            PeerTransportOptions.Create(endpoint),
            new P256PeerSessionAuthenticationProvider(phone.Identity.PublicIdentity, desktop.Identity, phone.Identity.IdentityKey),
            new PeerSyncApplication(phone.Runtime.Store, new OriginReplicationCoordinator(phone.Runtime.Store)));
        return await connector.SendControlAsync(make, default);
    }

    private static ControlCommandSigner Signer(Phone phone, TimeProvider? time = null, bool withPresence = true) =>
        new(phone.Identity.PublicIdentity, phone.Identity.IdentityKey, withPresence ? phone.Presence : null, time);

    private static async Task<IReadOnlyList<ControlAuditEntry>> AuditAsync(DesktopPeer desktop) =>
        await desktop.ControlStore.ReadAuditAsync(200);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!await condition())
        {
            await Task.Delay(50, cancellation.Token);
        }
    }

    private static Alert Raised(string owner, string rule = "threshold.disk_free") => new(
        Alert.CreateId(owner, rule, "peer:disk", DateTimeOffset.UtcNow), 1, rule, "peer:disk", owner,
        AlertSeverity.Critical, AlertState.Raised, "Low free disk space", "5.0% free", DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task AuthorizedNormalCommandsApplyAreAuditedAndNeedNoBiometric()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-normal");
            var desktopId = desktop.Identity.PeerId;

            var interval = await phone.Runtime.SendControlAsync(desktopId,
                ControlRequest.SetSamplingInterval(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)));
            Assert.Equal(ControlStatus.Applied, interval.Status);
            Assert.Equal(TimeSpan.FromSeconds(30), desktop.Monitoring.IntervalOverride);

            var alert = Raised(desktopId);
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phone.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            var ack = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.AcknowledgeAlert(alert.AlertId));
            Assert.Equal(ControlStatus.Applied, ack.Status);
            Assert.Equal(alert.AlertId, Assert.Single(await desktop.Alerts.ReadOperatorAcknowledgementsAsync()).AlertId);
            var missing = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.AcknowledgeAlert("no-such-alert"));
            Assert.Equal("alert_not_found", missing.ReasonCode);

            Assert.Equal(0, phone.Presence.Prompts);
            var audit = await AuditAsync(desktop);
            Assert.Equal(["refused", "applied", "applied"], audit.Select(item => item.Outcome));
            Assert.All(audit, item => Assert.Equal(phone.PeerId, item.PeerId));
            Assert.All(audit, item => Assert.Equal(nameof(ControlRiskTier.Normal), item.Tier));
            Assert.Equal("set_sampling_interval", audit[2].Action);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task ExpiredTamperedWrongTargetAndWrongSessionCommandsAreRefusedAndAudited()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-refuse");
            var target = desktop.Identity.PeerId;
            var request = ControlRequest.SetSamplingInterval(TimeSpan.FromSeconds(20));

            var expired = await RawAsync(desktop, phone, (session, token) =>
                Signer(phone, new OffsetTimeProvider(TimeSpan.FromMinutes(-5))).CreateAsync(target, session, request, null, token));
            Assert.Equal("expired", expired.ReasonCode);
            var future = await RawAsync(desktop, phone, (session, token) =>
                Signer(phone, new OffsetTimeProvider(TimeSpan.FromMinutes(5))).CreateAsync(target, session, request, null, token));
            Assert.Equal("not_yet_valid", future.ReasonCode);
            var tampered = await RawAsync(desktop, phone, async (session, token) =>
            {
                var command = await Signer(phone).CreateAsync(target, session, request, null, token);
                command.SetSamplingInterval.IntervalSeconds = 10;
                return command;
            });
            Assert.Equal("bad_signature", tampered.ReasonCode);
            var wrongTarget = await RawAsync(desktop, phone, (session, token) =>
                Signer(phone).CreateAsync("peer-some-other-desktop", session, request, null, token));
            Assert.Equal("wrong_target", wrongTarget.ReasonCode);
            var wrongSession = await RawAsync(desktop, phone, (_, token) =>
                Signer(phone).CreateAsync(target, "a-different-session", request, null, token));
            Assert.Equal("session_mismatch", wrongSession.ReasonCode);

            Assert.All(new[] { expired, future, tampered, wrongTarget, wrongSession }, result => Assert.Equal(ControlStatus.Refused, result.Status));
            Assert.Null(desktop.Monitoring.IntervalOverride);
            var audit = await AuditAsync(desktop);
            Assert.Equal(
                ["session_mismatch", "wrong_target", "bad_signature", "not_yet_valid", "expired"],
                audit.Select(item => item.ReasonCode));
            Assert.All(audit, item => Assert.Equal("refused", item.Outcome));
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task UntrustedAndRevokedIssuersAreRefusedAndAudited()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-trust");
            var target = desktop.Identity.PeerId;
            var request = ControlRequest.ResumeCollection();
            var command = await Signer(phone).CreateAsync(target, "session-1", request, null, default);

            using var stranger = new SoftwareIdentity();
            var untrusted = await desktop.Control.ExecuteAsync(stranger.PublicIdentity.PeerId, "session-1", command, default);
            Assert.Equal("issuer_mismatch", untrusted.ReasonCode);
            var impostor = new ControlCommandSigner(stranger.PublicIdentity, stranger.IdentityKey, null);
            var forged = await impostor.CreateAsync(target, "session-1", request, null, default);
            var forgedResult = await desktop.Control.ExecuteAsync(stranger.PublicIdentity.PeerId, "session-1", forged, default);
            Assert.Equal("untrusted", forgedResult.ReasonCode);

            await desktop.Store.RevokeTrustedPeerAsync(phone.PeerId, DateTimeOffset.UtcNow);
            var revoked = await desktop.Control.ExecuteAsync(phone.PeerId, "session-1", command, default);
            Assert.Equal("revoked", revoked.ReasonCode);
            // And a revoked phone cannot even open a session to deliver one.
            await Assert.ThrowsAnyAsync<Exception>(() => phone.Runtime.SendControlAsync(target, request));

            var reasons = (await AuditAsync(desktop)).Select(item => item.ReasonCode).ToArray();
            Assert.Equal(["revoked", "untrusted", "issuer_mismatch"], reasons);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task ResendingTheSameCommandIsIdempotentAndReplayVariantsAreRefused()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-replay");
            var target = desktop.Identity.PeerId;

            // Resend through the public API (new session each time): the stored result comes back, nothing re-runs.
            var first = await phone.Runtime.SendControlAsync(
                desktop.Identity.PeerId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(5)), "fixed-command-id");
            Assert.Equal(ControlStatus.Applied, first.Status);
            Assert.False(first.Replayed);
            desktop.Monitoring.Resume(null);
            var again = await phone.Runtime.SendControlAsync(
                desktop.Identity.PeerId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(5)), "fixed-command-id");
            Assert.Equal(ControlStatus.Applied, again.Status);
            Assert.True(again.Replayed);
            Assert.False(desktop.Monitoring.IsPaused("monitor.system")); // not applied a second time

            // The very same signed bytes in the same session: replayed result, no effect.
            var signed = await Signer(phone).CreateAsync(target, "session-x", ControlRequest.PauseCollection(TimeSpan.FromMinutes(5)), null, default);
            Assert.Equal(ControlStatus.Applied, (await desktop.Control.ExecuteAsync(phone.PeerId, "session-x", signed, default)).Status);
            desktop.Monitoring.Resume(null);
            var replay = await desktop.Control.ExecuteAsync(phone.PeerId, "session-x", signed, default);
            Assert.True(replay.Replayed);
            Assert.False(desktop.Monitoring.IsPaused("monitor.system"));

            // Same command id for a different command, and a reused nonce under a new id, are refused.
            var reuse = await Signer(phone).CreateAsync(target, "session-x", ControlRequest.ResumeCollection(), signed.CommandId, default);
            Assert.Equal("command_id_reuse", (await desktop.Control.ExecuteAsync(phone.PeerId, "session-x", reuse, default)).ReasonCode);
            var sameNonce = await Signer(phone).CreateAsync(target, "session-x", ControlRequest.ResumeCollection(), null, default);
            sameNonce.Nonce = signed.Nonce;
            sameNonce.IssuerSignature = ByteString.CopyFrom(phone.Identity.IdentityKey.Sign(ControlCanonical.IdentitySigningInput(sameNonce)));
            Assert.Equal("nonce_reused", (await desktop.Control.ExecuteAsync(phone.PeerId, "session-x", sameNonce, default)).ReasonCode);

            var audit = await AuditAsync(desktop);
            Assert.Contains(audit, item => item.Outcome == "replayed");
            Assert.Equal(
                ["refused", "refused", "replayed", "applied", "replayed", "applied"],
                audit.Select(item => item.Outcome));
            Assert.Equal(["nonce_reused", "command_id_reuse"], audit.Take(2).Select(item => item.ReasonCode));
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task HighRiskCommandsNeedAnEnrolledBiometricKeyAndAValidPresenceSignature()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-high");
            var desktopId = desktop.Identity.PeerId;
            await using var newcomer = await PairingNewcomerAsync(desktop, "ctl-newcomer");

            var pending = Assert.Single(await phone.Runtime.ReadPendingPairingsAsync(desktopId));
            Assert.Equal("ctl-newcomer", pending.PairingId);
            Assert.Equal(newcomer.Identity.PublicIdentity.PeerId, pending.RemotePeerId);
            Assert.Equal(newcomer.Identity.PublicIdentity.PublicKeyFingerprint, pending.RemotePublicKeySha256.ToByteArray());
            var approve = ControlRequest.ResolvePairing(pending.PairingId, pending.RemotePublicKeySha256.ToByteArray(), approve: true);

            // No presence key enrolled yet: refused even though the phone is trusted and the signature is valid.
            var unenrolled = await phone.Runtime.SendControlAsync(desktopId, approve);
            Assert.Equal("presence_not_enrolled", unenrolled.ReasonCode);

            // Enrollment alone is not enough: a remote peer cannot activate its own presence key.
            var enrollResult = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.EnrollPresenceKey());
            Assert.Equal(ControlStatus.PendingLocalApproval, enrollResult.Status);
            Assert.Equal("presence_not_enrolled", (await phone.Runtime.SendControlAsync(desktopId, approve)).ReasonCode);
            var enrollment = Assert.Single(await desktop.ControlStore.ReadEnrollmentsAsync(), item => item.State == PresenceEnrollmentState.Pending);
            await desktop.Control.DecideEnrollmentAsync(enrollment.EnrollmentId, approve: true, default);

            // Enrolled, but the signature is missing (a compromised app without the biometric key).
            var stripped = await RawAsync(desktop, phone, async (session, token) =>
            {
                var command = await Signer(phone).CreateAsync(desktopId, session, approve, null, token);
                command.PresenceSignature = ByteString.Empty;
                command.IssuerSignature = ByteString.CopyFrom(phone.Identity.IdentityKey.Sign(ControlCanonical.IdentitySigningInput(command)));
                return command;
            });
            Assert.Equal("presence_required", stripped.ReasonCode);

            // Signed by some other key (not the enrolled one).
            using var otherKey = new SoftwarePresenceSigner();
            var other = await RawAsync(desktop, phone, (session, token) =>
                new ControlCommandSigner(phone.Identity.PublicIdentity, phone.Identity.IdentityKey, otherKey).CreateAsync(desktopId, session, approve, null, token));
            Assert.Equal("presence_invalid", other.ReasonCode);

            // The person declining the prompt sends nothing at all.
            phone.Presence.Deny = true;
            await Assert.ThrowsAsync<UserPresenceDeniedException>(() => phone.Runtime.SendControlAsync(desktopId, approve));
            phone.Presence.Deny = false;
            Assert.Equal(PairingApprovalState.PendingApproval, (await desktop.PairingStore.ReadPairingAsync("ctl-newcomer"))!.State);

            // The enrolled key's biometric signature: accepted, and the new device finishes pairing.
            var promptsBefore = phone.Presence.Prompts;
            var approved = await phone.Runtime.SendControlAsync(desktopId, approve);
            Assert.Equal(ControlStatus.Applied, approved.Status);
            Assert.True(phone.Presence.Prompts > promptsBefore);
            await newcomer.PairingTask.WaitAsync(Deadline);
            Assert.NotNull(await desktop.Store.ReadTrustedPeerAsync(newcomer.Identity.PublicIdentity.PeerId));

            var audit = await AuditAsync(desktop);
            Assert.Contains(audit, item => item.Action == "approve_pairing" && item.Outcome == "applied" && item.PresenceVerified &&
                item.Tier == nameof(ControlRiskTier.High));
            Assert.Contains(audit, item => item.ReasonCode == "presence_invalid" && !item.PresenceVerified);
            Assert.Contains(audit, item => item.Action == "local_approve_presence_enrollment");
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task ElevatedPauseAndMuteNeedBiometricWhileShortOnesDoNot()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-elevated");
            var desktopId = desktop.Identity.PeerId;

            Assert.Equal(ControlRiskTier.Normal, ControlCommandSigner.TierOf(ControlRequest.PauseCollection(TimeSpan.FromHours(1))));
            Assert.Equal(ControlRiskTier.Elevated, ControlCommandSigner.TierOf(ControlRequest.PauseCollection(TimeSpan.FromHours(2))));
            Assert.Equal(ControlRiskTier.Elevated, ControlCommandSigner.TierOf(ControlRequest.PauseCollection()));
            Assert.Equal(ControlRiskTier.Normal, ControlCommandSigner.TierOf(ControlRequest.MuteAlerts(TimeSpan.FromHours(24))));
            Assert.Equal(ControlRiskTier.Elevated, ControlCommandSigner.TierOf(ControlRequest.MuteAlerts(TimeSpan.FromHours(25))));
            Assert.Equal(ControlRiskTier.Normal, ControlCommandSigner.TierOf(ControlRequest.ResolvePairing("p", new byte[32], approve: false)));
            Assert.Equal(ControlRiskTier.High, ControlCommandSigner.TierOf(ControlRequest.ResolvePairing("p", new byte[32], approve: true)));

            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(30)))).Status);
            Assert.Equal(0, phone.Presence.Prompts);
            desktop.Monitoring.Resume(null);
            Assert.Equal("presence_not_enrolled", (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromHours(3)))).ReasonCode);
            Assert.Equal("presence_not_enrolled", (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.MuteAlerts(TimeSpan.FromDays(2)))).ReasonCode);

            await EnrollPresenceAsync(desktop, phone);
            var longPause = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromHours(3), "monitor.system"));
            Assert.Equal(ControlStatus.Applied, longPause.Status);
            Assert.True(desktop.Monitoring.IsPaused("monitor.system"));
            Assert.False(desktop.Monitoring.IsPaused("other.collector"));
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.MuteAlerts(TimeSpan.FromDays(2)))).Status);
            Assert.Contains(await desktop.Alerts.ReadActiveMutesAsync(DateTimeOffset.UtcNow), mute => mute.Scope == "*" && mute.Recipient == phone.PeerId);

            // Out-of-bounds parameters never reach the effect.
            var tooFast = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.SetSamplingInterval(TimeSpan.FromSeconds(1)));
            Assert.Equal("invalid_payload", tooFast.ReasonCode);
            // A phone without a biometric cannot even try.
            phone.Presence.IsAvailable = false;
            await Assert.ThrowsAsync<UserPresenceUnavailableException>(() => phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection()));
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task AnotherPresenceKeyOnTheSamePhoneCannotBeUsedUntilItIsLocallyApproved()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-rotate");
            var desktopId = desktop.Identity.PeerId;
            await EnrollPresenceAsync(desktop, phone);
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection())).Status);
            desktop.Monitoring.Resume(null);

            using var replacement = new SoftwarePresenceSigner();
            await using var restarted = await phone.RestartWithAsync(replacement);
            Assert.Equal("presence_invalid", (await restarted.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection())).ReasonCode);
            await EnrollPresenceAsync(desktop, restarted);
            Assert.Equal(ControlStatus.Applied, (await restarted.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection())).Status);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task PerPeerGrantsRestrictWhatAPhoneMayDo()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-grant", grant: false);
            var desktopId = desktop.Identity.PeerId;
            await using var newcomer = await PairingNewcomerAsync(desktop, "ctl-grant-newcomer");

            // Owner decision: a newly paired device is denied everything until the administrator grants it.
            Assert.Equal("not_permitted", (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.ResumeCollection())).ReasonCode);
            Assert.Equal("not_permitted", (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.EnrollPresenceKey())).ReasonCode);
            Assert.Empty(await phone.Runtime.ReadPendingPairingsAsync(desktopId));
            await desktop.Control.SetGrantAsync(new ControlGrant(phone.PeerId, MonitoringControls: true, PairingApproval: true, null), default);
            Assert.Single(await phone.Runtime.ReadPendingPairingsAsync(desktopId));
            await desktop.Control.SetGrantAsync(new ControlGrant(phone.PeerId, MonitoringControls: false, PairingApproval: false, null), default);
            Assert.Equal("not_permitted", (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.ResumeCollection())).ReasonCode);
            Assert.Empty(await phone.Runtime.ReadPendingPairingsAsync(desktopId));
            await desktop.Control.SetGrantAsync(new ControlGrant(phone.PeerId, MonitoringControls: true, PairingApproval: false, null), default);
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.ResumeCollection())).Status);
            Assert.Contains(await AuditAsync(desktop), item => item.Action == "local_set_grant");
            await desktop.Pairing.RejectPairingAsync("ctl-grant-newcomer");
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task PauseStopsRealCollectionAndResumeRestartsIt()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-pause");
            var desktopId = desktop.Identity.PeerId;
            await using var scheduler = new NodeCollectionScheduler(
                desktop.Collector, TimeSpan.FromMinutes(1), desktop.Interests, desktopId, desktop.Monitoring);
            using var viewing = desktop.Interests.WatchLocal(desktopId); // fast (1 s) sampling while someone watches
            scheduler.Start();

            async Task<ulong> HeadAsync() => (await desktop.Store.ReadLocalIdentityAsync())!.NextSequence;
            var start = await HeadAsync();
            await WaitForAsync(async () => await HeadAsync() > start + 12);

            var paused = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(10), "monitor.system"));
            Assert.Equal(ControlStatus.Applied, paused.Status);
            await WaitForAsync(() => Task.FromResult(scheduler.IsPaused));
            await Task.Delay(1500); // let a collection that was already running finish
            var frozen = await HeadAsync();
            await Task.Delay(3500);
            Assert.Equal(frozen, await HeadAsync());

            var resumed = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.ResumeCollection("monitor.system"));
            Assert.Equal(ControlStatus.Applied, resumed.Status);
            await WaitForAsync(async () => await HeadAsync() > frozen);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task MutedAlertIsNotDeliveredWhileMutedAndArrivesAfterUnmute()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-mute");
            var desktopId = desktop.Identity.PeerId;
            await phone.Runtime.SyncOnceAsync(); // registers the phone as an alert recipient

            var muted = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.MuteAlerts(TimeSpan.FromHours(1), "threshold.disk_free"));
            Assert.Equal(ControlStatus.Applied, muted.Status);
            var alert = Raised(desktopId);
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phone.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            var other = Raised(desktopId, "threshold.memory_free");
            Assert.True(await desktop.Alerts.RaiseAsync(other, [phone.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));

            await phone.Runtime.SyncOnceAsync();
            var delivered = await phone.Runtime.ReadAlertsAsync();
            Assert.Equal(other.AlertId, Assert.Single(delivered).Alert.AlertId); // only the unmuted rule

            var unmuted = await phone.Runtime.SendControlAsync(desktopId, ControlRequest.UnmuteAlerts("threshold.disk_free"));
            Assert.Equal(ControlStatus.Applied, unmuted.Status);
            await phone.Runtime.SyncOnceAsync();
            Assert.Contains(await phone.Runtime.ReadAlertsAsync(), item => item.Alert.AlertId == alert.AlertId);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task PhonesSetTheirOwnAlertPreferencesAndOnlyMatchingAlertsAreQueuedForThem()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phoneA = await PairPhoneAsync(desktop, "ctl-pref-a");
            await using var phoneB = await PairPhoneAsync(desktop, "ctl-pref-b");
            await using var ungranted = await PairPhoneAsync(desktop, "ctl-pref-c", grant: false);
            var desktopId = desktop.Identity.PeerId;
            await phoneA.Runtime.SyncOnceAsync();
            await phoneB.Runtime.SyncOnceAsync();

            Assert.Equal(ControlRiskTier.Normal, ControlCommandSigner.TierOf(ControlRequest.SetAlertPreferences(AlertSeverity.Critical)));
            // Respects grants like mute does.
            var refused = await ungranted.Runtime.SendControlAsync(desktopId, ControlRequest.SetAlertPreferences(AlertSeverity.Critical));
            Assert.Equal("not_permitted", refused.ReasonCode);
            Assert.Empty(await desktop.Alerts.ReadPreferencesAsync());
            // Reading needs the same grant: an ungranted phone is not permitted and learns nothing.
            var notPermitted = await ungranted.Runtime.ReadAlertPreferencesAsync(desktopId);
            Assert.False(notPermitted.Permitted);
            Assert.Empty(notPermitted.MutedRuleKinds);
            var defaultRead = await phoneA.Runtime.ReadAlertPreferencesAsync(desktopId);
            Assert.True(defaultRead.Permitted);
            Assert.Equal(AlertSeverity.Unspecified, defaultRead.MinSeverity);
            Assert.Empty(defaultRead.MutedRuleKinds);
            Assert.Null(defaultRead.UpdatedAt);
            Assert.Equal("invalid_payload", (await phoneA.Runtime.SendControlAsync(desktopId,
                ControlRequest.SetAlertPreferences(AlertSeverity.Info, [new string('x', 200)]))).ReasonCode);

            // A: warnings and above, but never CPU alerts. B keeps the default (everything).
            var set = await phoneA.Runtime.SendControlAsync(desktopId,
                ControlRequest.SetAlertPreferences(AlertSeverity.Warning, ["threshold.cpu_high"]));
            Assert.Equal(ControlStatus.Applied, set.Status);
            var stored = Assert.Single(await desktop.Alerts.ReadPreferencesAsync());
            Assert.Equal(phoneA.PeerId, stored.RecipientPeerId);
            Assert.Equal(AlertSeverity.Warning, stored.MinimumSeverity);
            Assert.Equal(["threshold.cpu_high"], stored.MutedRuleKinds);
            var readBack = await phoneA.Runtime.ReadAlertPreferencesAsync(desktopId);
            Assert.True(readBack.Permitted);
            Assert.Equal(AlertSeverity.Warning, readBack.MinSeverity);
            Assert.Equal(["threshold.cpu_high"], readBack.MutedRuleKinds);
            Assert.NotNull(readBack.UpdatedAt);
            // Another phone only ever reads its own (still the default), never A's.
            var otherRead = await phoneB.Runtime.ReadAlertPreferencesAsync(desktopId);
            Assert.True(otherRead.Permitted);
            Assert.Equal(AlertSeverity.Unspecified, otherRead.MinSeverity);
            Assert.Empty(otherRead.MutedRuleKinds);
            Assert.Null(otherRead.UpdatedAt);
            Assert.Contains(await AuditAsync(desktop), entry =>
                entry.Action == "read_alert_preferences" && entry.PeerId == ungranted.PeerId && entry.Outcome == "refused");

            Alert Make(string rule, string subject, AlertSeverity severity) => new(
                Alert.CreateId(desktopId, rule, subject, DateTimeOffset.UtcNow), 1, rule, subject, desktopId, severity,
                AlertState.Raised, rule, "detail", DateTimeOffset.UtcNow, null);
            var cpu = Make("threshold.cpu_high", "peer:cpu", AlertSeverity.Warning);
            var info = Make("peer.silent", "peer:quiet", AlertSeverity.Info);
            var disk = Make("threshold.disk_free", "peer:disk", AlertSeverity.Critical);
            var recipients = new[] { phoneA.PeerId, phoneB.PeerId };
            foreach (var alert in new[] { cpu, info, disk })
            {
                Assert.True(await desktop.Alerts.RaiseAsync(alert, recipients, TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            }

            await phoneA.Runtime.SyncOnceAsync();
            await phoneB.Runtime.SyncOnceAsync();
            Assert.Equal([disk.AlertId], (await phoneA.Runtime.ReadAlertsAsync()).Select(item => item.Alert.AlertId).ToArray());
            Assert.Equal(
                new[] { cpu.AlertId, info.AlertId, disk.AlertId }.Order().ToArray(),
                (await phoneB.Runtime.ReadAlertsAsync()).Select(item => item.Alert.AlertId).Order().ToArray());
            // Filtered alerts remain recorded as events, just never queued for A.
            Assert.Equal(3, (await desktop.Alerts.ReadActiveAsync()).Count);
            Assert.Equal([disk.AlertId], (await desktop.Alerts.ReadOutboxAsync(phoneA.PeerId)).Select(item => item.AlertId).ToArray());

            // Resolutions follow the same filter; clearing the preferences restores the default for later alerts.
            _ = await desktop.Alerts.ResolveAsync(desktopId, cpu.RuleId, cpu.SubjectId, DateTimeOffset.UtcNow, recipients, TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
            Assert.DoesNotContain(await desktop.Alerts.ReadOutboxAsync(phoneA.PeerId), item => item.AlertId == cpu.AlertId);
            Assert.Equal(ControlStatus.Applied, (await phoneA.Runtime.SendControlAsync(desktopId, ControlRequest.SetAlertPreferences(AlertSeverity.Unspecified))).Status);
            var again = Make("threshold.cpu_high", "peer:cpu2", AlertSeverity.Warning);
            Assert.True(await desktop.Alerts.RaiseAsync(again, recipients, TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            await phoneA.Runtime.SyncOnceAsync();
            Assert.Contains(await phoneA.Runtime.ReadAlertsAsync(), item => item.Alert.AlertId == again.AlertId);

            var audit = await AuditAsync(desktop);
            Assert.Contains(audit, entry => entry.Action == "set_alert_preferences" && entry.PeerId == phoneA.PeerId && entry.Outcome == "applied");
            Assert.Contains(audit, entry => entry.Action == "set_alert_preferences" && entry.PeerId == ungranted.PeerId && entry.Outcome == "refused");
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    private sealed class ShiftedTime(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    [Fact]
    public async Task ManualOverridesSurviveANodeRestartUnlessTheyHaveExpired()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-persist");
            var desktopId = desktop.Identity.PeerId;
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(30), "monitor.system"))).Status);
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.PauseCollection(TimeSpan.FromMinutes(40), "monitor.lan"))).Status);
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.SetSamplingInterval(TimeSpan.FromSeconds(45)))).Status);
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.ResumeCollection("monitor.lan"))).Status);

            // "Restart": a brand-new state and service over the same database file, as the node builds them at start.
            async Task<MonitoringControlState> RestartAsync(TimeProvider time)
            {
                var store = new ControlStore(ControlStoreOptions.Create(desktop.DatabasePath));
                await store.InitializeAsync();
                var restarted = new MonitoringControlState(time);
                using var service = new ControlService(
                    store, desktop.Store, desktop.PairingStore, desktop.Pairing, desktop.Alerts, restarted, desktop.LocalIdentity);
                await service.RestoreMonitoringAsync();
                return restarted;
            }

            var soon = await RestartAsync(TimeProvider.System);
            var diag = $"pauses=[{string.Join(",", soon.Read().Pauses.Keys)}] interval={soon.IntervalOverride}";
            Assert.True(soon.IsPaused("monitor.system"), diag);
            Assert.False(soon.IsPaused("monitor.lan"), "explicitly resumed collector must stay resumed; " + diag);
            Assert.Equal(TimeSpan.FromSeconds(45), soon.IntervalOverride);

            // The same restart two hours later: the 30 minute pause has lapsed and is not restored.
            var later = await RestartAsync(new ShiftedTime(TimeSpan.FromHours(2)));
            Assert.False(later.IsPaused("monitor.system"), "expired pause was restored");
            Assert.Equal(TimeSpan.FromSeconds(45), later.IntervalOverride); // no expiry: still in force

            // Clearing the interval is persisted too.
            Assert.Equal(ControlStatus.Applied, (await phone.Runtime.SendControlAsync(desktopId, ControlRequest.SetSamplingInterval(null))).Status);
            Assert.Null((await RestartAsync(TimeProvider.System)).IntervalOverride);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task AMuteFromOnePhoneSilencesOnlyThatPhone()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phoneA = await PairPhoneAsync(desktop, "ctl-mute-a");
            await using var phoneB = await PairPhoneAsync(desktop, "ctl-mute-b");
            var desktopId = desktop.Identity.PeerId;
            await phoneA.Runtime.SyncOnceAsync();
            await phoneB.Runtime.SyncOnceAsync();

            Assert.Equal(ControlStatus.Applied, (await phoneA.Runtime.SendControlAsync(desktopId, ControlRequest.MuteAlerts(TimeSpan.FromHours(1)))).Status);
            var alert = Raised(desktopId);
            Assert.True(await desktop.Alerts.RaiseAsync(alert, [phoneA.PeerId, phoneB.PeerId], TimeSpan.FromDays(1), DateTimeOffset.UtcNow));
            await phoneA.Runtime.SyncOnceAsync();
            await phoneB.Runtime.SyncOnceAsync();

            var mutes = await desktop.Alerts.ReadActiveMutesAsync(DateTimeOffset.UtcNow);
            var diag = "mutes=" + string.Join(",", mutes.Select(m => $"{m.Recipient[..8]}:{m.Scope}"));
            Assert.Equal(phoneA.PeerId, Assert.Single(mutes).Recipient);
            Assert.Empty(await phoneA.Runtime.ReadAlertsAsync());
            Assert.True((await phoneB.Runtime.ReadAlertsAsync()).Any(item => item.Alert.AlertId == alert.AlertId), "phone B was silenced by A's mute; " + diag);

            // B cannot lift A's mute (unmute is scoped to the issuer), and A's own unmute releases the queued alert.
            Assert.Equal(ControlStatus.Applied, (await phoneB.Runtime.SendControlAsync(desktopId, ControlRequest.UnmuteAlerts())).Status);
            Assert.Single(await desktop.Alerts.ReadActiveMutesAsync(DateTimeOffset.UtcNow));
            Assert.Equal(ControlStatus.Applied, (await phoneA.Runtime.SendControlAsync(desktopId, ControlRequest.UnmuteAlerts())).Status);
            await phoneA.Runtime.SyncOnceAsync();
            Assert.Contains(await phoneA.Runtime.ReadAlertsAsync(), item => item.Alert.AlertId == alert.AlertId);

            var audit = await AuditAsync(desktop);
            Assert.Contains(audit, entry => entry.PeerId == phoneA.PeerId && entry.Action == "mute_alerts" && entry.Outcome == "applied");
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task LocalApiExposesAuditAndLetsTheAdministratorApprovePresenceEnrollments()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phone = await PairPhoneAsync(desktop, "ctl-api");
            await using var host = LocalApiHost.Create(
                LocalApiOptions.Loopback(desktop.DatabasePath), desktop.Store, desktop.PairingStore, desktop.Pairing,
                new SystemPeerClock(), alertStore: desktop.Alerts, control: desktop.Control, monitoring: desktop.Monitoring);
            await host.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() })
            {
                BaseAddress = host.Address,
            };

            var enrolled = await phone.Runtime.SendControlAsync(desktop.Identity.PeerId, ControlRequest.EnrollPresenceKey());
            Assert.Equal(ControlStatus.PendingLocalApproval, enrolled.Status);
            var control = await client.GetFromJsonAsync<JsonElement>("api/v1/control");
            var enrollment = control.GetProperty("presenceEnrollments").EnumerateArray().Single();
            Assert.Equal("Pending", enrollment.GetProperty("state").GetString());
            Assert.Equal(await phone.Runtime.GetPresenceKeyFingerprintAsync(), enrollment.GetProperty("fingerprintSha256").GetString());

            // Mutations need the CSRF token: without it the approval is refused.
            var enrollmentId = enrollment.GetProperty("enrollmentId").GetString();
            var denied = await client.PostAsync($"api/v1/control/presence-enrollments/{enrollmentId}/approve", null);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
            var token = (await client.GetFromJsonAsync<JsonElement>("api/csrf")).GetProperty("requestToken").GetString();
            using var approve = new HttpRequestMessage(HttpMethod.Post, $"api/v1/control/presence-enrollments/{enrollmentId}/approve");
            approve.Headers.Add("X-CSRF-TOKEN", token);
            Assert.True((await client.SendAsync(approve)).IsSuccessStatusCode);
            Assert.Equal(ControlStatus.Applied,
                (await phone.Runtime.SendControlAsync(desktop.Identity.PeerId, ControlRequest.PauseCollection())).Status);

            var audit = await client.GetFromJsonAsync<JsonElement>("api/v1/audit?limit=10");
            var entries = audit.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(["pause_collection", "local_approve_presence_enrollment", "enroll_presence_key"],
                entries.Select(item => item.GetProperty("action").GetString()));
            Assert.Equal(phone.PeerId, entries[0].GetProperty("peerId").GetString());
            Assert.True(entries[0].GetProperty("presenceVerified").GetBoolean());
            var filtered = await client.GetFromJsonAsync<JsonElement>("api/v1/audit?peerId=nobody");
            Assert.Empty(filtered.GetProperty("entries").EnumerateArray());
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("api/v1/audit?limit=0")).StatusCode);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    /// <summary>
    /// T051 end to end over the real PeerSession and loopback API: an unacknowledged Critical alert is escalated once to every
    /// phone; one acknowledged by phone A (and one acknowledged on the desktop) is not, and the desktop API says who acknowledged.
    /// The escalation clock is the evaluator's TimeProvider (real time shifted 20 minutes), as the T042 cooldown tests do.
    /// </summary>
    [Fact]
    public async Task UnacknowledgedCriticalAlertEscalatesOnceWhileAcknowledgedOnesDoNot()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await using var phoneA = await PairPhoneAsync(desktop, "esc-a");
            await using var phoneB = await PairPhoneAsync(desktop, "esc-b");
            var desktopId = desktop.Identity.PeerId;
            await phoneA.Runtime.SyncOnceAsync();
            await phoneB.Runtime.SyncOnceAsync();

            var now = DateTimeOffset.UtcNow;
            Alert Critical(string rule) => new(
                Alert.CreateId(desktopId, rule, "peer:disk", now), 1, rule, "peer:disk", desktopId,
                AlertSeverity.Critical, AlertState.Raised, "Low free disk space " + rule, "5.0% free", now, null);
            var silent = Critical("rule.nobody-acks");
            var ackedByPhone = Critical("rule.phone-acks");
            var ackedLocally = Critical("rule.desktop-acks");
            foreach (var alert in new[] { silent, ackedByPhone, ackedLocally })
            {
                Assert.True(await desktop.Alerts.RaiseAsync(alert, [phoneA.PeerId, phoneB.PeerId], TimeSpan.FromDays(1), now));
            }

            foreach (var phone in new[] { phoneA, phoneB })
            {
                await phone.Runtime.SyncOnceAsync();
                foreach (var stored in await phone.Runtime.ReadAlertsAsync())
                {
                    Assert.False(stored.Alert.Escalated);
                    await phone.Runtime.MarkAlertNotifiedAsync(stored);
                }

                Assert.Empty(await phone.Runtime.ReadPendingNotificationsAsync());
            }

            Assert.Equal(ControlStatus.Applied,
                (await phoneA.Runtime.SendControlAsync(desktopId, ControlRequest.AcknowledgeAlert(ackedByPhone.AlertId))).Status);
            await using var host = LocalApiHost.Create(
                LocalApiOptions.Loopback(desktop.DatabasePath), desktop.Store, desktop.PairingStore, desktop.Pairing,
                new SystemPeerClock(), alertStore: desktop.Alerts, control: desktop.Control, monitoring: desktop.Monitoring);
            await host.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() })
            {
                BaseAddress = host.Address,
            };
            var locallyDenied = await client.PostAsync($"api/v1/alerts/{ackedLocally.AlertId}/acknowledge", null);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, locallyDenied.StatusCode);
            var token = (await client.GetFromJsonAsync<JsonElement>("api/csrf")).GetProperty("requestToken").GetString();
            using var acknowledge = new HttpRequestMessage(HttpMethod.Post, $"api/v1/alerts/{ackedLocally.AlertId}/acknowledge");
            acknowledge.Headers.Add("X-CSRF-TOKEN", token);
            Assert.True((await client.SendAsync(acknowledge)).IsSuccessStatusCode);

            // Fourteen minutes in: nothing escalates; twenty minutes in: exactly the silent alert, once.
            var config = AlertRulesConfig.Default with { PeerSilentAfter = TimeSpan.FromHours(2), EscalationAfter = TimeSpan.FromMinutes(15) };
            Assert.Equal(0, (await new AlertEvaluator(desktop.Store, desktop.Alerts, config, new OffsetTimeProvider(TimeSpan.FromMinutes(14)))
                .EvaluateOnceAsync()).Escalated);
            var late = new AlertEvaluator(desktop.Store, desktop.Alerts, config, new OffsetTimeProvider(TimeSpan.FromMinutes(20)));
            Assert.Equal(1, (await late.EvaluateOnceAsync()).Escalated);

            foreach (var phone in new[] { phoneA, phoneB })
            {
                await phone.Runtime.SyncOnceAsync();
                var needing = Assert.Single(await phone.Runtime.ReadPendingNotificationsAsync());
                Assert.Equal(silent.AlertId, needing.Alert.AlertId);
                Assert.True(needing.Alert.Escalated);
                await phone.Runtime.MarkAlertNotifiedAsync(needing);
                await phone.Runtime.SyncOnceAsync();
                Assert.Empty(await phone.Runtime.ReadPendingNotificationsAsync());
            }

            // A later evaluation, even from a fresh evaluator (a node restart), does not escalate again.
            Assert.Equal(0, (await new AlertEvaluator(desktop.Store, desktop.Alerts, config, new OffsetTimeProvider(TimeSpan.FromMinutes(45)))
                .EvaluateOnceAsync()).Escalated);
            Assert.Empty(await phoneA.Runtime.ReadPendingNotificationsAsync());

            var listed = await client.GetFromJsonAsync<JsonElement>("api/v1/alerts");
            var byId = listed.GetProperty("alerts").EnumerateArray().ToDictionary(item => item.GetProperty("alertId").GetString()!);
            Assert.True(byId[ackedByPhone.AlertId].GetProperty("acknowledged").GetBoolean());
            Assert.Equal(phoneA.PeerId, byId[ackedByPhone.AlertId].GetProperty("acknowledgedBy").GetString());
            Assert.Equal(JsonValueKind.String, byId[ackedByPhone.AlertId].GetProperty("acknowledgedAt").ValueKind);
            Assert.False(byId[ackedByPhone.AlertId].GetProperty("escalated").GetBoolean());
            Assert.Equal(LocalApiHost.LocalAcknowledger, byId[ackedLocally.AlertId].GetProperty("acknowledgedBy").GetString());
            Assert.False(byId[ackedLocally.AlertId].GetProperty("escalated").GetBoolean());
            Assert.False(byId[silent.AlertId].GetProperty("acknowledged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, byId[silent.AlertId].GetProperty("acknowledgedBy").ValueKind);
            Assert.True(byId[silent.AlertId].GetProperty("escalated").GetBoolean());
            var silentDeliveries = byId[silent.AlertId].GetProperty("deliveries").EnumerateArray().Where(item => item.GetProperty("escalation").GetBoolean()).ToArray();
            Assert.Equal(2, silentDeliveries.Length);
            Assert.All(silentDeliveries, item => Assert.Equal(JsonValueKind.String, item.GetProperty("deliveredAt").ValueKind));
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    [Fact]
    public async Task AuditTrailIsAppendOnlyAtTheDatabaseLevel()
    {
        var desktopRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            await desktop.ControlStore.AppendAuditAsync(new ControlAuditEntry(
                0, DateTimeOffset.UtcNow, "peer-x", "cmd", "resume_collection", "Normal", "applied", "applied", "s", false, string.Empty));
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={desktop.DatabasePath}");
            await connection.OpenAsync();
            foreach (var statement in new[] { "UPDATE control_audit SET outcome = 'forged';", "DELETE FROM control_audit;" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
            }

            Assert.Equal("applied", Assert.Single(await desktop.ControlStore.ReadAuditAsync(10)).Outcome);
        }
        finally
        {
            Cleanup(desktopRoot);
        }
    }

    /// <summary>A second phone that has asked to pair and is waiting for the desktop to approve it.</summary>
    private sealed class Newcomer(Phone phone, Task pairing) : IAsyncDisposable
    {
        public SoftwareIdentity Identity => phone.Identity;

        public Task PairingTask => pairing;

        public ValueTask DisposeAsync() => phone.DisposeAsync();
    }

    private static async Task<Newcomer> PairingNewcomerAsync(DesktopPeer desktop, string pairingId)
    {
        var root = TestDirectories.Create();
        var identity = new SoftwareIdentity();
        var runtime = await MobilePeerRuntime.CreateAsync(Options(root), identity, new ScriptedDeviceReader());
        var pairing = runtime.PairAsync(desktop.Invitation(), pairingId);
        await WaitForAsync(async () => await desktop.PairingStore.ReadPairingAsync(pairingId) is { State: PairingApprovalState.PendingApproval });
        return new Newcomer(new Phone(root, identity, new SoftwarePresenceSigner(), runtime), pairing);
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
