namespace MonitorApp.Domain.Telemetry;

/// <summary>
/// A numeric telemetry observation that preserves value presence separately from quality.
/// </summary>
public sealed record NumericObservation
{
    private NumericObservation(
        ObservationQuality quality,
        double? value,
        DateTimeOffset? observedAtUtc,
        DateTimeOffset? lastContactAtUtc)
    {
        Quality = quality;
        Value = value;
        ObservedAtUtc = observedAtUtc;
        LastContactAtUtc = lastContactAtUtc;
    }

    /// <summary>Gets the collector-reported quality.</summary>
    public ObservationQuality Quality { get; }

    /// <summary>
    /// Gets the measured value. A value of zero is a valid measurement and is not missing data.
    /// </summary>
    public double? Value { get; }

    /// <summary>Gets the UTC time at which the source produced the measurement.</summary>
    public DateTimeOffset? ObservedAtUtc { get; }

    /// <summary>Gets the UTC time of the most recent device contact known with this observation.</summary>
    public DateTimeOffset? LastContactAtUtc { get; }

    /// <summary>
    /// Creates an observation when no measurement has been established, optionally preserving
    /// the most recent device contact used to determine whether the device is offline.
    /// </summary>
    public static NumericObservation Unknown(DateTimeOffset? lastContactAtUtc = null)
    {
        RequireUtcIfPresent(lastContactAtUtc, nameof(lastContactAtUtc));
        return new NumericObservation(ObservationQuality.Unknown, null, null, lastContactAtUtc);
    }

    /// <summary>
    /// Creates an observation for a metric the collector cannot provide, optionally preserving
    /// the most recent device contact used to determine whether the device is offline.
    /// </summary>
    public static NumericObservation Unavailable(DateTimeOffset? lastContactAtUtc = null)
    {
        RequireUtcIfPresent(lastContactAtUtc, nameof(lastContactAtUtc));
        return new NumericObservation(ObservationQuality.Unavailable, null, null, lastContactAtUtc);
    }

    /// <summary>Creates a valid measurement with explicit source and contact times.</summary>
    public static NumericObservation Valid(
        double value,
        DateTimeOffset observedAtUtc,
        DateTimeOffset lastContactAtUtc)
    {
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireUtc(lastContactAtUtc, nameof(lastContactAtUtc));

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A valid measurement must be finite.");
        }

        if (lastContactAtUtc < observedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastContactAtUtc),
                "Last contact cannot precede the source observation.");
        }

        return new NumericObservation(
            ObservationQuality.Valid,
            value,
            observedAtUtc,
            lastContactAtUtc);
    }

    private static void RequireUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Telemetry timestamps must use UTC.", parameterName);
        }
    }

    private static void RequireUtcIfPresent(DateTimeOffset? timestamp, string parameterName)
    {
        if (timestamp.HasValue)
        {
            RequireUtc(timestamp.Value, parameterName);
        }
    }
}
