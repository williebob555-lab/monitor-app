using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;

namespace MonitorApp.Peer.Persistence;

/// <summary>
/// Diagnostic bookkeeping of SQLite connections that are currently open, keyed by full database path. Every store that
/// opens a connection registers it right after opening; the registration ends when the connection is closed or disposed.
/// Tests assert that nothing remains open for a database before deleting its directory (Windows refuses to delete a file
/// that still has an open handle, Linux does not, so a leaked connection is otherwise invisible there).
/// </summary>
public static class SqliteConnectionTracker
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, string>> Open =
        new(StringComparer.Ordinal);

    private static long nextId;

    /// <summary>When true, the stack of every open is retained so a leak can be attributed. Off by default (cost).</summary>
    public static bool TrackStacks { get; set; }

    /// <summary>Registers an already opened connection; the registration is removed when it closes.</summary>
    public static SqliteConnection Track(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var key = Path.GetFullPath(connection.DataSource);
        var id = Interlocked.Increment(ref nextId);
        var bucket = Open.GetOrAdd(key, static _ => new ConcurrentDictionary<long, string>());
        bucket[id] = TrackStacks ? Environment.StackTrace : string.Empty;
        var released = 0;
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Closed && Interlocked.Exchange(ref released, 1) == 0)
            {
                bucket.TryRemove(id, out string? _);
            }
        };
        return connection;
    }

    /// <summary>Number of connections still open for the database file.</summary>
    public static int OpenCount(string databasePath) =>
        Open.TryGetValue(Path.GetFullPath(databasePath), out var bucket) ? bucket.Count : 0;

    /// <summary>Number of connections still open for any database file under the directory.</summary>
    public static int OpenCountUnder(string directory) =>
        Open.Where(pair => IsUnder(pair.Key, directory)).Sum(pair => pair.Value.Count);

    /// <summary>Human-readable list of open connections under the directory, with open stacks when tracked.</summary>
    public static string DescribeOpenUnder(string directory)
    {
        var lines = new List<string>();
        foreach (var pair in Open.Where(pair => IsUnder(pair.Key, directory)))
        {
            foreach (var stack in pair.Value.Values)
            {
                lines.Add(pair.Key + ": open connection" + (stack.Length == 0
                    ? " (set SqliteConnectionTracker.TrackStacks for the opening stack)"
                    : Environment.NewLine + stack));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsUnder(string file, string directory) =>
        file.StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
}
