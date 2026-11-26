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

                if (Ring.Count == 0)
                {
                    LoadPreviousRun(folder);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the device; the ring still works.
            }
        }
    }

    /// <summary>Lines kept from the previous run when a process starts, so what happened just before it stopped is still sent.</summary>
    public const int PreviousRunLines = 1500;

    /// <summary>
    /// Seeds the memory ring with the end of the newest daily file. Phones send only the ring to their computers, and a phone whose
    /// monitoring stopped used to come back with a log that began at its restart, hiding why it stopped.
    /// </summary>
    private static void LoadPreviousRun(string folder)
    {
        var newest = System.IO.Directory.EnumerateFiles(folder, "diagnostics-*.log")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is null)
        {
            return;
        }

        var tail = new Queue<string>();
        foreach (var line in File.ReadLines(newest, Encoding.UTF8))
        {
            tail.Enqueue(line);
            if (tail.Count > PreviousRunLines)
            {
                tail.Dequeue();
            }
        }

        foreach (var line in tail)
        {
            Ring.Enqueue(line);
        }

        if (tail.Count > 0)
        {
            Ring.Enqueue(string.Create(CultureInfo.InvariantCulture,
                $"{clock():yyyy-MM-ddTHH:mm:ss.fffZ} [log] ---- lines above are the end of the previous run ({Path.GetFileName(newest)}) ----"));
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

    public const int MaximumPeerLines = 4000;
    public const int MaximumPeerLineLength = 1000;
    private static readonly Dictionary<string, (DateTimeOffset At, string[] Lines)> PeerLogs = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps the newest log a paired device sent about itself (replacing its previous one), in memory and, when a folder
    /// is configured, as <c>peers/diagnostics-{peerId}.log</c>. Lines are bounded and redacted again here.
    /// </summary>
    public static int StorePeerLog(string peerId, IEnumerable<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        if (!PeerIdPattern().IsMatch(peerId))
        {
            throw new ArgumentException("Not a peer id.", nameof(peerId));
        }

        var kept = lines.TakeLast(MaximumPeerLines)
            .Select(line => Redact(line.ReplaceLineEndings(" ")))
            .Select(line => line.Length > MaximumPeerLineLength ? line[..MaximumPeerLineLength] : line)
            .ToArray();
        lock (Gate)
        {
            PeerLogs[peerId] = (clock(), kept);
            if (directory is not null)
            {
                try
                {
                    var folder = Path.Combine(directory, "peers");
                    System.IO.Directory.CreateDirectory(folder);
                    File.WriteAllLines(Path.Combine(folder, $"diagnostics-{peerId}.log"), kept, Encoding.UTF8);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The memory copy still answers the local API.
                }
            }
        }

        return kept.Length;
    }

    /// <summary>The newest log <paramref name="peerId"/> sent and when, or null when it never sent one.</summary>
    public static (DateTimeOffset ReceivedAt, IReadOnlyList<string> Lines)? PeerLog(string peerId)
    {
        lock (Gate)
        {
            return PeerLogs.TryGetValue(peerId, out var log) ? (log.At, log.Lines) : null;
        }
    }

    /// <summary>Empties the memory ring and stops file output (tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Ring.Clear();
            PeerLogs.Clear();
            directory = null;
            clock = () => DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Cuts long hex or base64 runs (keys, signatures, tokens) to their first eight characters.</summary>
    public static string Redact(string text) =>
        LongSecret().Replace(text, match => match.Value[..8] + "…");

    [GeneratedRegex("[A-Za-z0-9+/=_-]{40,}")]
    private static partial Regex LongSecret();

    [GeneratedRegex("^peer-[a-z0-9]{1,64}$")]
    private static partial Regex PeerIdPattern();
}
