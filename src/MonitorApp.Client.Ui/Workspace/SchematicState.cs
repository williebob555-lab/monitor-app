using Avalonia;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Workspace;

/// <summary>A link with its routed screen-space polyline.</summary>
public sealed record LinkRoute(SceneLink Link, LinkPorts Ports, IReadOnlyList<Point> Points);

/// <summary>
/// Everything a renderer needs for one frame plus the hit-testing queries the control uses. Owned and mutated by
/// <see cref="SchematicCanvas"/>; renderers only read it. Layout (world space) and routes (screen space) are cached
/// and recomputed only when the scene, positions, camera, or explode progress change.
/// </summary>
public sealed class SchematicState
{
    private readonly Dictionary<string, Point> positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect> rects = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, LinkPorts> worldPorts = new Dictionary<string, LinkPorts>();
    private readonly List<LinkRoute> routes = [];
    private bool layoutDirty = true;
    private int routesCameraVersion = -1;
    private double routesExplode = -1;
    private bool routesDirty = true;
    private List<NodeBounds> paintOrder = [];

    public SchematicCamera Camera { get; } = new();

    public SceneGraph Scene { get; private set; } = SceneGraph.Empty;

    /// <summary>Nodes in paint order: peers and devices first, hosted services after.</summary>
    public IReadOnlyList<SceneNode> Nodes { get; private set; } = [];

    public IReadOnlyDictionary<string, SceneNode> NodeById { get; private set; } = new Dictionary<string, SceneNode>();

    /// <summary>Hosted children grouped by parent id (only parents that exist in the scene).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

    public string? SelectedNodeId { get; internal set; }

    public string? SelectedLinkId { get; internal set; }

    public string? HoverNodeId { get; internal set; }

    public string? HoverLinkId { get; internal set; }

    /// <summary>Node currently drawn exploded (kept while it reassembles).</summary>
    public string? ExplodedNodeId { get; internal set; }

    /// <summary>0 assembled .. 1 exploded; renderers apply their own easing.</summary>
    public double ExplodeProgress { get; internal set; }

    public double Seconds { get; internal set; }

    public bool ReducedMotion { get; internal set; }

    public bool IsHistorical { get; internal set; }

    public bool ShowFocusRing { get; internal set; }

    public IReadOnlyList<Guide> Guides { get; internal set; } = [];

    public IReadOnlyCollection<SceneLinkKind>? VisibleLinkKinds { get; internal set; }

    public Size Size { get; internal set; }

    public SemanticLevel Level => SemanticZoom.For(Camera.Zoom);

    public IReadOnlyList<LinkRoute> Routes => routes;

    public Func<SceneNode, Size> NodeSize { get; internal set; } = _ => new Size(1.5, 0.95);

    /// <summary>Screen point where links to the exploded node should land (e.g. its network card), if any.</summary>
    public Func<SchematicState, Point?>? ExplodedLanding { get; internal set; }

    public bool IsLinkVisible(SceneLink link) => VisibleLinkKinds is null || VisibleLinkKinds.Contains(link.Kind);

    public Point Position(string id) => positions.TryGetValue(id, out var p) ? p : default;

    public Rect WorldRect(string id) => rects.TryGetValue(id, out var r) ? r : default;

    public Rect ScreenRect(string id)
    {
        var r = WorldRect(id);
        var a = Camera.ToScreen(r.TopLeft);
        return new Rect(a, new Size(r.Width * Camera.Scale, r.Height * Camera.Scale));
    }

    public void SetScene(SceneGraph scene, Func<string, Point> placement)
    {
        Scene = scene;
        var ordered = scene.Nodes.Where(n => n.Kind != SceneNodeKind.Service).Concat(scene.Nodes.Where(n => n.Kind == SceneNodeKind.Service)).ToList();
        Nodes = ordered;
        NodeById = ordered.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Children = ordered
            .Where(n => n.ParentId is not null && NodeById.ContainsKey(n.ParentId))
            .GroupBy(n => n.ParentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(n => n.Id).ToList(), StringComparer.Ordinal);
        positions.Clear();
        foreach (var n in ordered)
        {
            positions[n.Id] = placement(n.Id);
        }

        MarkLayoutDirty();
    }

    public void SetPosition(string id, Point world)
    {
        positions[id] = world;
        MarkLayoutDirty();
    }

    public void MarkLayoutDirty()
    {
        layoutDirty = true;
        routesDirty = true;
    }

    public void MarkRoutesDirty() => routesDirty = true;

    /// <summary>Bounds of all nodes in world space, with a little room for labels. Empty scene gives a unit rect.</summary>
    public Rect ContentBounds()
    {
        EnsureLayout();
        if (rects.Count == 0)
        {
            return new Rect(-1, -1, 2, 2);
        }

        var all = rects.Values.Aggregate((a, b) => a.Union(b));
        return new Rect(all.X, all.Y, all.Width, all.Height + 0.35);
    }

    private void EnsureLayout()
    {
        if (!layoutDirty)
        {
            return;
        }

        rects.Clear();
        foreach (var n in Nodes)
        {
            var s = NodeSize(n);
            var c = Position(n.Id);
            rects[n.Id] = new Rect(c.X - (s.Width / 2), c.Y - (s.Height / 2), s.Width, s.Height);
        }

        worldPorts = PortPlanner.Plan(rects, Scene.Links);
        paintOrder = Nodes.Select(n => new NodeBounds(n.Id, HitRect(n.Id))).ToList();
        layoutDirty = false;
    }

    private Rect HitRect(string id)
    {
        var r = rects[id];
        return id == ExplodedNodeId && ExplodeProgress > 0.3
            ? new Rect(r.X - (r.Width * 0.6), r.Y - (r.Height * 1.6), r.Width * 2.2, r.Height * 3.0)
            : r;
    }

    public void EnsureRoutes()
    {
        EnsureLayout();
        if (!routesDirty && routesCameraVersion == Camera.Version && Math.Abs(routesExplode - ExplodeProgress) < 1e-9)
        {
            return;
        }

        routes.Clear();
        var landing = ExplodedNodeId is not null && ExplodeProgress > 0 ? ExplodedLanding?.Invoke(this) : null;
        var stub = Math.Max(12, 0.22 * Camera.Scale);
        foreach (var link in Scene.Links)
        {
            if (!worldPorts.TryGetValue(link.Id, out var wp))
            {
                continue;
            }

            var a = ToScreen(wp.From, link.FromId == ExplodedNodeId ? landing : null);
            var b = ToScreen(wp.To, link.ToId == ExplodedNodeId ? landing : null);
            routes.Add(new LinkRoute(link, new LinkPorts(a, b), SchematicGeometry.Route(a, b, stub)));
        }

        paintOrder = Nodes.Select(n => new NodeBounds(n.Id, HitRect(n.Id))).ToList();
        routesDirty = false;
        routesCameraVersion = Camera.Version;
        routesExplode = ExplodeProgress;
    }

    private Port ToScreen(Port world, Point? landing)
    {
        var p = Camera.ToScreen(world.Position);
        if (landing is { } l)
        {
            var e = Math.Clamp(ExplodeProgress, 0, 1);
            p = new Point(p.X + ((l.X - p.X) * e), p.Y + ((l.Y - p.Y) * e));
        }

        return new Port(p, world.Side);
    }

    public string? HitNode(Point screen)
    {
        EnsureRoutes();
        var world = Camera.ToWorld(screen);
        return SchematicHitTest.Node(paintOrder, world, 4 / Camera.Scale);
    }

    public string? HitLink(Point screen)
    {
        EnsureRoutes();
        var paths = routes.Where(r => IsLinkVisible(r.Link)).Select(r => new LinkPath(r.Link.Id, r.Points)).ToList();
        return SchematicHitTest.Link(paths, screen, 6);
    }

    /// <summary>Node, then link, at a screen point; null on empty space.</summary>
    public (string? Node, string? Link) Hit(Point screen)
    {
        var node = HitNode(screen);
        return node is not null ? (node, null) : (null, HitLink(screen));
    }

    /// <summary>Nodes that stay at full strength when something is selected: the selection and what it connects to.</summary>
    public HashSet<string>? FocusSet()
    {
        if (SelectedNodeId is { } id && NodeById.ContainsKey(id))
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { id };
            foreach (var l in Scene.Links.Where(l => l.FromId == id || l.ToId == id))
            {
                set.Add(l.FromId);
                set.Add(l.ToId);
            }

            if (Children.TryGetValue(id, out var kids))
            {
                set.UnionWith(kids);
            }

            if (NodeById[id].ParentId is { } parent)
            {
                set.Add(parent);
            }

            return set;
        }

        if (SelectedLinkId is { } lid && Scene.Links.FirstOrDefault(l => l.Id == lid) is { } link)
        {
            return new HashSet<string>(StringComparer.Ordinal) { link.FromId, link.ToId };
        }

        return null;
    }
}

/// <summary>Draws a <see cref="SchematicState"/>. The control never draws itself; swap the renderer to change the whole look.</summary>
public interface ISchematicRenderer
{
    /// <summary>Node footprint in world units (a node is laid out and hit-tested with this).</summary>
    Avalonia.Size NodeSize(SceneNode node);

    void Render(Avalonia.Media.DrawingContext context, SchematicState state);

    /// <summary>True while something drawn depends on time (pulses, status animation), so frames must keep coming.</summary>
    bool NeedsContinuousFrames(SchematicState state);

    /// <summary>Screen point where links land on the exploded node (its network component), or null.</summary>
    Point? ExplodedLanding(SchematicState state);

    /// <summary>Re-reads theme resources (brushes, fonts) from the host.</summary>
    void UpdateTheme(Avalonia.Controls.Control host);
}
