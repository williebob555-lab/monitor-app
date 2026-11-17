namespace MonitorApp.Domain.Telemetry;

/// <summary>
/// Represents the operational freshness of one telemetry observation.
/// </summary>
public enum FreshnessState
{
    /// <summary>No observation has been established.</summary>
    Unknown = 0,

    /// <summary>The observation and device contact are within the configured limits.</summary>
    Fresh = 1,

    /// <summary>The device is in contact, but the observation is older than the freshness limit.</summary>
    Stale = 2,

    /// <summary>The device has not contacted the controller within the offline limit.</summary>
    Offline = 3,

    /// <summary>The platform or collector explicitly cannot provide this observation.</summary>
    Unavailable = 4,
}
