namespace MonitorApp.Agent.Core;

public sealed record TelemetrySpoolState(
    ulong NextSequence,
    ulong AcceptedThroughSequence,
    ulong LowestPendingSequence,
    int PendingCount,
    long PendingBytes);
