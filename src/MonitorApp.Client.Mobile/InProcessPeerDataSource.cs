using System.Globalization;
using System.Net;
using MonitorApp.Client.Core;
using MonitorApp.Peer.Application;
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
public sealed class InProcessPeerDataSource : IPeerDataSource, IAlertSource, IViewingSource, IPairingInitiator, IQrPairingSource, IHostServiceControl, IDisposable
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
                item.Alert.ResolvedAt))
            .ToArray();
    });

    /// <summary>Asks the hosting service (when the head supplied one) to start collection and syncing.</summary>
    public void RequestStart() => startHost?.Invoke();

    public void RequestStop() => stopHost?.Invoke();

    public IDisposable BeginViewing(string? peerId) => runtime.BeginViewing(peerId);

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
