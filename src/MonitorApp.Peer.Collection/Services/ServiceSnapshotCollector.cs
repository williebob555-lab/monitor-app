using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Services;

/// <summary>A process seen on this computer: its name without ".exe" and when it started, when the system says.</summary>
public sealed record RunningProcess(string Name, DateTimeOffset? StartedAt);

/// <summary>What the service collector looks at; replaced in tests.</summary>
public interface IServiceProbe
{
    IReadOnlyList<RunningProcess> Processes();

    /// <summary>True when a systemd unit file with this name is installed (always false where there is no systemd).</summary>
    bool UnitInstalled(string unit);
}

/// <summary>Reads one service's own status in more depth (Tailscale, Syncthing) and returns its readings.</summary>
public interface IServiceDetailReader
{
    string ServiceId { get; }

    Task<IReadOnlyList<CollectedMetric>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The services this computer runs (D1): for each recognised service that is installed or running, whether it runs and
/// for how long, plus the detail its own reader finds (D2, D3). Readings are measured, never guessed: a service that was
/// never seen here sends nothing, and one that stopped sends "running: no".
/// </summary>
public sealed class ServiceSnapshotCollector(IServiceProbe probe, IReadOnlyList<IServiceDetailReader>? details = null, TimeProvider? time = null)
    : ISystemSnapshotCollector
{
    public const string CollectorId = "services";
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public static string RunningSignal(string id) => $"service.{id}.running";

    public static string UptimeSignal(string id) => $"service.{id}.uptime";

    public async ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var metrics = new List<CollectedMetric>();
        var health = new List<CollectorHealth>();
        var now = clock.GetUtcNow();
        IReadOnlyList<RunningProcess> processes;
        try
        {
            processes = probe.Processes();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            health.Add(new CollectorHealth(CollectorId, CapabilityState.Error, "process_list_failed"));
            return new SystemSnapshot(Timestamp.FromDateTimeOffset(now), metrics, health);
        }

        var byName = processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var running = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in KnownServices.All)
        {
            var mine = service.Processes.SelectMany(name => byName.GetValueOrDefault(name) ?? []).ToList();
            var installed = mine.Count > 0 || seen.Contains(service.Id) || service.Units.Any(SafeUnitInstalled);
            if (!installed)
            {
                continue;
            }

            seen.Add(service.Id);
            metrics.Add(new(RunningSignal(service.Id), MetricUnit.Boolean, ObservationQuality.Valid, new MetricValue { BooleanValue = mine.Count > 0 }));
            if (mine.Count > 0)
            {
                running.Add(service.Id);
                if (mine.Select(p => p.StartedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Min() is { } started && started != default && started <= now)
                {
                    metrics.Add(new(UptimeSignal(service.Id), MetricUnit.Seconds, ObservationQuality.Valid,
                        new MetricValue { DoubleValue = Math.Round((now - started).TotalSeconds) }));
                }
            }
        }

        foreach (var reader in details ?? [])
        {
            if (!running.Contains(reader.ServiceId))
            {
                continue;
            }

            try
            {
                metrics.AddRange(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One service's own status failing must not hide the others; say which one.
                health.Add(new CollectorHealth($"service.{reader.ServiceId}", CapabilityState.Error, "read_failed"));
            }
        }

        health.Add(new CollectorHealth(CollectorId, CapabilityState.Available, string.Empty));
        return new SystemSnapshot(Timestamp.FromDateTimeOffset(now), metrics, health);
    }

    private bool SafeUnitInstalled(string unit)
    {
        try
        {
            return probe.UnitInstalled(unit);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The real process list and systemd unit directories.</summary>
public sealed class SystemServiceProbe : IServiceProbe
{
    private static readonly string[] UnitDirectories =
        ["/etc/systemd/system", "/usr/lib/systemd/system", "/lib/systemd/system", "/usr/lib/systemd/user", "/etc/systemd/user"];

    public IReadOnlyList<RunningProcess> Processes()
    {
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    DateTimeOffset? started = null;
                    try
                    {
                        started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // Another user's process on Windows: the name is enough to say it runs.
                    }

                    result.Add(new RunningProcess(process.ProcessName, started));
                }
                catch (InvalidOperationException)
                {
                    // It exited while the list was read.
                }
            }
        }

        return result;
    }

    public bool UnitInstalled(string unit) =>
        OperatingSystem.IsLinux() && UnitDirectories.Any(directory => File.Exists(Path.Combine(directory, unit)));
}
