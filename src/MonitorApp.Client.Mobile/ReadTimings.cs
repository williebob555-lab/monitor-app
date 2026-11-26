using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MonitorApp.Peer.Application;

namespace MonitorApp.Client.Mobile;

/// <summary>
/// Times each kind of read the screen makes on this device: how long it waited for a background thread to start it and how long
/// it ran. Once a minute it writes one <c>[data]</c> line (with the thread pool's backlog) to the log the device sends to its
/// computers, so a slow screen can be traced to the read, or the wait, that makes it slow.
/// </summary>
public static class ReadTimings
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, Totals> Kinds = new(StringComparer.Ordinal);
    private static long windowStart = Stopwatch.GetTimestamp();
    private static int writing;
    private static readonly TimeSpan LiveHeapEvery = TimeSpan.FromMinutes(10);
    private static long lastLiveHeap = Stopwatch.GetTimestamp();
    private static TimeSpan lastCpu = Process.GetCurrentProcess().TotalProcessorTime;

    /// <summary>Set by the Android app: whether the screen is on, so the log shows if reads go on while it is off.</summary>
    public static Func<bool?>? ScreenOn { get; set; }
    private static TimeSpan lastPause = GC.GetTotalPauseDuration();
    private static int lastGen0 = GC.CollectionCount(0);
    private static int lastGen1 = GC.CollectionCount(1);
    private static int lastGen2 = GC.CollectionCount(2);

    internal static void Add(string kind, TimeSpan waited, TimeSpan ran)
    {
        Kinds.GetOrAdd(kind, _ => new Totals()).Add(waited, ran);
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref windowStart)) < Window || Interlocked.Exchange(ref writing, 1) == 1)
        {
            return;
        }

        try
        {
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref windowStart));
            var parts = new List<string>();
            foreach (var (name, totals) in Kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var taken = totals.TakeAndReset();
                if (taken.Count > 0)
                {
                    parts.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{name} x{taken.Count} wait {taken.WaitMs / taken.Count:0}/{taken.MaxWaitMs:0} run {taken.RunMs / taken.Count:0}/{taken.MaxRunMs:0}"));
                }
            }

            DiagnosticLog.Write("data", string.Create(
                CultureInfo.InvariantCulture,
                $"reads in {elapsed.TotalSeconds:0} s (ms average/slowest): {string.Join("; ", parts)}; " +
                $"thread pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued; {GcSummary()}"));
            Interlocked.Exchange(ref windowStart, Stopwatch.GetTimestamp());
        }
        finally
        {
            Interlocked.Exchange(ref writing, 0);
        }
    }

    /// <summary>Memory clean-ups since the last line and how long they paused every thread, the screen's included.</summary>
    private static string GcSummary()
    {
        var pause = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"memory clean-ups {gen0 - lastGen0}/{gen1 - lastGen1}/{gen2 - lastGen2} (gen 0/1/2) pausing all threads {(pause - lastPause).TotalMilliseconds:0} ms; " +
            $"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB, allocated {GC.GetTotalAllocatedBytes() / (1024 * 1024)} MB since start, " +
            $"process memory {Environment.WorkingSet / (1024 * 1024)} MB{LiveHeap()}; {CpuSummary()}");
        (lastPause, lastGen0, lastGen1, lastGen2) = (pause, gen0, gen1, gen2);
        return text;
    }

    /// <summary>
    /// Every 10 minutes, the heap after a full collection. On Android (Mono) the plain heap figure includes garbage not yet collected,
    /// so only this number tells a real leak (it keeps rising) from churn (it stays flat).
    /// </summary>
    private static string LiveHeap()
    {
        if (Stopwatch.GetElapsedTime(lastLiveHeap) < LiveHeapEvery)
        {
            return string.Empty;
        }

        lastLiveHeap = Stopwatch.GetTimestamp();
        return string.Create(CultureInfo.InvariantCulture, $", live heap after a full collection {GC.GetTotalMemory(true) / (1024 * 1024)} MB");
    }

    /// <summary>CPU time this app used since the last line: the part of the battery drain that is the app's own work.</summary>
    private static string CpuSummary()
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var used = cpu - lastCpu;
        lastCpu = cpu;
        var screen = ScreenOn?.Invoke() switch { true => "on", false => "off", null => "unknown" };
        return string.Create(CultureInfo.InvariantCulture, $"app CPU {used.TotalSeconds:0.0} s since the last line; screen {screen}");
    }

    private sealed class Totals
    {
        private readonly object gate = new();
        private Snapshot current;

        public void Add(TimeSpan waited, TimeSpan ran)
        {
            lock (gate)
            {
                current = new Snapshot(
                    current.Count + 1,
                    current.WaitMs + waited.TotalMilliseconds,
                    Math.Max(current.MaxWaitMs, waited.TotalMilliseconds),
                    current.RunMs + ran.TotalMilliseconds,
                    Math.Max(current.MaxRunMs, ran.TotalMilliseconds));
            }
        }

        public Snapshot TakeAndReset()
        {
            lock (gate)
            {
                var taken = current;
                current = default;
                return taken;
            }
        }
    }

    private readonly record struct Snapshot(int Count, double WaitMs, double MaxWaitMs, double RunMs, double MaxRunMs);
}
