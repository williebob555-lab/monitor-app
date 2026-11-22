using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Application;

/// <summary>A caller-correctable query problem reported as HTTP 400.</summary>
public sealed class PeerQueryException(string message) : Exception(message);

/// <summary>
/// Bounded history, point-in-time reconstruction and catalog reads over stored origin records. Record bytes are
/// decoded with the production <see cref="TelemetryEnvelope"/> contract; states are never coerced to numbers.
/// </summary>
public sealed class PeerHistoryQueryService
{
    public const int MaximumWindowDays = 31;
    public const int DefaultRows = 1000;
    public const int MaximumRows = 5000;
    public const int MaximumLookbackDays = 31;
    public const int DefaultLookbackHours = 24;
    /// <summary>Granularity of returned points. Only raw detail exists today; rollup tiers will add values.</summary>
    public const string RawResolution = "raw";
    /// <summary>Hourly rollup points: one per (origin, subject, signal, UTC hour).</summary>
    public const string HourlyResolution = "hourly";
    /// <summary>Request value: raw while the whole window is inside raw retention, hourly otherwise.</summary>
    public const string AutoResolution = "auto";
    private const string RollupCursorPrefix = "h1.";
    private const int MaximumIdentifierLength = 256;

    private readonly PeerStore store;
    private readonly IPeerClock clock;
    private readonly TimeSpan staleAfter;
    private readonly PeerRetentionPolicy retention;

    public PeerHistoryQueryService(PeerStore store, IPeerClock clock, TimeSpan staleAfter, PeerRetentionPolicy? retention = null)
    {
        this.store = store;
        this.clock = clock;
        this.staleAfter = staleAfter;
        this.retention = retention ?? PeerRetentionPolicy.Default;
    }

    public async Task<HistoryResponse> QueryHistoryAsync(
        string? from,
        string? to,
        string? subjectEntityId,
        string? signalId,
        string? originPeerId,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken,
        string? resolution = null)
    {
        var start = RequireTime(from, "from");
        var end = RequireTime(to, "to");
        if (end <= start)
        {
            throw new PeerQueryException("to must be later than from; the window is [from, to).");
        }

        if (end - start > TimeSpan.FromDays(MaximumWindowDays))
        {
            throw new PeerQueryException($"The time window cannot exceed {MaximumWindowDays} days.");
        }

        var take = limit ?? DefaultRows;
        if (take is < 1 or > MaximumRows)
        {
            throw new PeerQueryException($"limit must be 1 through {MaximumRows}.");
        }

        var mode = string.IsNullOrWhiteSpace(resolution) ? AutoResolution : resolution.Trim().ToLowerInvariant();
        if (mode is not (RawResolution or HourlyResolution or AutoResolution))
        {
            throw new PeerQueryException("resolution must be raw, hourly or auto.");
        }

        // Auto serves hourly rollups once any part of the window is older than raw retention: raw detail there is
        // pruned (apart from transition records), so a raw answer would silently look empty or sparse.
        var rawFloor = PeerRetentionService.FloorToHour(clock.UtcNow - retention.RawRetention);
        if (mode == HourlyResolution || (mode == AutoResolution && start < rawFloor))
        {
            return await QueryHourlyAsync(start, end, take, subjectEntityId, signalId, originPeerId, cursor, cancellationToken).ConfigureAwait(false);
        }

        var after = DecodeCursor(cursor);
        var page = await store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            start,
            end,
            Filter(subjectEntityId, "subjectEntityId"),
            Filter(signalId, "signalId"),
            Filter(originPeerId, "originPeerId"),
            after,
            take), cancellationToken).ConfigureAwait(false);
        var points = page.Records.Select(record => ToPoint(record, record.ReceivedAt)).ToArray();
        string? next = null;
        if (page.Truncated && page.Records.Count > 0)
        {
            var last = page.Records[^1];
            next = EncodeCursor(new OriginRecordCursor(last.ObservedAt, last.OriginPeerId, last.OriginStreamId, last.Sequence));
        }

        return new HistoryResponse(start, end, take, RawResolution, points.Length, page.Truncated, next, points);
    }

    private async Task<HistoryResponse> QueryHourlyAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        int take,
        string? subjectEntityId,
        string? signalId,
        string? originPeerId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await store.ReadHourlyRollupsAsync(new HourlyRollupQuery(
            PeerRetentionService.FloorToHour(start),
            end,
            Filter(subjectEntityId, "subjectEntityId"),
            Filter(signalId, "signalId"),
            Filter(originPeerId, "originPeerId"),
            DecodeRollupCursor(cursor),
            take), cancellationToken).ConfigureAwait(false);
        var views = page.Rollups.Select(ToRollupView).ToArray();
        string? next = null;
        if (page.Truncated && page.Rollups.Count > 0)
        {
            var last = page.Rollups[^1];
            next = RollupCursorPrefix + EncodeCursor(new HourlyRollupKey(last.OriginPeerId, last.SubjectEntityId, last.SignalId, last.HourStart));
        }

        return new HistoryResponse(start, end, take, HourlyResolution, views.Length, page.Truncated, next, [], views);
    }

    private static HourlyRollupView ToRollupView(HourlyRollup rollup) => new(
        rollup.OriginPeerId,
        rollup.SubjectEntityId,
        rollup.SignalId,
        rollup.HourStart,
        rollup.HourStart.AddHours(1),
        rollup.SampleCount,
        rollup.Unit,
        rollup.NumericCount == 0 || rollup.MinValue is null || rollup.MaxValue is null || rollup.SumValue is null
            ? null
            : new RollupNumbersView(rollup.NumericCount, rollup.MinValue.Value, rollup.MaxValue.Value, rollup.SumValue.Value / rollup.NumericCount),
        rollup.FirstObservedAt,
        rollup.LastObservedAt,
        rollup.WorstQuality,
        new RollupQualityCountsView(
            rollup.ValidCount, rollup.EstimatedCount, rollup.UnavailableCount, rollup.DeniedCount, rollup.ErrorCount, rollup.OtherQualityCount));

    public async Task<StateAsOfResponse> ReconstructAsync(
        string? asOf,
        int? lookbackSeconds,
        string? subjectEntityId,
        string? signalId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var at = string.IsNullOrWhiteSpace(asOf) ? clock.UtcNow : RequireTime(asOf, "asOf");
        var lookback = lookbackSeconds is null
            ? TimeSpan.FromHours(DefaultLookbackHours)
            : TimeSpan.FromSeconds(lookbackSeconds.Value);
        if (lookbackSeconds is < 1 || lookback > TimeSpan.FromDays(MaximumLookbackDays))
        {
            throw new PeerQueryException($"lookbackSeconds must be positive and at most {MaximumLookbackDays} days.");
        }

        var take = limit ?? DefaultRows;
        if (take is < 1 or > MaximumRows)
        {
            throw new PeerQueryException($"limit must be 1 through {MaximumRows}.");
        }

        var notBefore = at - lookback;
        var page = await store.ReadOriginStateAsOfAsync(
            at,
            notBefore,
            Filter(subjectEntityId, "subjectEntityId"),
            Filter(signalId, "signalId"),
            take,
            cancellationToken).ConfigureAwait(false);
        return new StateAsOfResponse(
            at,
            notBefore,
            take,
            RawResolution,
            page.Truncated,
            page.Records.Select(record => ToPoint(record, at)).ToArray());
    }

    public async Task<CatalogResponse> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var current = await store.ReadCurrentObservationsAsync(cancellationToken).ConfigureAwait(false);
        var subjects = current
            .GroupBy(observation => observation.SubjectEntityId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CatalogSubjectView(
                group.Key,
                group.OrderBy(observation => observation.SignalId, StringComparer.Ordinal).Select(observation =>
                {
                    var decoded = Decode(observation.RecordBytes);
                    return new CatalogSignalView(
                        observation.SignalId,
                        decoded.Unit,
                        decoded.Kind,
                        observation.Quality,
                        observation.ObservedAt);
                }).ToArray()))
            .ToArray();
        var capabilities = await store.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogResponse(
            subjects,
            current.Select(observation => observation.OriginPeerId).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            capabilities.Select(entry => new CatalogCapabilityView(
                entry.CapabilityId, entry.Namespace, entry.Version, entry.UpdatedAt)).ToArray());
    }

    public HistoryPointView ToPoint(StoredOriginRecord record, DateTimeOffset reference)
    {
        var decoded = Decode(record.RecordBytes);
        var age = reference - record.ObservedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var freshness = Classify(record.Quality, age);
        var carriesValue = freshness is "Fresh" or "Stale";
        return new HistoryPointView(
            record.OriginPeerId,
            record.OriginStreamId,
            record.Sequence,
            record.SubjectEntityId,
            record.SignalId,
            record.ObservedAt,
            record.ReceivedAt,
            record.AdapterInstanceId,
            record.Provenance.ToString(),
            record.ReceivedFromPeerId,
            record.Quality,
            freshness,
            age.TotalSeconds,
            carriesValue ? decoded.Value : null,
            decoded.Unit,
            decoded.Detail);
    }

    /// <summary>
    /// Mirrors the domain freshness vocabulary: a usable observation is Fresh until its age exceeds the stale
    /// limit; explicit unavailable, denied and error states are Unavailable; anything unrecognised is Unknown.
    /// </summary>
    private string Classify(string quality, TimeSpan age) => quality switch
    {
        "Valid" or "Estimated" => age > staleAfter ? "Stale" : "Fresh",
        "Unavailable" or "PermissionDenied" or "Error" => "Unavailable",
        _ => "Unknown",
    };

    public static DecodedObservation Decode(byte[] recordBytes)
    {
        TelemetryEnvelope envelope;
        try
        {
            envelope = TelemetryEnvelope.Parser.ParseFrom(recordBytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return new DecodedObservation("undecodable", null, null, "Stored record bytes are not a telemetry envelope.");
        }

        switch (envelope.PayloadCase)
        {
            case TelemetryEnvelope.PayloadOneofCase.Metric:
                var metric = envelope.Metric;
                var unit = metric.Unit == MetricUnit.Unspecified ? null : metric.Unit.ToString();
                return metric.Value?.ValueCase switch
                {
                    MetricValue.ValueOneofCase.DoubleValue => Number(metric.Value.DoubleValue, unit),
                    MetricValue.ValueOneofCase.SignedIntegerValue => Number(metric.Value.SignedIntegerValue, unit),
                    MetricValue.ValueOneofCase.UnsignedIntegerValue => Number(metric.Value.UnsignedIntegerValue, unit),
                    MetricValue.ValueOneofCase.BooleanValue => new DecodedObservation(
                        "boolean", new ObservationValueView("boolean", null, null, metric.Value.BooleanValue), unit, null),
                    MetricValue.ValueOneofCase.StringValue => new DecodedObservation(
                        "text", new ObservationValueView("text", null, metric.Value.StringValue, null), unit, null),
                    _ => new DecodedObservation("none", null, unit, metric.ComponentId),
                };
            case TelemetryEnvelope.PayloadOneofCase.CollectorStatus:
                var status = envelope.CollectorStatus;
                return new DecodedObservation(
                    "status",
                    new ObservationValueView("text", null, status.State.ToString(), null),
                    null,
                    string.IsNullOrEmpty(status.DetailCode) ? null : status.DetailCode);
            default:
                return new DecodedObservation("unsupported", null, null, "Payload type is not displayed.");
        }
    }

    private static DecodedObservation Number(double value, string? unit) =>
        double.IsFinite(value)
            ? new DecodedObservation("number", new ObservationValueView("number", value, null, null), unit, null)
            : new DecodedObservation("none", null, unit, "Non-finite value withheld.");

    private static DateTimeOffset RequireTime(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PeerQueryException($"{name} is required as an ISO-8601 UTC time such as 2026-11-21T16:00:00Z.");
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new PeerQueryException($"{name} is not a valid ISO-8601 time.");
        }

        return parsed.ToUniversalTime();
    }

    private static string? Filter(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > MaximumIdentifierLength)
        {
            throw new PeerQueryException($"{name} is too long.");
        }

        return value;
    }

    private static HourlyRollupKey? DecodeRollupCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            if (!cursor.StartsWith(RollupCursorPrefix, StringComparison.Ordinal))
            {
                throw new PeerQueryException("cursor is invalid.");
            }

            var padded = cursor[RollupCursorPrefix.Length..].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var value = JsonSerializer.Deserialize<HourlyRollupKey>(Convert.FromBase64String(padded));
            if (value is null || value.HourStart.Offset != TimeSpan.Zero ||
                string.IsNullOrEmpty(value.OriginPeerId) || string.IsNullOrEmpty(value.SubjectEntityId) || string.IsNullOrEmpty(value.SignalId))
            {
                throw new PeerQueryException("cursor is invalid.");
            }

            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new PeerQueryException("cursor is invalid.");
        }
    }

    private static string EncodeCursor<T>(T cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static OriginRecordCursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var value = JsonSerializer.Deserialize<OriginRecordCursor>(Convert.FromBase64String(padded));
            if (value is null || value.Sequence == 0 || value.ObservedAt.Offset != TimeSpan.Zero ||
                string.IsNullOrEmpty(value.OriginPeerId) || string.IsNullOrEmpty(value.OriginStreamId))
            {
                throw new PeerQueryException("cursor is invalid.");
            }

            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new PeerQueryException("cursor is invalid.");
        }
    }
}

public sealed record DecodedObservation(string Kind, ObservationValueView? Value, string? Unit, string? Detail);

public sealed record ObservationValueView(string Kind, double? Number, string? Text, bool? Boolean);

public sealed record HistoryPointView(
    string OriginPeerId,
    string OriginStreamId,
    ulong Sequence,
    string SubjectEntityId,
    string SignalId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    string Source,
    string Provenance,
    string? ReceivedFromPeerId,
    string Quality,
    string Freshness,
    double AgeSeconds,
    ObservationValueView? Value,
    string? Unit,
    string? Detail);

public sealed record HistoryResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit,
    string Resolution,
    int Count,
    bool Truncated,
    string? NextCursor,
    IReadOnlyList<HistoryPointView> Points,
    IReadOnlyList<HourlyRollupView>? Rollups = null);

/// <summary>An hourly rollup point. States are counted, never averaged: <see cref="Numeric"/> covers Valid/Estimated numeric samples only.</summary>
public sealed record HourlyRollupView(
    string OriginPeerId,
    string SubjectEntityId,
    string SignalId,
    DateTimeOffset HourStart,
    DateTimeOffset HourEnd,
    int SampleCount,
    string? Unit,
    RollupNumbersView? Numeric,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string WorstQuality,
    RollupQualityCountsView QualityCounts);

public sealed record RollupNumbersView(int Count, double Min, double Max, double Mean);

public sealed record RollupQualityCountsView(int Valid, int Estimated, int Unavailable, int PermissionDenied, int Error, int Other);

public sealed record StateAsOfResponse(
    DateTimeOffset AsOf,
    DateTimeOffset LookbackFrom,
    int Limit,
    string Resolution,
    bool Truncated,
    IReadOnlyList<HistoryPointView> Points);

public sealed record CatalogSignalView(
    string SignalId,
    string? Unit,
    string Kind,
    string LatestQuality,
    DateTimeOffset LatestObservedAt);

public sealed record CatalogSubjectView(string SubjectEntityId, IReadOnlyList<CatalogSignalView> Signals);

public sealed record CatalogCapabilityView(string CapabilityId, string Namespace, string Version, DateTimeOffset UpdatedAt);

public sealed record CatalogResponse(
    IReadOnlyList<CatalogSubjectView> Subjects,
    IReadOnlyList<string> OriginPeers,
    IReadOnlyList<CatalogCapabilityView> Capabilities);
