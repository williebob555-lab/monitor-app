namespace MonitorApp.Client.Core.Scene;

// The scene is a read-only projection of peer data for the living schematic and the exploded inspector.
// It is never the source of truth (AGENTS.md): every node, layer, and link is derived from DTOs the node
// returned, and nothing is invented that a peer, adapter, or evidence-qualified inference cannot support.

/// <summary>What an object in the schematic is. Drives its silhouette, never its health.</summary>
public enum SceneNodeKind
{
    /// <summary>The peer this client is attached to ("this device").</summary>
    LocalPeer,

    /// <summary>A paired Monitor App peer (computer, tablet, or phone).</summary>
    Peer,

    /// <summary>An observed non-peer device or a subject reported by a peer.</summary>
    Device,

    /// <summary>A hosted service.</summary>
    Service,
}

/// <summary>
/// Health as shown to the user. Stale and Offline are deliberately separate (spec 17.2: identify which
/// device is stale and which is confirmed offline). Ordered by attention: higher values need more.
/// </summary>
public enum SceneHealth
{
    Unknown = 0,
    Healthy = 1,
    Stale = 2,
    Degraded = 3,
    Offline = 4,
}

/// <summary>Typed relationship between two scene nodes.</summary>
public enum SceneLinkKind
{
    /// <summary>Paired trust plus origin-history replication between two peers.</summary>
    Replication,

    /// <summary>A peer hosts or collects a device or service.</summary>
    Hosting,

    /// <summary>One object depends on another.</summary>
    Dependency,
}

/// <summary>Visual lens: which aspect of the environment is emphasised. Lenses never hide health.</summary>
public enum SceneLens
{
    Health,
    Connectivity,
    Storage,
    Power,
    Compute,
}

/// <summary>Kind of an internal layer in the exploded architecture of a node.</summary>
public enum SceneLayerKind
{
    Compute,
    Memory,
    Storage,
    Network,
    Power,
    Connectivity,
    Other,

    /// <summary>Whether each of the device's own data collectors is working (battery reader, network reader, ...).</summary>
    Collection,
}

/// <summary>One exact reading, always with unit, time, source, quality, and freshness (spec 3: exact text for every metric).</summary>
public sealed record SceneReading(
    string SubjectEntityId,
    string SignalId,
    string Label,
    string DisplayValue,
    double? Number,
    string? Unit,
    string Quality,
    string Freshness,
    DateTimeOffset ObservedAt,
    double AgeSeconds,
    string OriginPeerId,
    string Source);

/// <summary>
/// An internal layer of a node (CPU, memory, storage, battery, ...). Only produced when the node actually
/// reports signals for it. <see cref="Fill"/> is a 0..1 proportion when one is truthfully derivable
/// (used/total, battery level, utilization), otherwise null.
/// </summary>
public sealed record SceneLayer(
    string Id,
    string Title,
    SceneLayerKind Kind,
    SceneHealth Health,
    double? Fill,
    string Summary,
    IReadOnlyList<SceneReading> Readings);

/// <summary>An object in the schematic.</summary>
/// <param name="Id">Stable identity (peer id or subject entity id). Used as the layout key.</param>
/// <param name="ParentId">Host node for devices/services; null for peers.</param>
/// <param name="HealthReason">Plain-text explanation of <paramref name="Health"/>, e.g. "Last reading 4 min ago".</param>
/// <param name="Activity">Broad 0..1 activity intensity derived from real counters, or 0 when unknown.</param>
public sealed record SceneNode(
    string Id,
    string Label,
    SceneNodeKind Kind,
    SceneHealth Health,
    string HealthReason,
    string? ParentId,
    IReadOnlyList<SceneLayer> Layers,
    double Activity,
    DateTimeOffset? LastSeen);

/// <summary>A typed relationship. <see cref="Activity"/> is a 0..1 broad intensity of real movement along it.</summary>
public sealed record SceneLink(
    string Id,
    string FromId,
    string ToId,
    SceneLinkKind Kind,
    SceneHealth Health,
    double Activity,
    string Description);

/// <summary>World-space position in layout units. The canvas maps world to screen; one node is about 1 unit wide.</summary>
public readonly record struct ScenePoint(double X, double Y);

/// <summary>The whole environment at one logical instant.</summary>
/// <param name="IsHistorical">True when <paramref name="AsOf"/> was chosen by the user (time travel), false for live.</param>
public sealed record SceneGraph(
    DateTimeOffset AsOf,
    bool IsHistorical,
    IReadOnlyList<SceneNode> Nodes,
    IReadOnlyList<SceneLink> Links,
    IReadOnlyDictionary<string, ScenePoint> Layout)
{
    public static SceneGraph Empty { get; } = new(
        DateTimeOffset.UnixEpoch, false, [], [], new Dictionary<string, ScenePoint>());

    public SceneNode? Find(string? id) => id is null ? null : Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>Raised by the schematic when the user drags a node to a new place it should remember.</summary>
public sealed record SceneNodeMoved(string NodeId, ScenePoint Position);
