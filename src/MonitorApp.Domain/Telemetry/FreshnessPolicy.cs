namespace MonitorApp.Domain.Telemetry;

/// <summary>
/// Classifies observations using explicit source time, device contact time, and quality.
/// </summary>
public sealed class FreshnessPolicy
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a freshness policy.</summary>
    public FreshnessPolicy(
        TimeSpan staleAfter,
        TimeSpan offlineAfter,
        TimeSpan futureSkewTolerance,
        TimeProvider timeProvider)
    {
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter), "The stale limit must be positive.");
        }

        if (offlineAfter <= staleAfter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offlineAfter),
                "The offline limit must be greater than the stale limit.");
        }

        if (futureSkewTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(futureSkewTolerance),
                "The future-skew tolerance cannot be negative.");
        }

        StaleAfter = staleAfter;
        OfflineAfter = offlineAfter;
        FutureSkewTolerance = futureSkewTolerance;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Gets the maximum age that is still considered fresh.</summary>
    public TimeSpan StaleAfter { get; }

    /// <summary>Gets the maximum contact age before a device is considered offline.</summary>
    public TimeSpan OfflineAfter { get; }

    /// <summary>
    /// Gets the maximum accepted clock lead. Accepted future timestamps have an age of zero.
    /// </summary>
    public TimeSpan FutureSkewTolerance { get; }

    /// <summary>Classifies an observation at the time supplied by the configured clock.</summary>
    public FreshnessState Evaluate(NumericObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var now = _timeProvider.GetUtcNow();
        TimeSpan? observedAge = observation.ObservedAtUtc is { } observedAt
            ? CalculateAge(observedAt, now, nameof(observation.ObservedAtUtc))
            : null;
        TimeSpan? lastContactAge = observation.LastContactAtUtc is { } lastContactAt
            ? CalculateAge(lastContactAt, now, nameof(observation.LastContactAtUtc))
            : null;

        if (lastContactAge > OfflineAfter)
        {
            return FreshnessState.Offline;
        }

        if (observation.Quality == ObservationQuality.Unknown)
        {
            return FreshnessState.Unknown;
        }

        if (observation.Quality == ObservationQuality.Unavailable)
        {
            return FreshnessState.Unavailable;
        }

        if (observation.Quality != ObservationQuality.Valid ||
            observation.Value is null ||
            observedAge is null ||
            lastContactAge is null)
        {
            throw new InvalidOperationException("A valid observation must contain a value and explicit timestamps.");
        }

        if (observedAge > StaleAfter)
        {
            return FreshnessState.Stale;
        }

        return FreshnessState.Fresh;
    }

    private TimeSpan CalculateAge(
        DateTimeOffset timestamp,
        DateTimeOffset now,
        string timestampName)
    {
        if (timestamp <= now)
        {
            return now - timestamp;
        }

        var clockLead = timestamp - now;
        if (clockLead <= FutureSkewTolerance)
        {
            return TimeSpan.Zero;
        }

        throw new ArgumentOutOfRangeException(
            timestampName,
            timestamp,
            $"Timestamp is {clockLead} ahead of the evaluation clock, which exceeds the allowed future skew of {FutureSkewTolerance}.");
    }
}
