using System.Globalization;
using System.Net;
using MonitorApp.Client.Core;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Transport;

namespace MonitorApp.Client.Mobile;

/// <summary>
/// The phone's data source: reads the in-process <see cref="MobilePeerRuntime"/> and its SQLite store directly (no HTTP),
/// so the shared client shows the phone's own readings and the replicated desktop history together. Live updates are
/// pushed from the store's commit stream; viewing interest goes to the runtime so viewed desktops sample fast.
/// It does not own the runtime (a foreground service does) and never starts or stops it.
/// </summary>
public sealed class InProcessPeerDataSource : IPeerDataSource, IAlertSource, IViewingSource, IPairingInitiator, IQrPairingSource, IHostServiceControl, IControlSource, IDisposable
{
    /// <summary>Quiet period that coalesces a burst of committed records into one <see cref="StateChanged"/>.</summary>
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(150);

    private readonly MobilePeerRuntime runtime;
    private readonly Action? startHost;
    private readonly Action? stopHost;
    private readonly IQrScanner? qrScanner;
    private readonly PeerHistoryQueryService history;
    private readonly CancellationTokenSource stop = new();
    private readonly Action<IReadOnlyList<MonitorApp.Peer.Alerts.InboxAlert>> alertHandler;
    private readonly Task liveLoop;
    private int changePending;

    public InProcessPeerDataSource(
        MobilePeerRuntime runtime, TimeSpan? staleAfter = null, Action? startHost = null, Action? stopHost = null,
        IQrScanner? qrScanner = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        this.runtime = runtime;
        this.startHost = startHost;
        this.stopHost = stopHost;
        this.qrScanner = qrScanner;
        history = new PeerHistoryQueryService(runtime.Store, new SystemPeerClock(), staleAfter ?? TimeSpan.FromMinutes(5));
        alertHandler = _ => RaiseSoon();
        runtime.NewAlerts += alertHandler;
        liveLoop = Task.Run(() => PumpLiveAsync(stop.Token));
    }

    public string Description => "this phone";

    public bool SupportsLiveUpdates => true;

    public event EventHandler? StateChanged;

    public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var status = runtime.GetStatus();
        var peers = await runtime.Store.ReadTrustedPeersAsync(ct).ConfigureAwait(false);
        return new RuntimeDto(
            status.Running ? "Running" : "Stopped",
            "This phone's own readings plus history replicated from paired desktops.",
            new LocalPeerDto(status.PeerId, status.FingerprintHex),
            peers.Count(peer => peer.TrustState == PeerTrustState.Active),
            status.PendingPairingId is null ? 0 : 1);
    });

    public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var peers = await runtime.Store.ReadTrustedPeersAsync(ct).ConfigureAwait(false);
        return (IReadOnlyList<TrustedPeerDto>)peers
            .Select(peer => new TrustedPeerDto(
                peer.PeerId,
                peer.DisplayName,
                Convert.ToHexString(peer.PublicKeyFingerprint),
                peer.TrustState.ToString(),
                peer.PairedAt,
                peer.RevokedAt))
            .ToArray();
    });

    /// <summary>The phone only initiates pairing, so the only pairing it can show is its own in-flight request.</summary>
    public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default)
    {
        var pending = runtime.GetStatus().PendingPairingId;
        IReadOnlyList<PendingPairingDto> result = pending is null
            ? []
            : [new PendingPairingDto(
                pending, "desktop", "Desktop (waiting for its user to approve this phone)", string.Empty,
                "WaitingForDesktop", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        return Task.FromResult(result);
    }

    public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => Guard(async () =>
    {
        var catalog = await history.ReadCatalogAsync(ct).ConfigureAwait(false);
        return new CatalogDto(
            catalog.Subjects
                .Select(subject => new CatalogSubjectDto(
                    subject.SubjectEntityId,
                    subject.Signals
                        .Select(signal => new CatalogSignalDto(
                            signal.SignalId, signal.Unit, signal.Kind, signal.LatestQuality, signal.LatestObservedAt))
                        .ToArray()))
                .ToArray(),
            catalog.OriginPeers);
    });

    public Task<StateDto> GetStateAsync(
        DateTimeOffset? asOf = null,
        int? lookbackSeconds = null,
        string? subjectEntityId = null,
        string? signalId = null,
        CancellationToken ct = default) => Guard(async () =>
    {
        var state = await history.ReconstructAsync(
            asOf?.ToString("O", CultureInfo.InvariantCulture),
            lookbackSeconds,
            subjectEntityId,
            signalId,
            null,
            ct).ConfigureAwait(false);
        return new StateDto(
            state.AsOf, state.LookbackFrom, state.Limit, state.Resolution, state.Truncated,
            state.Points.Select(ToDto).ToArray());
    });

    public async Task<HistoryResult> GetHistoryAsync(
        HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        var points = new List<HistoryPointDto>();
        string? cursor = null;
        var resolution = "raw";
        var pages = 0;
        while (true)
        {
            var page = await Guard(() => history.QueryHistoryAsync(
                query.From.ToString("O", CultureInfo.InvariantCulture),
                query.To.ToString("O", CultureInfo.InvariantCulture),
                query.SubjectEntityId,
                query.SignalId,
                null,
                pageSize,
                cursor,
                ct)).ConfigureAwait(false);
            pages++;
            resolution = page.Resolution;
            points.AddRange(page.Points.Select(ToDto));
            cursor = page.NextCursor;
            if (cursor is null || !page.Truncated)
            {
                return new HistoryResult(points, resolution, pages, MoreAvailable: false);
            }

            if (pages >= maxPages)
            {
                return new HistoryResult(points, resolution, pages, MoreAvailable: true);
            }
        }
    }

    public Task<IReadOnlyList<AlertDto>> GetAlertsAsync(int limit = 100, CancellationToken ct = default) => Guard(async () =>
    {
        var inbox = await runtime.ReadAlertsAsync(Math.Clamp(limit, 1, 500), ct).ConfigureAwait(false);
        return (IReadOnlyList<AlertDto>)inbox
            .Select(item => new AlertDto(
                item.Alert.AlertId,
                item.Alert.Revision,
                item.Alert.RuleId,
                item.Alert.SubjectId,
                item.Alert.OriginPeerId,
                item.Alert.Severity.ToString(),
                item.Alert.State.ToString(),
                item.Alert.Title,
                item.Alert.Detail,
                item.Alert.RaisedAt,
                item.Alert.ResolvedAt,
                Escalated: item.Alert.Escalated))
            .ToArray();
    });

    /// <summary>Asks the hosting service (when the head supplied one) to start collection and syncing.</summary>
    public void RequestStart() => startHost?.Invoke();

    public void RequestStop() => stopHost?.Invoke();

    public IDisposable BeginViewing(string? peerId, IReadOnlyCollection<string>? signalIds = null) => runtime.BeginViewing(peerId, signalIds);

    public async Task<string> PairWithCodeAsync(string address, string code, CancellationToken ct = default)
    {
        if (!IPEndPoint.TryParse(address?.Trim() ?? string.Empty, out var endpoint) || endpoint.Port == 0)
        {
            throw new ApiValidationException("Enter the desktop's private address as host:port, for example 100.64.0.5:55433.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ApiValidationException("Enter the pairing code shown on the desktop.");
        }

        return await PairAsync(() => runtime.PairWithCodeAsync(endpoint.Address, endpoint.Port, code.Trim(), null, ct)).ConfigureAwait(false);
    }

    public async Task<string> PairWithInvitationAsync(string invitation, CancellationToken ct = default)
    {
        PeerPairingInvitation parsed;
        try
        {
            parsed = PeerPairingInvitation.Decode(invitation?.Trim() ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new ApiValidationException(exception.Message);
        }

        return await PairAsync(() => runtime.PairAsync(parsed, null, ct)).ConfigureAwait(false);
    }

    public bool CanScanQr => qrScanner is { IsAvailable: true };

    public async Task<string?> ScanAndPairAsync(CancellationToken ct = default)
    {
        if (qrScanner is not { IsAvailable: true })
        {
            throw new ApiValidationException("This device cannot scan QR codes. Enter the pairing code shown on the desktop instead.");
        }

        string? text;
        try
        {
            text = await qrScanner.ScanAsync(ct).ConfigureAwait(false);
        }
        catch (QrScanUnavailableException exception)
        {
            throw new ApiValidationException(exception.Message + " Enter the pairing code shown on the desktop instead.");
        }

        // null: the person closed the scanner. Not an error and nothing to pair.
        return text is null ? null : await PairWithInvitationAsync(text, ct).ConfigureAwait(false);
    }

    /// <summary>Pairings are decided on the desktop; the phone has nothing to approve.</summary>
    public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) =>
        throw new ApiValidationException("This phone starts pairing; the desktop user approves it on the desktop.");

    public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) =>
        throw new ApiValidationException("This phone starts pairing; the desktop user decides on the desktop.");

    public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => Guard(async () =>
    {
        await runtime.RevokeAsync(peerId, ct).ConfigureAwait(false);
        return true;
    });

    public bool CanVerifyUserPresence => runtime.CanVerifyUserPresence;

    public ControlTierDescription DescribeTier(ControlActionKind action, TimeSpan? duration = null) =>
        ControlMapping.Describe(action, duration, runtime.CanVerifyUserPresence);

    public Task<ControlOutcomeDto> PauseCollectionAsync(string peerId, TimeSpan? duration, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.PauseCollection, duration, () => ControlRequest.PauseCollection(duration), ct);

    public Task<ControlOutcomeDto> ResumeCollectionAsync(string peerId, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.ResumeCollection, null, () => ControlRequest.ResumeCollection(), ct);

    public Task<ControlOutcomeDto> SetSamplingIntervalAsync(string peerId, TimeSpan? interval, TimeSpan? duration, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.SetSamplingInterval, duration, () => ControlRequest.SetSamplingInterval(interval, duration), ct);

    public Task<ControlOutcomeDto> AcknowledgeAlertAsync(string peerId, string alertId, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.AcknowledgeAlert, null, () => ControlRequest.AcknowledgeAlert(alertId), ct);

    public Task<ControlOutcomeDto> MuteAlertsAsync(string peerId, TimeSpan duration, string? ruleId, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.MuteAlerts, duration, () => ControlRequest.MuteAlerts(duration, ruleId), ct);

    public Task<ControlOutcomeDto> UnmuteAlertsAsync(string peerId, string? ruleId, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.UnmuteAlerts, null, () => ControlRequest.UnmuteAlerts(ruleId), ct);

    public Task<ControlOutcomeDto> SetAlertPreferencesAsync(
        string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutedRuleKinds);
        if (!MonitorApp.Peer.Alerts.AlertPreferences.TryParseSeverity(minSeverity, out var severity))
        {
            return Task.FromResult(new ControlOutcomeDto("Refused", "invalid_payload", "The minimum severity is not valid.", false));
        }

        return SendAsync(peerId, ControlActionKind.SetAlertPreferences, null,
            () => ControlRequest.SetAlertPreferences(severity, mutedRuleKinds), ct);
    }

    public Task<ControlOutcomeDto> SetAlertRulesAsync(
        string peerId, string? subjectPeerId, IReadOnlyCollection<AlertRuleOverrideDto> ruleOverrides, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ruleOverrides);
        var inputs = ruleOverrides.Select(item => new MonitorApp.Peer.Alerts.AlertRuleOverrideInput(item.RuleId, item.Enabled, item.Threshold)).ToArray();
        if (MonitorApp.Peer.Alerts.AlertRuleCatalog.Validate(inputs) is { } problem)
        {
            // Rejected locally with the same ranges the desktop enforces, so the person sees why before any biometric prompt.
            return Task.FromResult(new ControlOutcomeDto("Refused", "invalid_payload", problem, false));
        }

        return SendAsync(peerId, ControlActionKind.SetAlertRules, null,
            () => ControlRequest.SetAlertRuleOverrides(subjectPeerId, inputs), ct);
    }

    public async Task<IReadOnlyList<AlertRuleDto>?> ReadAlertRulesAsync(string peerId, CancellationToken ct = default)
    {
        try
        {
            var response = await runtime.ReadAlertRulesAsync(peerId, ct).ConfigureAwait(false);
            if (!response.Permitted)
            {
                return null;
            }

            return response.Rules.Select(item => new AlertRuleDto(
                item.SubjectPeerId, item.RuleId, item.Title, item.Unit, item.DefaultThreshold, item.Threshold, item.Enabled,
                item.Overridden, item.Minimum, item.Maximum, item.UpdatedAt is null ? null : item.UpdatedAt.ToDateTimeOffset())).ToArray();
        }
        catch (MobileControlFailedException)
        {
            return null;
        }
    }

    public async Task<AlertPreferencesDto?> ReadAlertPreferencesAsync(string peerId, CancellationToken ct = default)
    {
        try
        {
            var response = await runtime.ReadAlertPreferencesAsync(peerId, ct).ConfigureAwait(false);
            if (!response.Permitted)
            {
                return null;
            }

            return new AlertPreferencesDto(
                peerId,
                MonitorApp.Peer.Alerts.AlertPreferences.SeverityName(response.MinSeverity),
                response.MutedRuleKinds.ToArray(),
                response.UpdatedAt is null ? null : response.UpdatedAt.ToDateTimeOffset());
        }
        catch (MobileControlFailedException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PendingPairingDto>> ReadPendingPairingsAsync(string peerId, CancellationToken ct = default)
    {
        try
        {
            var pairings = await runtime.ReadPendingPairingsAsync(peerId, ct).ConfigureAwait(false);
            return pairings
                .Select(item => new PendingPairingDto(
                    item.PairingId,
                    item.RemotePeerId,
                    item.DisplayName.Length == 0 ? null : item.DisplayName,
                    Convert.ToHexString(item.RemotePublicKeySha256.ToByteArray()),
                    "AwaitingApproval",
                    item.CreatedAt.ToDateTimeOffset(),
                    item.ExpiresAt.ToDateTimeOffset()))
                .ToArray();
        }
        catch (MobileControlFailedException)
        {
            return [];
        }
    }

    public Task<ControlOutcomeDto> ResolvePairingAsync(
        string peerId, string pairingId, string fingerprint, bool approve, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(fingerprint?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            return Task.FromResult(new ControlOutcomeDto("Refused", "invalid_payload", "The fingerprint is not valid hex.", false));
        }

        var kind = approve ? ControlActionKind.ApprovePairing : ControlActionKind.RejectPairing;
        return SendAsync(peerId, kind, null, () => ControlRequest.ResolvePairing(pairingId, bytes, approve), ct);
    }

    public Task<ControlOutcomeDto> EnrollPresenceKeyAsync(string peerId, CancellationToken ct = default) =>
        SendAsync(peerId, ControlActionKind.EnrollPresenceKey, null, () => ControlRequest.EnrollPresenceKey(), ct);

    public async Task<RemoteUpdateStatusDto?> ReadUpdateStatusAsync(string peerId, CancellationToken ct = default)
    {
        try
        {
            var status = await runtime.ReadUpdateStatusAsync(peerId, ct).ConfigureAwait(false);
            return new RemoteUpdateStatusDto(
                status.Permitted,
                status.Available,
                status.InstalledVersion,
                status.StagedVersion.Length == 0 ? null : status.StagedVersion,
                status.StagedArtifactSha256.Length == 0 ? null : Convert.ToHexString(status.StagedArtifactSha256.Span),
                status.State,
                status.LastError.Length == 0 ? null : status.LastError,
                status.ApprovalPending);
        }
        catch (MobileControlFailedException)
        {
            return null;
        }
    }

    public Task<ControlOutcomeDto> ApproveUpdateAsync(string peerId, string stagedVersion, string stagedDigestHex, CancellationToken ct = default)
    {
        byte[] digest;
        try
        {
            digest = Convert.FromHexString(stagedDigestHex);
        }
        catch (FormatException)
        {
            digest = [];
        }

        if (string.IsNullOrWhiteSpace(stagedVersion) || digest.Length != 32)
        {
            return Task.FromResult(new ControlOutcomeDto("Refused", "invalid_payload", "That update identity is not valid.", true));
        }

        return SendAsync(peerId, ControlActionKind.ApproveUpdate, null, () => ControlRequest.ApproveUpdate(stagedVersion, digest), ct);
    }

    /// <summary>Sends one control; refusals and delivery/biometric failures become an outcome, never an exception.</summary>
    private async Task<ControlOutcomeDto> SendAsync(
        string peerId, ControlActionKind kind, TimeSpan? duration, Func<ControlRequest> build, CancellationToken ct)
    {
        var needsPresence = ControlMapping.Describe(kind, duration, runtime.CanVerifyUserPresence).RequiresPresence;
        try
        {
            var result = await runtime.SendControlAsync(peerId, build(), null, ct).ConfigureAwait(false);
            return ControlMapping.ToOutcome(result, needsPresence);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new ControlOutcomeDto("Refused", "invalid_payload", "That duration is not valid.", needsPresence);
        }
        catch (UserPresenceUnavailableException exception)
        {
            return new ControlOutcomeDto("Failed", "presence_unavailable", exception.Message, needsPresence);
        }
        catch (UserPresenceDeniedException exception)
        {
            return new ControlOutcomeDto("Failed", "presence_denied", exception.Message, needsPresence);
        }
        catch (MobileControlFailedException exception)
        {
            return new ControlOutcomeDto("Failed", "unreachable", exception.Message, needsPresence);
        }
    }

    public void Dispose()
    {
        runtime.NewAlerts -= alertHandler;
        stop.Cancel();
        try
        {
            liveLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop only ends by cancellation; nothing to report on shutdown.
        }

        stop.Dispose();
    }

    private static async Task<string> PairAsync(Func<Task<string>> pair)
    {
        try
        {
            return await pair().ConfigureAwait(false);
        }
        catch (MobilePairingFailedException exception)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (PeerQueryException exception)
        {
            throw new ApiValidationException(exception.Message);
        }
    }

    private static HistoryPointDto ToDto(HistoryPointView point) => new(
        point.OriginPeerId,
        point.OriginStreamId,
        point.Sequence,
        point.SubjectEntityId,
        point.SignalId,
        point.ObservedAt,
        point.ReceivedAt,
        point.Source,
        point.Provenance,
        point.Quality,
        point.Freshness,
        point.AgeSeconds,
        point.Value is null ? null : new ObservationValueDto(point.Value.Kind, point.Value.Number, point.Value.Text, point.Value.Boolean),
        point.Unit,
        point.Detail);

    /// <summary>Consumes the store's commit stream for the life of the source; restarts after a transient failure.</summary>
    private async Task PumpLiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var _ in runtime.LiveObservations(cancellationToken).ConfigureAwait(false))
                {
                    RaiseSoon();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                // Transient store trouble: back off briefly and resubscribe; views also refresh on demand.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseSoon()
    {
        if (Interlocked.Exchange(ref changePending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Coalesce, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref changePending, 0);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }
}
