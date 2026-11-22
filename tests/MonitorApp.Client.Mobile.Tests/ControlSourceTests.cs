using MonitorApp.Client.Core;
using MonitorApp.Client.Mobile;
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
