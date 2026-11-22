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

    public Task<HistoryResult> GetHistoryAsync(
        HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize, CancellationToken ct = default) =>
        Task.FromResult(new HistoryResult([], "raw", 1, false));

    public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Task.CompletedTask;

    private static HistoryPointDto Number(string peer, string signal, double value, string unit, DateTimeOffset at, double age) =>
        new(peer, "stream", 1, "peer:" + peer, signal, at.AddSeconds(-age), at.AddSeconds(-age), "fake", "Observed", "Valid", "Fresh",
            age, new ObservationValueDto("number", value, null, null), unit, null);
}

/// <summary>A paired-device source that can also send controls. Its answers are scripted per test; it records what was sent.</summary>
internal sealed class FakeControlEnvironmentSource : FakeEnvironmentSource, IControlSource, IViewingSource
{
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
        Record("interval", peerId);

    public Task<ControlOutcomeDto> AcknowledgeAlertAsync(string peerId, string alertId, CancellationToken ct = default) => Record("ack", peerId);

    public Task<ControlOutcomeDto> MuteAlertsAsync(string peerId, TimeSpan duration, string? ruleId, CancellationToken ct = default) =>
        Record("mute", peerId);

    public Task<ControlOutcomeDto> UnmuteAlertsAsync(string peerId, string? ruleId, CancellationToken ct = default) => Record("unmute", peerId);

    public Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PendingPairingDto>>(PendingPairings.ToArray());

    public Task<ControlOutcomeDto> ResolvePairingAsync(string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default) =>
        Record("pairing", peerId, $"{(approve ? "approve" : "deny")}:{pairingId}:{fingerprint}");

    public Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default) => Record("enroll", peerId);

    public Task<ControlOutcomeDto> SetAlertPreferencesAsync(string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default) =>
        Record("alert-prefs", peerId);

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
