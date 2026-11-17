namespace MonitorApp.Domain.Telemetry;

/// <summary>
/// Describes whether a telemetry observation contains a usable measurement.
/// </summary>
public enum ObservationQuality
{
    /// <summary>No observation has been established.</summary>
    Unknown = 0,

    /// <summary>The observation contains a measured value and explicit timestamps.</summary>
    Valid = 1,

    /// <summary>The collector reports that this measurement is not available.</summary>
    Unavailable = 2,
}
