using MonitorApp.Client.Core;
using MonitorApp.Client.Mobile;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Mobile.Tests;
using Xunit;

namespace MonitorApp.Client.Mobile.Tests;

/// <summary>
/// The shared client's control data layer over a real phone runtime and a real desktop peer on loopback gRPC with
/// file-backed SQLite and the real control service. Only the biometric prompt is scripted.
/// </summary>
public sealed class ControlSourceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static MobilePeerOptions Options(string directory) => new(
        directory, "control-source-phone", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private static async Task PairAsync(MobilePeerRuntime phone, DesktopPeer desktop, string pairingId)
    {
        var pairTask = phone.PairAsync(desktop.Invitation(), pairingId);
        await desktop.ApproveWhenPendingAsync(pairingId);
        await pairTask.WaitAsync(Deadline);
    }

    private static void Cleanup(params string[] roots)
    {
        foreach (var root in roots)
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

    [Fact]
    public async Task UngrantedIsNotPermittedThenGrantedPauseAndResumeApplyOnTheDesktop()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            using var presence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader(), presence);
            using var source = new InProcessPeerDataSource(phone);
            var control = Assert.IsAssignableFrom<IControlSource>(source);
            await PairAsync(phone, desktop, "ctl-src-1");
            var desktopId = desktop.Identity.PeerId;
            Assert.True(control.CanVerifyUserPresence);

            // Default is deny: a freshly paired phone may do nothing until the owner grants it on the computer.
            var denied = await control.PauseCollectionAsync(desktopId, TimeSpan.FromMinutes(10));
            Assert.Equal("Refused", denied.Status);
            Assert.Equal("not_permitted", denied.ReasonCode);
            Assert.False(denied.IsApplied);
            Assert.False(desktop.Monitoring.IsPaused("monitor.system"));
            Assert.False((await control.ReadUpdateStatusAsync(desktopId))!.Permitted);
            Assert.Null(await control.ReadAlertPreferencesAsync(desktopId));

            await desktop.ControlStore.SetGrantAsync(
                new ControlGrant(phone.Identity.PeerId, MonitoringControls: true, PairingApproval: true, null), DateTimeOffset.UtcNow);

            var defaults = await control.ReadAlertPreferencesAsync(desktopId);
            Assert.NotNull(defaults);
            Assert.Equal("All", defaults.MinSeverity);
            Assert.Empty(defaults.MutedRuleKinds);
            Assert.Null(defaults.UpdatedAt);
            Assert.True((await control.SetAlertPreferencesAsync(desktopId, "Warning", ["threshold.cpu_high"])).IsApplied);
            var saved = await control.ReadAlertPreferencesAsync(desktopId);
            Assert.NotNull(saved);
            Assert.Equal("Warning", saved.MinSeverity);
            Assert.Equal(["threshold.cpu_high"], saved.MutedRuleKinds);
            Assert.NotNull(saved.UpdatedAt);

            var paused = await control.PauseCollectionAsync(desktopId, TimeSpan.FromMinutes(10));
            Assert.True(paused.IsApplied, paused.ReasonCode);
            Assert.False(paused.RequiresPresence);
            Assert.True(desktop.Monitoring.IsPaused("monitor.system"));

            var resumed = await control.ResumeCollectionAsync(desktopId);
            Assert.True(resumed.IsApplied, resumed.ReasonCode);
            Assert.False(desktop.Monitoring.IsPaused("monitor.system"));
            Assert.Equal(0, presence.Prompts);

            var interval = await control.SetSamplingIntervalAsync(desktopId, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
            Assert.True(interval.IsApplied, interval.ReasonCode);
            Assert.Equal(TimeSpan.FromSeconds(30), desktop.Monitoring.IntervalOverride);

            // Out-of-bounds input is the peer's refusal, delivered as an outcome rather than an exception.
            var tooFast = await control.SetSamplingIntervalAsync(desktopId, TimeSpan.FromSeconds(1), null);
            Assert.Equal("Refused", tooFast.Status);
            Assert.Equal("invalid_payload", tooFast.ReasonCode);

            var prefs = await control.SetAlertPreferencesAsync(desktopId, "Warning", ["threshold.cpu_high"]);
            Assert.True(prefs.IsApplied, prefs.ReasonCode);
            var stored = Assert.Single(await desktop.Alerts.ReadPreferencesAsync());
            Assert.Equal(phone.Identity.PeerId, stored.RecipientPeerId);
            Assert.Equal(["threshold.cpu_high"], stored.MutedRuleKinds);
            Assert.Equal("invalid_payload", (await control.SetAlertPreferencesAsync(desktopId, "loud", [])).ReasonCode);

            // No pairing is pending, so the read is empty; the grant allows the read itself.
            Assert.Empty(await control.ReadPendingPairingsAsync(desktopId));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task ElevatedCommandWithoutBiometricsAndUnknownPeerBecomeOutcomesNotExceptions()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());
            using var source = new InProcessPeerDataSource(phone);
            await PairAsync(phone, desktop, "ctl-src-2");
            await desktop.ControlStore.SetGrantAsync(
                new ControlGrant(phone.Identity.PeerId, MonitoringControls: true, PairingApproval: true, null), DateTimeOffset.UtcNow);

            Assert.False(source.CanVerifyUserPresence);
            var description = source.DescribeTier(ControlActionKind.PauseCollection, TimeSpan.FromHours(2));
            Assert.True(description.RequiresPresence);
            Assert.False(description.PresenceAvailable);

            var longPause = await source.PauseCollectionAsync(desktop.Identity.PeerId, TimeSpan.FromHours(2));
            Assert.Equal("Failed", longPause.Status);
            Assert.Equal("presence_unavailable", longPause.ReasonCode);
            Assert.True(longPause.RequiresPresence);
            Assert.False(desktop.Monitoring.IsPaused("monitor.system"));

            var unknown = await source.ResumeCollectionAsync("peer-not-trusted");
            Assert.Equal("Failed", unknown.Status);
            Assert.Equal("unreachable", unknown.ReasonCode);
            Assert.Empty(await source.ReadPendingPairingsAsync("peer-not-trusted"));

            var badFingerprint = await source.ResolvePairingAsync(desktop.Identity.PeerId, "p", "not-hex", approve: true);
            Assert.Equal("invalid_payload", badFingerprint.ReasonCode);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task RemoteAlertRuleEditsAreDeniedByDefaultElevatedValidatedAndAuditedThroughTheControlSource()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            using var presence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader(), presence);
            using var source = new InProcessPeerDataSource(phone);
            var control = Assert.IsAssignableFrom<IControlSource>(source);
            await PairAsync(phone, desktop, "ctl-rules");
            var desktopId = desktop.Identity.PeerId;
            var phoneId = phone.Identity.PeerId;
            AlertRuleOverrideDto[] tight = [new("threshold.cpu_high", true, 70), new("threshold.memory_high", false, null)];

            async Task<string> DiagnosticsAsync()
            {
                var audit = await desktop.ControlStore.ReadAuditAsync(50);
                var stored = await desktop.Alerts.ReadRuleOverridesAsync();
                return "stored overrides: [" + string.Join("; ", stored.Select(item => $"{item.PeerId}:{item.RuleId}:{item.Enabled}:{item.Threshold}")) +
                    "] audit: [" + string.Join("; ", audit.Where(item => item.Action.Contains("rule", StringComparison.Ordinal))
                        .Select(item => $"{item.Action}/{item.Outcome}/{item.ReasonCode}/presence={item.PresenceVerified}")) + "]";
            }

            // Default is deny: no grant means no write and no read, and nothing is stored.
            var denied = await control.SetAlertRulesAsync(desktopId, null, tight);
            Assert.True(denied is { Status: "Refused", ReasonCode: "not_permitted" }, $"ungranted set: {denied.Status}/{denied.ReasonCode}. {await DiagnosticsAsync()}");
            Assert.Null(await control.ReadAlertRulesAsync(desktopId));
            Assert.Empty(await desktop.Alerts.ReadRuleOverridesAsync());

            await desktop.ControlStore.SetGrantAsync(new ControlGrant(phoneId, MonitoringControls: true, PairingApproval: false, null), DateTimeOffset.UtcNow);

            // Granted is still not enough for an action that changes what fires for every recipient: it is elevated and needs the enrolled biometric key.
            Assert.True(control.DescribeTier(ControlActionKind.SetAlertRules).RequiresPresence);
            var unenrolled = await control.SetAlertRulesAsync(desktopId, null, tight);
            Assert.True(unenrolled is { Status: "Refused", ReasonCode: "presence_not_enrolled" }, $"unenrolled set: {unenrolled.Status}/{unenrolled.ReasonCode}. {await DiagnosticsAsync()}");
            Assert.Empty(await desktop.Alerts.ReadRuleOverridesAsync());

            Assert.Equal("PendingLocalApproval", (await control.EnrollPresenceKeyAsync(desktopId)).Status);
            var enrollment = Assert.Single(await desktop.ControlStore.ReadEnrollmentsAsync(), item => item.PeerId == phoneId && item.State == PresenceEnrollmentState.Pending);
            await desktop.Control.DecideEnrollmentAsync(enrollment.EnrollmentId, approve: true, default);

            // The read shows the built-in defaults before any edit.
            var before = await control.ReadAlertRulesAsync(desktopId);
            Assert.NotNull(before);
            Assert.Contains(before, rule => rule.SubjectPeerId == desktopId && rule.RuleId == "threshold.cpu_high" && rule.Threshold == 90 && !rule.Overridden);
            Assert.Contains(before, rule => rule.SubjectPeerId == phoneId && rule.RuleId == "peer.silent" && rule.Threshold == 10 && rule.Maximum == 10080);

            // Invalid ranges: refused before any prompt on the phone, and refused by the desktop when a modified client sends them anyway.
            var promptsBefore = presence.Prompts;
            var tooHigh = await control.SetAlertRulesAsync(desktopId, null, [new("threshold.cpu_high", true, 100)]);
            Assert.True(tooHigh is { Status: "Refused", ReasonCode: "invalid_payload" }, $"client range check: {tooHigh.Status}/{tooHigh.ReasonCode}");
            Assert.Equal(promptsBefore, presence.Prompts);
            var forced = await phone.SendControlAsync(desktopId, ControlRequest.SetAlertRuleOverrides(null, [new AlertRuleOverrideInput("threshold.cpu_high", true, 100)]));
            Assert.True(forced.ReasonCode == "invalid_payload", $"server range check: {forced.Status}/{forced.ReasonCode}. {await DiagnosticsAsync()}");
            var tooSoon = await phone.SendControlAsync(desktopId, ControlRequest.SetAlertRuleOverrides(phoneId, [new AlertRuleOverrideInput("peer.silent", true, 0.5)]));
            Assert.Equal("invalid_payload", tooSoon.ReasonCode);
            // A signal rule cannot be set for another peer (its owner evaluates it), and peer-not-seen needs an active trusted peer.
            var foreign = await control.SetAlertRulesAsync(desktopId, phoneId, [new("threshold.cpu_high", true, 70)]);
            Assert.True(foreign is { Status: "Refused", ReasonCode: "invalid_payload" }, $"foreign subject: {foreign.Status}/{foreign.ReasonCode}. {await DiagnosticsAsync()}");
            var stranger = await control.SetAlertRulesAsync(desktopId, "peer-stranger", [new("peer.silent", true, 5)]);
            Assert.Equal("invalid_payload", stranger.ReasonCode);
            Assert.Empty(await desktop.Alerts.ReadRuleOverridesAsync());

            // A valid edit is applied with a verified biometric, stored durably per peer, and audited as elevated.
            var applied = await control.SetAlertRulesAsync(desktopId, null, tight);
            Assert.True(applied.IsApplied && applied.RequiresPresence, $"valid set: {applied.Status}/{applied.ReasonCode}. {await DiagnosticsAsync()}");
            var silent = await control.SetAlertRulesAsync(desktopId, phoneId, [new("peer.silent", true, 3)]);
            Assert.True(silent.IsApplied, $"peer.silent set: {silent.Status}/{silent.ReasonCode}. {await DiagnosticsAsync()}");
            var stored = await desktop.Alerts.ReadRuleOverridesAsync();
            Assert.Contains(stored, item => item.PeerId == desktopId && item.RuleId == "threshold.cpu_high" && item.Enabled && item.Threshold == 70);
            Assert.Contains(stored, item => item.PeerId == desktopId && item.RuleId == "threshold.memory_high" && !item.Enabled && item.Threshold is null);
            Assert.Contains(stored, item => item.PeerId == phoneId && item.RuleId == "peer.silent" && item.Threshold == 3);

            var after = await control.ReadAlertRulesAsync(desktopId);
            Assert.NotNull(after);
            Assert.Contains(after, rule => rule.RuleId == "threshold.cpu_high" && rule.Threshold == 70 && rule.Overridden && rule.Enabled);
            Assert.Contains(after, rule => rule.RuleId == "threshold.memory_high" && !rule.Enabled && rule.Overridden);
            Assert.Contains(after, rule => rule.SubjectPeerId == phoneId && rule.RuleId == "peer.silent" && rule.Threshold == 3);

            var audit = await desktop.ControlStore.ReadAuditAsync(100);
            Assert.Contains(audit, item => item.Action == "set_alert_rule_overrides" && item.Outcome == "applied" && item.PresenceVerified && item.Tier == "Elevated");
            Assert.Contains(audit, item => item.Action == "set_alert_rule_overrides" && item.Outcome == "refused" && item.ReasonCode == "not_permitted");
            Assert.Contains(audit, item => item.Action == "set_alert_rule_overrides" && item.Outcome == "refused" && item.ReasonCode == "presence_not_enrolled");

            // An empty list restores the defaults for that peer.
            Assert.True((await control.SetAlertRulesAsync(desktopId, null, [])).IsApplied);
            Assert.DoesNotContain(await desktop.Alerts.ReadRuleOverridesAsync(), item => item.PeerId == desktopId);

            // The grant is revocable: after it is withdrawn the same call is refused again and the stored overrides stay as they were.
            await desktop.ControlStore.SetGrantAsync(new ControlGrant(phoneId, MonitoringControls: false, PairingApproval: false, null), DateTimeOffset.UtcNow);
            var revoked = await control.SetAlertRulesAsync(desktopId, phoneId, [new("peer.silent", true, 9)]);
            Assert.Equal("not_permitted", revoked.ReasonCode);
            Assert.Null(await control.ReadAlertRulesAsync(desktopId));
            Assert.Equal(3, Assert.Single(await desktop.Alerts.ReadRuleOverridesAsync()).Threshold);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Theory]
    [InlineData(ControlActionKind.PauseCollection, 600, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.PauseCollection, 3600, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.PauseCollection, 7200, ControlTierDto.Elevated)]
    [InlineData(ControlActionKind.PauseCollection, null, ControlTierDto.Elevated)]
    [InlineData(ControlActionKind.MuteAlerts, 3600, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.MuteAlerts, 172800, ControlTierDto.Elevated)]
    [InlineData(ControlActionKind.ResumeCollection, null, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.AcknowledgeAlert, null, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.ApprovePairing, null, ControlTierDto.High)]
    [InlineData(ControlActionKind.RejectPairing, null, ControlTierDto.Normal)]
    [InlineData(ControlActionKind.EnrollPresenceKey, null, ControlTierDto.High)]
    [InlineData(ControlActionKind.ApproveUpdate, null, ControlTierDto.High)]
    public async Task DescribeTierMatchesThePeerRiskPolicyBeforeAnythingIsSent(
        ControlActionKind action, int? seconds, ControlTierDto expected)
    {
        var phoneRoot = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            using var presence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader(), presence);
            using var source = new InProcessPeerDataSource(phone);

            var description = source.DescribeTier(action, seconds is { } value ? TimeSpan.FromSeconds(value) : null);
            Assert.Equal(expected, description.Tier);
            Assert.Equal(expected != ControlTierDto.Normal, description.RequiresPresence);
            Assert.True(description.PresenceAvailable);
        }
        finally
        {
            Cleanup(phoneRoot);
        }
    }
}
