namespace MonitorApp.Agent.Core;

public sealed class SpoolCapacityExceededException : InvalidOperationException
{
    public SpoolCapacityExceededException()
        : base("The telemetry spool is full; unacknowledged observations were preserved.")
    {
    }
}
