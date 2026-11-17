namespace MonitorApp.Agent.Core;

public sealed record TelemetrySpoolOptions(
    string DatabasePath,
    int MaximumPendingCount = 10_000,
    long MaximumPendingBytes = 64L * 1024L * 1024L)
{
    public TelemetrySpoolOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPendingCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPendingBytes);
        return this;
    }
}
