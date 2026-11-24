using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorApp.Peer.Application;

/// <summary>
/// A plain-text record of what this device did with pairing, connections, removals and updates, so a failure can be
/// explained afterwards instead of guessed at. Lines go to a memory ring (the newest few thousand, readable through the
/// local API) and, once <see cref="Configure"/> names a folder, to one file per day there; files older than a week are
/// deleted. Nothing secret is written: long key-like runs of hex or base64 are cut to their first eight characters.
/// </summary>
public static partial class DiagnosticLog
{
    public const int RingCapacity = 4000;
    public const long MaximumFileBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly Lock Gate = new();
    private static readonly Queue<string> Ring = new();
    private static string? directory;
    private static Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;

    /// <summary>The folder the daily files are written to, or null when only the memory ring is kept.</summary>
    public static string? Directory
    {
        get
        {
            lock (Gate)
            {
                return directory;
            }
        }
    }

    /// <summary>Starts writing daily files into <paramref name="folder"/> (created if needed) and prunes old ones.</summary>
    public static void Configure(string folder, Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        lock (Gate)
        {
            directory = folder;
            if (now is not null)
            {
                clock = now;
            }

            try
            {
                System.IO.Directory.CreateDirectory(folder);
                var cutoff = clock() - Retention;
                foreach (var file in System.IO.Directory.EnumerateFiles(folder, "diagnostics-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the device; the ring still works.
            }
        }
    }

    /// <summary>Records one line under <paramref name="area"/> (pairing, link, removal, mesh, update…).</summary>
    public static void Write(string area, string message)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (Gate)
        {
            var now = clock();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} [{area}] {Redact(message.ReplaceLineEndings(" "))}");
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity)
            {
                Ring.Dequeue();
            }

            if (directory is null)
            {
                return;
            }

            try
            {
                var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"diagnostics-{now:yyyyMMdd}.log"));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaximumFileBytes)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A full or locked disk loses file lines only.
            }
        }
    }

    public static void Write(string area, string message, Exception exception) =>
        Write(area, $"{message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>The newest <paramref name="count"/> lines, oldest first.</summary>
    public static IReadOnlyList<string> Recent(int count = 500)
    {
        lock (Gate)
        {
            return Ring.Skip(Math.Max(0, Ring.Count - Math.Clamp(count, 1, RingCapacity))).ToArray();
        }
    }

    /// <summary>Empties the memory ring and stops file output (tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Ring.Clear();
            directory = null;
            clock = () => DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Cuts long hex or base64 runs (keys, signatures, tokens) to their first eight characters.</summary>
    public static string Redact(string text) =>
        LongSecret().Replace(text, match => match.Value[..8] + "…");

    [GeneratedRegex("[A-Za-z0-9+/=_-]{40,}")]
    private static partial Regex LongSecret();
}
