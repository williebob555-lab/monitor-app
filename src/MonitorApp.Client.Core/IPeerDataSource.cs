namespace MonitorApp.Client.Core;

/// <summary>
/// Everything the client views need from a peer. The desktop head uses the loopback HTTP adapter
/// (<see cref="LocalApiClient"/>); a mobile head can implement this over its in-process peer runtime and
/// local replicated store, with no HTTP involved. Failures are reported as <see cref="LocalApiException"/>
/// subclasses so view models can show them without knowing the transport.
/// </summary>
public interface IPeerDataSource
{
    /// <summary>Human-readable place the data comes from, for status lines (an address, "this phone", ...).</summary>
    string Description { get; }

    /// <summary>
    /// True when this source raises <see cref="StateChanged"/> by itself; views then update on push and
    /// do not need to poll. False means callers must poll.
    /// </summary>
    bool SupportsLiveUpdates { get; }

    /// <summary>Raised (on any thread) when current state may have changed and should be re-read.</summary>
    event EventHandler? StateChanged;

    Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default);

    Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>State as of <paramref name="asOf"/>, or "now" when null.</summary>
    Task<StateDto> GetStateAsync(
        DateTimeOffset? asOf = null,
        int? lookbackSeconds = null,
        string? subjectEntityId = null,
        string? signalId = null,
        CancellationToken ct = default);

    /// <summary>All history for the query, following any paging the source needs, bounded by <paramref name="maxPages"/>.</summary>
    Task<HistoryResult> GetHistoryAsync(
        HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize, CancellationToken ct = default);

    Task ApprovePairingAsync(string pairingId, CancellationToken ct = default);

    Task RejectPairingAsync(string pairingId, CancellationToken ct = default);

    Task RevokePeerAsync(string peerId, CancellationToken ct = default);
}
