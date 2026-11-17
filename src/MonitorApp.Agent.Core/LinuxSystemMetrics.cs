using System.Globalization;

namespace MonitorApp.Agent.Core;

public static class LinuxSystemMetrics
{
    public static CpuCounters ReadCpu(string procStat)
    {
        ArgumentNullException.ThrowIfNull(procStat);
        var cpuLine = procStat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line => line.StartsWith("cpu ", StringComparison.Ordinal));
        if (cpuLine is null)
        {
            throw new InvalidOperationException("/proc/stat does not contain aggregate CPU counters.");
        }

        var fields = cpuLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            throw new InvalidOperationException("/proc/stat aggregate CPU counters are incomplete.");
        }

        ulong total = 0;
        // guest and guest_nice are already included in user and nice by Linux.
        // Stop after steal (field index 8) to avoid counting guest time twice.
        for (var index = 1; index < Math.Min(fields.Length, 9); index++)
        {
            total = checked(total + ParseUnsigned(fields[index], "/proc/stat"));
        }

        var idle = ParseUnsigned(fields[4], "/proc/stat");
        if (fields.Length > 5)
        {
            idle = checked(idle + ParseUnsigned(fields[5], "/proc/stat"));
        }

        return new CpuCounters(idle, total);
    }

    public static MemoryCounters ReadMemory(string procMemInfo)
    {
        ArgumentNullException.ThrowIfNull(procMemInfo);
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in procMemInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var fields = line[(separator + 1)..].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            var kibibytes = ParseUnsigned(fields[0], "/proc/meminfo");
            values[line[..separator]] = checked(kibibytes * 1024UL);
        }

        if (!values.TryGetValue("MemTotal", out var total) || total == 0)
        {
            throw new InvalidOperationException("/proc/meminfo does not contain a valid MemTotal value.");
        }

        ulong available;
        if (!values.TryGetValue("MemAvailable", out available))
        {
            values.TryGetValue("MemFree", out var free);
            values.TryGetValue("Buffers", out var buffers);
            values.TryGetValue("Cached", out var cached);
            available = checked(free + buffers + cached);
        }

        available = Math.Min(available, total);
        return new MemoryCounters(total, total - available);
    }

    private static ulong ParseUnsigned(string value, string source)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"{source} contains a non-numeric counter.");
        }

        return parsed;
    }
}
