using System.Diagnostics;
using System.Globalization;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>The parts of one live refresh, timed separately so a slow one names its cause.</summary>
internal enum RefreshStage
{
    /// <summary>Reading this device's status, paired devices and latest readings (off the UI thread on the phone).</summary>
    Read,

    /// <summary>Two minutes of network history for the activity rates.</summary>
    History,

    /// <summary>Names from Things.</summary>
    Things,

    /// <summary>Building the scene and placing it (UI thread).</summary>
    Project,

    /// <summary>Handing the scene to every view: lists, inspector, Dive, map (UI thread).</summary>
    Screen,
}

/// <summary>Stopwatch marks for one refresh.</summary>
internal sealed class RefreshTiming
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private long last;

    public double[] Milliseconds { get; } = new double[Enum.GetValues<RefreshStage>().Length];

    public static RefreshTiming Start() => new();

    public void Mark(RefreshStage stage)
    {
        var now = watch.ElapsedTicks;
        Milliseconds[(int)stage] += (now - last) * 1000.0 / Stopwatch.Frequency;
        last = now;
    }

    public double Total => watch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Adds up live refreshes and writes one line a minute to <see cref="EnvironmentViewModel.PerformanceLog"/>: how many ran, the
/// average time of each stage and the slowest, so lag on a device can be measured rather than guessed.
/// </summary>
internal sealed class RefreshStats
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly double[] totals = new double[Enum.GetValues<RefreshStage>().Length];
    private long windowStart = Stopwatch.GetTimestamp();
    private int count;
    private double slowest;
    private long readings;

    public void Add(RefreshTiming timing, int readingCount)
    {
        var sink = EnvironmentViewModel.PerformanceLog;
        if (sink is null)
        {
            return;
        }

        count++;
        readings += readingCount;
        slowest = Math.Max(slowest, timing.Total);
        for (var i = 0; i < totals.Length; i++)
        {
            totals[i] += timing.Milliseconds[i];
        }

        var elapsed = Stopwatch.GetElapsedTime(windowStart);
        if (elapsed < Window)
        {
            return;
        }

        sink(Describe(elapsed));
        Array.Clear(totals);
        count = 0;
        slowest = 0;
        readings = 0;
        windowStart = Stopwatch.GetTimestamp();
    }

    private string Describe(TimeSpan elapsed)
    {
        static string Ms(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        var stages = string.Join(", ", Enum.GetValues<RefreshStage>().Select(s => $"{s.ToString().ToLowerInvariant()} {Ms(totals[(int)s] / count)}"));
        var screenShare = totals[(int)RefreshStage.Project] + totals[(int)RefreshStage.Screen];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{count} screen refreshes in {elapsed.TotalSeconds:0} s; average ms: {stages}; slowest {Ms(slowest)} ms; " +
            $"view work on the UI thread {Ms(screenShare)} ms in total ({screenShare / elapsed.TotalMilliseconds:P1}); {readings / count} readings per refresh");
    }
}
