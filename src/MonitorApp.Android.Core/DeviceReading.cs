namespace MonitorApp.Android.Core;

public sealed record DeviceReading<T>(
    bool IsAvailable,
    T? Value,
    DateTimeOffset SourceTime,
    string Detail)
    where T : struct;
