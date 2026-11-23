namespace MonitorApp.Client.Core.Scene;

/// <summary>
/// Deterministic, memory-keeping placement (spec 5.1). The local peer goes to the centre, peers on rings around
/// it at an angle taken from a stable hash of their id, and child devices close to their parent. A node that has
/// a remembered position (pinned by the user or placed earlier, possibly in a previous session) always keeps it;
/// a new node takes the nearest free slot, so nothing already on screen ever moves. Remembered positions of nodes
/// that are currently absent stay reserved so a returning node reappears where it was.
/// Units: a node is about 1 unit wide; new placements keep at least <see cref="MinSpacing"/> from every other node.
/// </summary>
public sealed class StableLayout
{
    public const double MinSpacing = 1.6;
    private const double PeerRingStart = 4.0;
    private const double PeerRingStep = 2.0;
    private const double ChildRingStart = 2.0;
    private const double ChildRingStep = 1.6;
    private const int MaxRings = 200;

    private readonly object gate = new();
    private readonly ILayoutStore store;
    private readonly Dictionary<string, ScenePoint> positions;

    public StableLayout(ILayoutStore? store = null)
    {
        this.store = store ?? new InMemoryLayoutStore();
        positions = new Dictionary<string, ScenePoint>(this.store.Load(), StringComparer.Ordinal);
    }

    /// <summary>Set when the last attempt to save positions failed; placement itself is unaffected.</summary>
    public string? PersistenceError { get; private set; }

    /// <summary>Positions for every node in <paramref name="nodes"/>; existing ones are returned unchanged.</summary>
    public IReadOnlyDictionary<string, ScenePoint> Place(IReadOnlyList<SceneNode> nodes)
    {
        lock (gate)
        {
            var added = false;
            var pending = nodes
                .Where(n => !positions.ContainsKey(n.Id))
                .OrderBy(n => Rank(n.Kind))
                .ThenBy(n => Hash(n.Id))
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var node in pending)
            {
                positions[node.Id] = node.Kind switch
                {
                    SceneNodeKind.LocalPeer => Free(default, 0, 1, 0),
                    _ when node.ParentId is { } parent && positions.TryGetValue(parent, out var origin) =>
                        Free(origin, ChildRingStart, ChildRingStep, Angle(node.Id)),
                    _ => Free(default, PeerRingStart, PeerRingStep, Angle(node.Id)),
                };
                added = true;
            }

            if (added)
            {
                Persist();
            }

            return nodes.ToDictionary(n => n.Id, n => positions[n.Id], StringComparer.Ordinal);
        }
    }

    /// <summary>Remembers a position the user chose. Not constrained by spacing: it is the user's decision.</summary>
    public void Pin(string nodeId, ScenePoint position)
    {
        lock (gate)
        {
            positions[nodeId] = position;
            Persist();
        }
    }

    private static int Rank(SceneNodeKind kind) => kind switch
    {
        SceneNodeKind.LocalPeer => 0,
        SceneNodeKind.Peer => 1,
        _ => 2,
    };

    private ScenePoint Free(ScenePoint center, double startRadius, double step, double startAngle)
    {
        if (startRadius == 0 && IsFree(center))
        {
            return center;
        }

        var radius = Math.Max(startRadius, step);
        for (var ring = 0; ring < MaxRings; ring++, radius += step)
        {
            var slots = Math.Max(6, (int)Math.Floor(Math.PI / Math.Asin(Math.Min(1, (MinSpacing / 2 + 0.05) / radius))));
            for (var slot = 0; slot < slots; slot++)
            {
                var angle = startAngle + (2 * Math.PI * slot / slots);
                var candidate = new ScenePoint(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
                if (IsFree(candidate))
                {
                    return candidate;
                }
            }
        }

        return new ScenePoint(center.X + radius, center.Y);
    }

    private bool IsFree(ScenePoint candidate) =>
        positions.Values.All(p => Math.Sqrt(Math.Pow(p.X - candidate.X, 2) + Math.Pow(p.Y - candidate.Y, 2)) >= MinSpacing - 1e-9);

    private void Persist()
    {
        try
        {
            store.Save(new Dictionary<string, ScenePoint>(positions, StringComparer.Ordinal));
            PersistenceError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PersistenceError = exception.Message;
        }
    }

    private static double Angle(string id) => Hash(id) / 4294967296.0 * 2 * Math.PI;

    /// <summary>FNV-1a over UTF-8; unlike string.GetHashCode it is identical across runs and machines.</summary>
    public static uint Hash(string id)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(id))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}
