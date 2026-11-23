using System.Text.Json;

namespace MonitorApp.Client.Core.Scene;

/// <summary>Remembers where nodes sit so the schematic keeps its spatial memory between refreshes and sessions.</summary>
public interface ILayoutStore
{
    IReadOnlyDictionary<string, ScenePoint> Load();

    void Save(IReadOnlyDictionary<string, ScenePoint> positions);
}

public sealed class InMemoryLayoutStore : ILayoutStore
{
    private readonly object gate = new();
    private Dictionary<string, ScenePoint> positions = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ScenePoint> Load()
    {
        lock (gate)
        {
            return new Dictionary<string, ScenePoint>(positions, StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, ScenePoint> positions)
    {
        lock (gate)
        {
            this.positions = new Dictionary<string, ScenePoint>(positions, StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Stores positions as JSON. Saves write a temporary file in the same directory and then replace the target, so a
/// crash never leaves a half-written layout. A missing or unreadable file loads as empty (the layout is only a
/// convenience; the scene is rebuilt from node data).
/// </summary>
public sealed class JsonFileLayoutStore(string path) : ILayoutStore
{
    private sealed record Entry(double X, double Y);

    public IReadOnlyDictionary<string, ScenePoint> Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, ScenePoint>();
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            return entries is null
                ? new Dictionary<string, ScenePoint>()
                : entries
                    .Where(e => e.Value is not null && double.IsFinite(e.Value.X) && double.IsFinite(e.Value.Y))
                    .ToDictionary(e => e.Key, e => new ScenePoint(e.Value.X, e.Value.Y), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ScenePoint>();
        }
    }

    public void Save(IReadOnlyDictionary<string, ScenePoint> positions)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var entries = positions.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => new Entry(p.Value.X, p.Value.Y), StringComparer.Ordinal);
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
