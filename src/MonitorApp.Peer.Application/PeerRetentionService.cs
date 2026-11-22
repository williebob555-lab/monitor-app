using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Application;

/// <summary>
/// Tiered retention: full-detail raw records, hourly rollups, and long-lived transition (event) records.
/// Working defaults are recorded in docs/DECISIONS.md.
/// </summary>
/// <param name="RawRetention">How long every raw observation is kept.</param>
/// <param name="RollupRetention">How long hourly rollups are kept.</param>
/// <param name="EventRetention">How long raw transition records (quality changed versus the previous record of the same series) are kept past raw retention.</param>
/// <param name="ReplicationHardCap">Locally originated records not yet acknowledged by every active trusted peer are held past RawRetention, but never longer than this.</param>
public sealed record PeerRetentionPolicy(
    TimeSpan RawRetention,
    TimeSpan RollupRetention,
    TimeSpan EventRetention,
    TimeSpan ReplicationHardCap)
{
    public static PeerRetentionPolicy Default { get; } = new(
        TimeSpan.FromDays(7), TimeSpan.FromDays(365), TimeSpan.FromDays(5 * 365), TimeSpan.FromDays(30));

    public void Validate()
    {
        if (RawRetention < TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(RawRetention), "Raw retention must be at least two hours.");
        }

        if (RollupRetention < RawRetention || EventRetention < RawRetention || ReplicationHardCap < RawRetention)
        {
            throw new ArgumentOutOfRangeException(nameof(RollupRetention), "Rollup, event and replication hard-cap retention cannot be shorter than raw retention.");
        }
    }
}

public sealed record PeerRetentionReport(
    DateTimeOffset RanAt,
    DateTimeOffset RawCutoff,
    int RollupHoursComputed,
    int RollupRowsWritten,
    int RawDeleted,
    int RawRetainedPinned,
    int RawRetainedTransitions,
    int RawHeldForReplication,
    int PrunedRangesWritten,
    int RollupsDeleted);

public sealed class PeerRetentionService
{
    private const int RollupPageSize = 5000;
    private readonly PeerStore store;
    private readonly IPeerClock clock;

    public PeerRetentionService(PeerStore store, IPeerClock clock, PeerRetentionPolicy? policy = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Policy = policy ?? PeerRetentionPolicy.Default;
        Policy.Validate();
    }

    public PeerRetentionPolicy Policy { get; }

    public static DateTimeOffset FloorToHour(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - (value.UtcDateTime.Ticks % TimeSpan.TicksPerHour), TimeSpan.Zero);

    /// <summary>
    /// One idempotent retention pass for the node to call periodically (for example hourly): roll up completed
    /// hours, prune expired raw records (after rolling them up), then prune expired rollups.
    /// </summary>
    public async Task<PeerRetentionReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow.ToUniversalTime();
        var rawCutoff = FloorToHour(now - Policy.RawRetention);
        var (hours, rows) = await RollUpAsync(FloorToHour(now), rawCutoff, cancellationToken).ConfigureAwait(false);

        var local = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        var prune = await store.PruneRawRecordsAsync(new RawPruneRequest(
            rawCutoff,
            now - Policy.EventRetention,
            now - Policy.ReplicationHardCap,
            local?.PeerId,
            now), cancellationToken).ConfigureAwait(false);

        var rollupsDeleted = await store.PruneHourlyRollupsBeforeAsync(
            FloorToHour(now - Policy.RollupRetention), cancellationToken).ConfigureAwait(false);
        return new PeerRetentionReport(
            now, rawCutoff, hours, rows, prune.Deleted, prune.RetainedPinned, prune.RetainedTransitions,
            prune.RetainedAwaitingReplication, prune.PrunedRangesWritten, rollupsDeleted);
    }

    // Hours older than the raw cutoff are finalised once (existing rows kept even if raw is only partly left);
    // hours still inside raw retention are recomputed when late-arriving records change their count.
    private async Task<(int Hours, int Rows)> RollUpAsync(DateTimeOffset completeBefore, DateTimeOffset rawCutoff, CancellationToken cancellationToken)
    {
        var hours = await store.ReadRollupWorkHoursAsync(completeBefore, rawCutoff, cancellationToken).ConfigureAwait(false);
        var rows = 0;
        foreach (var hour in hours)
        {
            var rollups = await ComputeHourAsync(hour, cancellationToken).ConfigureAwait(false);
            rows += await store.UpsertHourlyRollupsAsync(rollups, replace: hour >= rawCutoff, cancellationToken).ConfigureAwait(false);
        }

        return (hours.Count, rows);
    }

    private async Task<IReadOnlyList<HourlyRollup>> ComputeHourAsync(DateTimeOffset hour, CancellationToken cancellationToken)
    {
        var accumulators = new SortedDictionary<(string Origin, string Subject, string Signal), Accumulator>();
        OriginRecordCursor? after = null;
        while (true)
        {
            var page = await store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
                hour, hour.AddHours(1), null, null, null, after, RollupPageSize), cancellationToken).ConfigureAwait(false);
            foreach (var record in page.Records)
            {
                var key = (record.OriginPeerId, record.SubjectEntityId, record.SignalId);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new Accumulator();
                    accumulators[key] = accumulator;
                }

                accumulator.Add(record);
            }

            if (!page.Truncated || page.Records.Count == 0)
            {
                break;
            }

            var last = page.Records[^1];
            after = new OriginRecordCursor(last.ObservedAt, last.OriginPeerId, last.OriginStreamId, last.Sequence);
        }

        return accumulators.Select(pair => pair.Value.ToRollup(pair.Key.Origin, pair.Key.Subject, pair.Key.Signal, hour)).ToArray();
    }

    // Quality severity for "worst quality"; unrecognised qualities sit between Estimated and Unavailable.
    internal static int Severity(string quality) => quality switch
    {
        "Valid" => 0,
        "Estimated" => 1,
        "Unavailable" => 3,
        "PermissionDenied" => 4,
        "Error" => 5,
        _ => 2,
    };

    private sealed class Accumulator
    {
        private int samples, numeric, valid, estimated, unavailable, denied, error, other;
        private double? min, max, sum;
        private string? unit;
        private DateTimeOffset first = DateTimeOffset.MaxValue, last = DateTimeOffset.MinValue;
        private string worst = "Valid";

        public void Add(StoredOriginRecord record)
        {
            samples++;
            first = record.ObservedAt < first ? record.ObservedAt : first;
            last = record.ObservedAt > last ? record.ObservedAt : last;
            if (samples == 1 || Severity(record.Quality) > Severity(worst))
            {
                worst = record.Quality;
            }

            switch (record.Quality)
            {
                case "Valid": valid++; break;
                case "Estimated": estimated++; break;
                case "Unavailable": unavailable++; break;
                case "PermissionDenied": denied++; break;
                case "Error": error++; break;
                default: other++; break;
            }

            if (record.Quality is not ("Valid" or "Estimated"))
            {
                return;
            }

            var decoded = PeerHistoryQueryService.Decode(record.RecordBytes);
            unit ??= decoded.Unit;
            if (decoded.Value is { Kind: "number", Number: { } number })
            {
                numeric++;
                min = min is null ? number : Math.Min(min.Value, number);
                max = max is null ? number : Math.Max(max.Value, number);
                sum = (sum ?? 0) + number;
            }
        }

        public HourlyRollup ToRollup(string origin, string subject, string signal, DateTimeOffset hour) =>
            new(origin, subject, signal, hour, samples, numeric, min, max, sum, unit, first, last, worst,
                valid, estimated, unavailable, denied, error, other);
    }
}
