using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>The Dive as a person uses it, over the sample environment: Home tiles, the dive to the cause, climbing out, parts, actions, history.</summary>
public sealed class DiveViewModelTests
{
    [Fact]
    public async Task HomeTilesDiveToTheCauseClimbOutActAndStopInHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new ControlFake();
        using var model = new MainViewModel(source);
        model.Environment.ShowScene(SampleEnvironment.Create(now));
        var dive = model.Shell.Dive;
        var moves = new List<DiveDirection>();
        dive.Navigated += (_, e) => moves.Add(e.Direction);

        // Home: the attention tile first, then one tile per top-level device, faults first, then offline, stale and the rest.
        Assert.True(dive.IsHome && dive.Page is DiveHomePage, "starts at Home");
        Assert.True(model.Shell.IsDive, "the ecosystem opens as the Dive");
        var attention = Assert.IsType<DiveAttentionTile>(dive.Home.Tiles[0]);
        var devices = dive.Home.Tiles.OfType<DiveDeviceTile>().ToList();
        Assert.True(
            devices.Select(t => t.NodeId).SequenceEqual([SampleEnvironment.Server, SampleEnvironment.Phone, SampleEnvironment.Fedora, SampleEnvironment.Tablet, SampleEnvironment.Laptop]),
            "tile order was: " + string.Join(", ", devices.Select(t => t.Title)));
        var fedora = devices.Single(t => t.NodeId == SampleEnvironment.Fedora);
        var fedoraSeen = DiveText.Clock(model.Environment.Scene.Find(SampleEnvironment.Fedora)!.LastSeen);
        Assert.True(
            fedora.Badge == new DiveBadge($"Offline since {fedoraSeen}", SceneHealth.Offline) && fedora.Headline == "--" && !fedora.HasGauges,
            $"offline tile says so in words and hides old gauges: {fedora.Badge} / {fedora.Headline} / {fedora.Gauges.Count}");
        var tablet = devices.Single(t => t.NodeId == SampleEnvironment.Tablet);
        Assert.True(tablet.Badge.Health == SceneHealth.Stale && tablet.Badge.Text.StartsWith("Stale · last seen ", StringComparison.Ordinal), tablet.Badge.Text);
        var server = devices[0];
        Assert.True(
            server.Badge.Text == "2 problems inside" && server.Gauges.Select(g => g.Label).SequenceEqual(["CPU", "Memory", "Storage"])
                && server.Gauges[0].Value == "18" && server.ChildrenText == "3 services · 2 need attention",
            $"server tile: {server.Badge.Text}; {string.Join(",", server.Gauges.Select(g => g.Label + "=" + g.Value))}; {server.ChildrenText}");
        Assert.True(attention.Headline == "5 need attention" && attention.Breakdown == "3 faults · 1 offline · 1 stale", $"{attention.Headline} / {attention.Breakdown}");

        // "Dive to the cause": Media server depends on the failed Backup, so the dive lands on Backup, with the real reason.
        Assert.Equal(SampleEnvironment.Backup, attention.Cause?.NodeId);
        dive.OpenCommand.Execute(attention);
        Assert.True(dive.NodeId == SampleEnvironment.Backup && model.Environment.SelectedNodeId == SampleEnvironment.Backup, $"at {dive.NodeId}");
        Assert.Equal(["Home", "Home server", "Backup"], dive.Crumbs.Select(c => c.Text));
        Assert.Equal(["Home", "Home server", "Backup"], model.Shell.Breadcrumbs.Select(c => c.Text));
        var backup = Assert.IsType<DiveDevicePage>(dive.Page);
        Assert.Equal("Backup: Last backup failed 12 min ago", backup.Problem?.Text);
        Assert.False(backup.HasInvestigate, "nothing deeper to investigate inside Backup itself, and its host reports no other fault");
        Assert.Equal(["Hosted on Home server", "Needed by Media server"], backup.Relations.Select(r => r.Text));

        // Climbing out: Backup -> Home server (chambers say where the cause is) -> Home; Up at Home does nothing.
        Assert.True(dive.Up());
        var serverPage = Assert.IsType<DiveDevicePage>(dive.Page);
        Assert.Equal(SampleEnvironment.Server, serverPage.NodeId);
        Assert.Equal("Cause inside", serverPage.Chambers.Single(c => c.NodeId == SampleEnvironment.Backup).Hint);
        Assert.Contains("Reported by the device itself", serverPage.Provenance, StringComparison.Ordinal);

        // A gauge dives into its layer as a part; the breadcrumb names the part.
        dive.OpenPartCommand.Execute(serverPage.Gauges.Single(g => g.Label == "Storage"));
        var part = Assert.IsType<DivePartPage>(dive.Page);
        Assert.Equal(["Home", "Home server", "Storage"], dive.Crumbs.Select(c => c.Text));
        Assert.Contains("61% full", part.Statement, StringComparison.Ordinal);
        Assert.Equal(["Media server: Degraded: depends on Backup, which failed", "Backup: Last backup failed 12 min ago"], part.Related.Select(p => p.Text));
        Assert.True(dive.Up() && dive.Up() && dive.IsHome && !dive.Up(), "part -> device -> Home, then nothing to climb");
        Assert.Equal([DiveDirection.In, DiveDirection.Out, DiveDirection.In, DiveDirection.Out, DiveDirection.Out], moves);

        // A selection made elsewhere (search, timeline, outline) moves the Dive too.
        model.Environment.SelectedNodeId = SampleEnvironment.Phone;
        Assert.Equal(SampleEnvironment.Phone, dive.NodeId);
        dive.GoHome();

        // A full disk is the deepest fault: the dive goes straight to the part, and the device page offers Investigate.
        model.Environment.ShowScene(WithServerStorage(model.Environment.Scene, free: 0.24e12, total: 4e12));
        dive.DiveToCause();
        Assert.True(dive.NodeId == SampleEnvironment.Server && dive.LayerId == "storage", $"cause at {dive.NodeId}/{dive.LayerId}");
        var full = Assert.IsType<DivePartPage>(dive.Page);
        Assert.True(full.IsCritical && full.Badge == new DiveBadge("94% full", SceneHealth.Degraded, IsFault: true), full.Badge.ToString());
        dive.GoTo(SampleEnvironment.Backup);
        var lead = Assert.IsType<DiveDevicePage>(dive.Page);
        Assert.True(
            lead.Investigate is { NodeId: SampleEnvironment.Server, LayerId: "storage" } && lead.LeadText == "Also on Home server: Home server storage 94% full",
            $"Backup leads to its host's full disk as a fact: {lead.Investigate} / {lead.LeadText}");

        // Actions on the device reuse the controls: live, they go to the device; pending approvals appear on Home.
        source.Pending.Add(new PendingPairingDto("p1", "peer-watch", "Pixel Watch", "AB12CD34", "PendingApproval", now, now.AddMinutes(5)));
        dive.GoTo(SampleEnvironment.Server);
        Assert.True(dive.CanAct && dive.Controls.IsAvailable, "a live paired device can be acted on");
        await dive.Controls.RunCommand.ExecuteAsync(dive.Controls.Actions.Single(a => a.Kind == ControlActionKind.PauseCollection));
        Assert.Equal([$"pause:{SampleEnvironment.Server}"], source.Sent);
        await dive.RefreshApprovalsAsync();
        var approval = dive.Home.Tiles.OfType<DiveApprovalTile>().Single();
        Assert.True(approval.HostNodeId == SampleEnvironment.Server && approval.TierText.Contains("asks for your fingerprint", StringComparison.Ordinal), approval.ToString());
        dive.GoHome();
        dive.OpenCommand.Execute(approval);
        Assert.Equal(SampleEnvironment.Server, dive.NodeId);

        // History: recorded words, actions off with the reason, no approvals offered.
        model.Environment.AsOf = now.AddHours(-1);
        Assert.True(dive.IsHistorical && !dive.CanAct, "recorded state disables actions");
        Assert.StartsWith("RECORDED · as of ", dive.RecordedText, StringComparison.Ordinal);
        Assert.Contains("Return to live to act", DiveViewModel.RecordedNotice, StringComparison.Ordinal);
        Assert.Contains("Return to live to act", dive.Controls.HistoryNotice, StringComparison.Ordinal);
        Assert.False(dive.Controls.RunCommand.CanExecute(dive.Controls.Actions[0]), "the pause command refuses in history");
        Assert.Empty(dive.Home.Tiles.OfType<DiveApprovalTile>());
        Assert.Single(source.Sent);
    }

    private static SceneGraph WithServerStorage(SceneGraph scene, double free, double total)
    {
        var server = scene.Find(SampleEnvironment.Server)!;
        SceneReading Bytes(string signal, double value) => new(
            server.Id, signal, SignalCatalog.Describe(signal).Label, SignalCatalog.FormatValue(signal, "Bytes", value, null, null),
            value, "Bytes", "Valid", "Fresh", scene.AsOf, 6, server.Id, "sample");
        var readings = server.Layers.Where(l => l.Kind != SceneLayerKind.Storage).SelectMany(l => l.Readings)
            .Concat([Bytes(SignalIds.StorageFree, free), Bytes(SignalIds.StorageTotal, total)]);
        var updated = server with { Layers = LayerBuilder.Build(readings, new HealthClassifier()) };
        return scene with { Nodes = scene.Nodes.Select(n => n.Id == server.Id ? updated : n).ToList() };
    }

    /// <summary>A source that can send controls; it records what was sent and holds pending pairings on the sample server.</summary>
    private sealed class ControlFake : IPeerDataSource, IControlSource
    {
        private static readonly ControlOutcomeDto Applied = new("Applied", "applied", "done", false);

        public List<string> Sent { get; } = [];

        public List<PendingPairingDto> Pending { get; } = [];

        public string Description => "fake";

        public bool SupportsLiveUpdates => false;

        public bool CanVerifyUserPresence => true;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) =>
            Task.FromResult(new RuntimeDto("Running", "test", new LocalPeerDto(SampleEnvironment.Laptop, "AB"), 1, 0));

        public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TrustedPeerDto>>([]);

        public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingPairingDto>>([]);

        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(new CatalogDto([], []));

        public Task<StateDto> GetStateAsync(
            DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null, string? signalId = null, CancellationToken ct = default) =>
            Task.FromResult(new StateDto(asOf ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "raw", false, []));

        public Task<HistoryResult> GetHistoryAsync(
            HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize, CancellationToken ct = default) =>
            Task.FromResult(new HistoryResult([], "raw", 1, false));

        public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;

        public ControlTierDescription DescribeTier(ControlActionKind action, TimeSpan? duration = null) =>
            action is ControlActionKind.PauseCollection or ControlActionKind.ApprovePairing or ControlActionKind.ApproveUpdate
                ? new ControlTierDescription(ControlTierDto.Elevated, true, true)
                : new ControlTierDescription(ControlTierDto.Normal, false, true);

        public Task<ControlOutcomeDto> PauseCollectionAsync(string peerId, TimeSpan? duration, CancellationToken ct = default) => Record("pause", peerId);

        public Task<ControlOutcomeDto> ResumeCollectionAsync(string peerId, CancellationToken ct = default) => Record("resume", peerId);

        public Task<ControlOutcomeDto> SetSamplingIntervalAsync(string peerId, TimeSpan? interval, TimeSpan? duration, CancellationToken ct = default) =>
            Record("interval", peerId);

        public Task<ControlOutcomeDto> AcknowledgeAlertAsync(string peerId, string alertId, CancellationToken ct = default) => Record("ack", peerId);

        public Task<ControlOutcomeDto> MuteAlertsAsync(string peerId, TimeSpan duration, string? ruleId, CancellationToken ct = default) => Record("mute", peerId);

        public Task<ControlOutcomeDto> UnmuteAlertsAsync(string peerId, string? ruleId, CancellationToken ct = default) => Record("unmute", peerId);

        public Task<ControlOutcomeDto> SetAlertPreferencesAsync(
            string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default) => Record("prefs", peerId);

        public Task<AlertPreferencesDto?> ReadAlertPreferencesAsync(string peerId, CancellationToken ct = default) => Task.FromResult<AlertPreferencesDto?>(null);

        public Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PendingPairingDto>>(peerId == SampleEnvironment.Server ? Pending.ToArray() : []);

        public Task<ControlOutcomeDto> ResolvePairingAsync(string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default) =>
            Record("pairing", peerId);

        public Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default) => Record("enroll", peerId);

        public Task<RemoteUpdateStatusDto?> ReadUpdateStatusAsync(string peerId, CancellationToken ct = default) =>
            Task.FromResult<RemoteUpdateStatusDto?>(null);

        public Task<ControlOutcomeDto> ApproveUpdateAsync(string peerId, string stagedVersion, string stagedDigestHex, CancellationToken ct = default) =>
            Record("update", peerId);

        private Task<ControlOutcomeDto> Record(string what, string peerId)
        {
            Sent.Add(what + ":" + peerId);
            return Task.FromResult(Applied);
        }
    }
}
