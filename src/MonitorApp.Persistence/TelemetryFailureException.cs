namespace MonitorApp.Persistence;

public enum TelemetryFailureReason
{
    Unauthorized,
    InvalidMessage,
    SequenceConflict,
    Replay,
}

public sealed class TelemetryFailureException(
    TelemetryFailureReason reason,
    string safeMessage) : Exception(safeMessage)
{
    public TelemetryFailureReason Reason { get; } = reason;
}
