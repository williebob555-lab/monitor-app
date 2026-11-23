using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Agent.Core;

public sealed class SystemSnapshotCollector : ISystemSnapshotCollector
{
    private readonly Platform platform;
    private CpuCounters? previousCpu;

    public SystemSnapshotCollector(Platform platform)
    {
        if (platform is not (Platform.Windows or Platform.Fedora or Platform.Ubuntu))
        {
            throw new PlatformNotSupportedException("A desktop agent collector requires Windows, Fedora, or Ubuntu.");
        }

        this.platform = platform;
    }

    public ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = new List<CollectedMetric>();
        var health = new List<CollectorHealth>();

        CollectCpu(metrics, health);
        CollectMemory(metrics, health);
        CollectStorage(metrics, health);
        CollectNetwork(metrics, health);

        return ValueTask.FromResult(new SystemSnapshot(
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            metrics.AsReadOnly(),
            health.AsReadOnly()));
    }

    private void CollectCpu(List<CollectedMetric> metrics, List<CollectorHealth> health)
    {
        try
        {
            var current = platform == Platform.Windows
                ? WindowsSystemMetrics.ReadCpu()
                : LinuxSystemMetrics.ReadCpu(File.ReadAllText("/proc/stat"));

            if (previousCpu is null || current.Total <= previousCpu.Value.Total)
            {
                metrics.Add(Unavailable("system.cpu.utilization", MetricUnit.Ratio));
                health.Add(new CollectorHealth("cpu", CapabilityState.Degraded, "warmup_required"));
            }
            else
            {
                var totalDelta = current.Total - previousCpu.Value.Total;
                var idleDelta = current.Idle - previousCpu.Value.Idle;
                var utilization = Math.Clamp(1D - ((double)idleDelta / totalDelta), 0D, 1D);
                metrics.Add(Double("system.cpu.utilization", MetricUnit.Ratio, utilization));
                health.Add(new CollectorHealth("cpu", CapabilityState.Available, string.Empty));
            }

            previousCpu = current;
        }
        catch (UnauthorizedAccessException)
        {
            metrics.Add(Unavailable("system.cpu.utilization", MetricUnit.Ratio, ObservationQuality.PermissionDenied));
            health.Add(new CollectorHealth("cpu", CapabilityState.PermissionDenied, "permission_denied"));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            metrics.Add(Unavailable("system.cpu.utilization", MetricUnit.Ratio, ObservationQuality.Error));
            health.Add(new CollectorHealth("cpu", CapabilityState.Error, "collection_failed"));
        }
    }

    private void CollectMemory(List<CollectedMetric> metrics, List<CollectorHealth> health)
    {
        try
        {
            var memory = platform == Platform.Windows
                ? WindowsSystemMetrics.ReadMemory()
                : LinuxSystemMetrics.ReadMemory(File.ReadAllText("/proc/meminfo"));
            metrics.Add(Unsigned("system.memory.total", MetricUnit.Bytes, memory.Total));
            metrics.Add(Unsigned("system.memory.used", MetricUnit.Bytes, memory.Used));
            health.Add(new CollectorHealth("memory", CapabilityState.Available, string.Empty));
        }
        catch (UnauthorizedAccessException)
        {
            AddUnavailablePair(metrics, "system.memory.total", "system.memory.used", ObservationQuality.PermissionDenied);
            health.Add(new CollectorHealth("memory", CapabilityState.PermissionDenied, "permission_denied"));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            AddUnavailablePair(metrics, "system.memory.total", "system.memory.used", ObservationQuality.Error);
            health.Add(new CollectorHealth("memory", CapabilityState.Error, "collection_failed"));
        }
    }

    private static void CollectStorage(List<CollectedMetric> metrics, List<CollectorHealth> health)
    {
        try
        {
            ulong total = 0;
            ulong free = 0;
            foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
            {
                total = checked(total + (ulong)drive.TotalSize);
                free = checked(free + (ulong)drive.AvailableFreeSpace);
            }

            if (total == 0)
            {
                AddUnavailablePair(metrics, "system.storage.total", "system.storage.free");
                health.Add(new CollectorHealth("storage", CapabilityState.Unsupported, "no_ready_filesystem"));
                return;
            }

            metrics.Add(Unsigned("system.storage.total", MetricUnit.Bytes, total));
            metrics.Add(Unsigned("system.storage.free", MetricUnit.Bytes, free));
            health.Add(new CollectorHealth("storage", CapabilityState.Available, string.Empty));
        }
        catch (UnauthorizedAccessException)
        {
            AddUnavailablePair(metrics, "system.storage.total", "system.storage.free", ObservationQuality.PermissionDenied);
            health.Add(new CollectorHealth("storage", CapabilityState.PermissionDenied, "permission_denied"));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OverflowException)
        {
            AddUnavailablePair(metrics, "system.storage.total", "system.storage.free", ObservationQuality.Error);
            health.Add(new CollectorHealth("storage", CapabilityState.Error, "collection_failed"));
        }
    }

    private void CollectNetwork(List<CollectedMetric> metrics, List<CollectorHealth> health)
    {
        try
        {
            ulong received = 0;
            ulong sent = 0;
            var usableInterfaceCount = 0;
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.OperationalStatus != OperationalStatus.Up ||
                    (platform == Platform.Windows && WindowsNetworkPlumbing.IsPlumbing(
                        networkInterface.NetworkInterfaceType, networkInterface.Name, networkInterface.Description)))
                {
                    continue;
                }

                var statistics = networkInterface.GetIPStatistics();
                received = checked(received + (ulong)statistics.BytesReceived);
                sent = checked(sent + (ulong)statistics.BytesSent);
                usableInterfaceCount++;
            }

            if (usableInterfaceCount == 0)
            {
                AddUnavailablePair(metrics, "system.network.bytes_received", "system.network.bytes_sent");
                health.Add(new CollectorHealth("network", CapabilityState.Unsupported, "no_active_interface"));
                return;
            }

            metrics.Add(Unsigned("system.network.bytes_received", MetricUnit.Bytes, received));
            metrics.Add(Unsigned("system.network.bytes_sent", MetricUnit.Bytes, sent));
            health.Add(new CollectorHealth("network", CapabilityState.Available, string.Empty));
        }
        catch (NetworkInformationException)
        {
            AddUnavailablePair(metrics, "system.network.bytes_received", "system.network.bytes_sent", ObservationQuality.Error);
            health.Add(new CollectorHealth("network", CapabilityState.Error, "collection_failed"));
        }
    }

    private static CollectedMetric Double(string id, MetricUnit unit, double value) =>
        new(id, unit, ObservationQuality.Valid, new MetricValue { DoubleValue = value });

    private static CollectedMetric Unsigned(string id, MetricUnit unit, ulong value) =>
        new(id, unit, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = value });

    private static CollectedMetric Unavailable(
        string id,
        MetricUnit unit,
        ObservationQuality quality = ObservationQuality.Unavailable) =>
        new(id, unit, quality, null);

    private static void AddUnavailablePair(
        List<CollectedMetric> metrics,
        string first,
        string second,
        ObservationQuality quality = ObservationQuality.Unavailable)
    {
        metrics.Add(Unavailable(first, MetricUnit.Bytes, quality));
        metrics.Add(Unavailable(second, MetricUnit.Bytes, quality));
    }
}

public readonly record struct CpuCounters(ulong Idle, ulong Total);

public readonly record struct MemoryCounters(ulong Total, ulong Used);
