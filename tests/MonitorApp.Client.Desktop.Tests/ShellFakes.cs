using MonitorApp.Client.Core;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Desktop.Tests;

/// <summary>
/// An in-memory peer: this device plus one paired device ("Home server") with a few readings. It serves the same DTOs the
/// real node does, so the environment is built by the production projector, not by the test.
/// </summary>
internal class FakeEnvironmentSource : IPeerDataSource
{
    public const string LocalId = "peer-local";
    public const string ServerId = "peer-server";
    public const string ServerName = "Home server";

    public string Description => "fake node";

    public bool SupportsLiveUpdates => false;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) =>
        Task.FromResult(new RuntimeDto("Running", "test", new LocalPeerDto(LocalId, "AB"), 1, 0));

    public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrustedPeerDto>>(
            [new TrustedPeerDto(ServerId, ServerName, "CD", "Active", DateTimeOffset.UtcNow.AddDays(-2), null)]);

    public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PendingPairingDto>>([]);

    public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(new CatalogDto([], []));

    public Task<StateDto> GetStateAsync(
        DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null,
        string? signalId = null, CancellationToken ct = default)
    {
        var at = asOf ?? DateTimeOffset.UtcNow;
        var points = new List<HistoryPointDto>
        {
            Number(LocalId, SignalIds.CpuUtilization, 0.31, "Ratio", at, 4),
            Number(ServerId, SignalIds.CpuUtilization, 0.18, "Ratio", at, 6),
            Number(ServerId, SignalIds.MemoryUsed, 12.4e9, "Bytes", at, 6),
            Number(ServerId, SignalIds.MemoryTotal, 32e9, "Bytes", at, 6),
        };
        return Task.FromResult(new StateDto(at, at.AddHours(-1), 1000, "raw", false, points));
    }

    /// <summary>Points served by <see cref="GetHistoryAsync"/>, filtered by the query's signal, subject and time range.</summary>
    public List<HistoryPointDto> History { get; } = [];

    public Task<HistoryResult> GetHistoryAsync(
        HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize, CancellationToken ct = default) =>
        Task.FromResult(new HistoryResult(
            History.Where(p => (query.SignalId is null || p.SignalId == query.SignalId)
                && (query.SubjectEntityId is null || p.SubjectEntityId == query.SubjectEntityId)
                && p.ObservedAt >= query.From && p.ObservedAt <= query.To).ToList(),
            "raw",
            1,
            false));

    /// <summary>A recorded numeric reading as history returns it (test data for sparklines).</summary>
    public static HistoryPointDto Recorded(string subject, string signal, double value, string unit, DateTimeOffset observedAt, ulong sequence) =>
        new(subject, "stream", sequence, subject, signal, observedAt, observedAt, "fake", "Observed", "Valid", "Fresh",
            0, new ObservationValueDto("number", value, null, null), unit, null);

    public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;

    private static HistoryPointDto Number(string peer, string signal, double value, string unit, DateTimeOffset at, double age) =>
        new(peer, "stream", 1, "peer:" + peer, signal, at.AddSeconds(-age), at.AddSeconds(-age), "fake", "Observed", "Valid", "Fresh",
            age, new ObservationValueDto("number", value, null, null), unit, null);
}

/// <summary>Scenes for the Dive built on <see cref="SampleEnvironment"/> (sample data only).</summary>
internal static class DiveScenes
{
    /// <summary>
    /// The sample with the prototype's story: the home server's storage is 94% full (240 GB free of 4 TB) while its Backup fails,
    /// and the laptop is transferring.
    /// </summary>
    public static SceneGraph BackupDiskFull(DateTimeOffset now)
    {
        var scene = SampleEnvironment.Create(now);
        var server = scene.Find(SampleEnvironment.Server)!;
        SceneReading Bytes(string signal, double value) => new(
            SampleEnvironment.Server, signal, SignalCatalog.Describe(signal).Label, SignalCatalog.FormatValue(signal, "Bytes", value, null, null),
            value, "Bytes", "Valid", "Fresh", now.AddSeconds(-6), 6, SampleEnvironment.Server, "sample");
        var readings = server.Layers.Where(l => l.Kind != SceneLayerKind.Storage).SelectMany(l => l.Readings)
            .Concat([Bytes(SignalIds.StorageFree, 0.24e12), Bytes(SignalIds.StorageTotal, 4e12)]);
        var full = server with { Layers = LayerBuilder.Build(readings, new HealthClassifier()) };
        var nodes = scene.Nodes.Select(n => n.Id == server.Id ? full : n.Id == SampleEnvironment.Laptop ? n with { Activity = 0.4 } : n).ToList();
        return scene with { Nodes = nodes };
    }

    /// <summary>
    /// Ten minutes of recorded history behind the sample's sparklines (test data): the server's CPU every 30 s and its network counters,
    /// and the laptop's CPU. Other devices have none, so their sparklines stay hidden.
    /// </summary>
    public static void AddTrendHistory(FakeEnvironmentSource source, DateTimeOffset now)
    {
        ulong seq = 1;
        double sent = 1e9, received = 4e9;
        for (var i = 20; i >= 1; i--)
        {
            var at = now.AddSeconds(-30 * i);
            source.History.Add(FakeEnvironmentSource.Recorded(SampleEnvironment.Server, SignalIds.CpuUtilization, 0.12 + (0.1 * Math.Abs(Math.Sin(i / 3.0))), "Ratio", at, seq++));
            source.History.Add(FakeEnvironmentSource.Recorded(SampleEnvironment.Laptop, SignalIds.CpuUtilization, 0.2 + (0.015 * i), "Ratio", at, seq++));
            sent += 30 * (2e5 + (1.5e5 * Math.Sin(i / 2.0)));
            received += 30 * (9e5 + (6e5 * Math.Cos(i / 4.0)));
            source.History.Add(FakeEnvironmentSource.Recorded(SampleEnvironment.Server, SignalIds.NetworkBytesSent, sent, "Bytes", at, seq++));
            source.History.Add(FakeEnvironmentSource.Recorded(SampleEnvironment.Server, SignalIds.NetworkBytesReceived, received, "Bytes", at, seq++));
        }
    }
}

/// <summary>A paired-device source that can also send controls. Its answers are scripted per test; it records what was sent.</summary>
internal sealed class FakeControlEnvironmentSource : FakeEnvironmentSource, IControlSource, IViewingSource, IAlertSource
{
    /// <summary>One critical alert on the server, raised 5 h ago and resolved 4 h ago, plus any <see cref="ExtraAlerts"/>.</summary>
    public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AlertDto>>(
        [
            new AlertDto("a1", 1, "cpu-high", "peer:" + ServerId, ServerId, "Critical", "Resolved", "CPU saturated", "CPU at 99%",
                DateTimeOffset.UtcNow.AddHours(-5), DateTimeOffset.UtcNow.AddHours(-4)),
            .. ExtraAlerts,
        ]);

    public List<AlertDto> ExtraAlerts { get; } = [];

    /// <summary>Viewing interest as it happened: "begin:peer:signals" and "end:peer" (peer "local" for this device).</summary>
    public List<string> Viewing { get; } = [];

    public IDisposable BeginViewing(string? peerId, IReadOnlyCollection<string>? signalIds = null)
    {
        var peer = peerId ?? "local";
        Viewing.Add($"begin:{peer}:{string.Join(',', signalIds ?? [])}");
        return new Scope(() => Viewing.Add("end:" + peer));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }

    public ControlOutcomeDto Outcome { get; set; } = new("Applied", "applied", "Collection paused", false);

    public List<string> Sent { get; } = [];

    public RemoteUpdateStatusDto? UpdateStatus { get; set; }

    public List<PendingPairingDto> PendingPairings { get; } = [];

    public bool CanVerifyUserPresence => true;

    public ControlTierDescription DescribeTier(ControlActionKind action, TimeSpan? duration = null) => action switch
    {
        ControlActionKind.PauseCollection or ControlActionKind.ApproveUpdate or ControlActionKind.ApprovePairing => new ControlTierDescription(ControlTierDto.Elevated, true, true),
        _ => new ControlTierDescription(ControlTierDto.Normal, false, true),
    };

    public Task<ControlOutcomeDto> PauseCollectionAsync(string peerId, TimeSpan? duration, CancellationToken ct = default) =>
        Record("pause", peerId);

    public Task<ControlOutcomeDto> ResumeCollectionAsync(string peerId, CancellationToken ct = default) => Record("resume", peerId);

    public Task<ControlOutcomeDto> SetSamplingIntervalAsync(string peerId, TimeSpan? interval, TimeSpan? duration, CancellationToken ct = default) =>
        Record("interval", peerId, interval is { } i ? i.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s" : "default");

    public Task<ControlOutcomeDto> AcknowledgeAlertAsync(string peerId, string alertId, CancellationToken ct = default) => Record("ack", peerId, alertId);

    public Task<ControlOutcomeDto> MuteAlertsAsync(string peerId, TimeSpan duration, string? ruleId, CancellationToken ct = default) =>
        Record("mute", peerId, ruleId is null ? null : $"{ruleId}:{duration.TotalHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}h");

    public Task<ControlOutcomeDto> UnmuteAlertsAsync(string peerId, string? ruleId, CancellationToken ct = default) => Record("unmute", peerId);

    /// <summary>When set, only this peer holds <see cref="PendingPairings"/>; otherwise every peer reports them.</summary>
    public string? PairingsHeldBy { get; set; }

    public Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PendingPairingDto>>(
            PairingsHeldBy is null || PairingsHeldBy == peerId ? PendingPairings.ToArray() : []);

    public Task<ControlOutcomeDto> ResolvePairingAsync(string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default) =>
        Record("pairing", peerId, $"{(approve ? "approve" : "deny")}:{pairingId}:{fingerprint}");

    public Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default) => Record("enroll", peerId);

    public Task<ControlOutcomeDto> SetAlertPreferencesAsync(string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default) =>
        Record("alert-prefs", peerId, $"{minSeverity ?? "All"}:{string.Join(',', mutedRuleKinds)}");

    public AlertPreferencesDto? StoredAlertPreferences { get; set; }

    public Task<IReadOnlyList<AlertRuleDto>?> ReadAlertRulesAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AlertRuleDto>?>(null);

    public Task<ControlOutcomeDto> SetAlertRulesAsync(string peerId, string? minSeverity, IReadOnlyCollection<AlertRuleOverrideDto> overrides, CancellationToken ct = default) =>
        Task.FromResult(new ControlOutcomeDto("Refused", "not_permitted", "not in this fake", false));

    public Task<AlertPreferencesDto?> ReadAlertPreferencesAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult(StoredAlertPreferences);

    public Task<RemoteUpdateStatusDto?> ReadUpdateStatusAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult(UpdateStatus);

    public Task<ControlOutcomeDto> ApproveUpdateAsync(string peerId, string stagedVersion, string stagedDigestHex, CancellationToken ct = default) =>
        Record("update", peerId, $"{stagedVersion}:{stagedDigestHex}");

    private Task<ControlOutcomeDto> Record(string what, string peerId, string? detail = null)
    {
        Sent.Add(detail is null ? what + ":" + peerId : what + ":" + peerId + ":" + detail);
        return Task.FromResult(Outcome);
    }
}
