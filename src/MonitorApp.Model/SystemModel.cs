namespace MonitorApp.Model;

/// <summary>
/// The model as it stands at one point in time, seen either as the actual world or as the actual
/// world with one design layer on top. It is built only by replaying stored changes, so the same
/// code answers "now", "as of last Tuesday" and "in design B" (docs/ARCHITECTURE-MODEL.md 3.7, 3.11).
/// </summary>
public sealed class SystemModel
{
    private readonly Dictionary<string, ModelObject> objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Relationship> relationships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesignLayer> layers = new(StringComparer.Ordinal);
    private readonly Dictionary<(MonitoringSubjectKind, string), string> boundSubjects = [];
    private readonly List<LayerConflict> conflicts = [];

    internal SystemModel(ModelLibrary library, string? viewLayerId)
    {
        Library = library;
        ViewLayerId = viewLayerId;
    }

    public ModelLibrary Library { get; }

    /// <summary>The design layer this model shows on top of the actual world, or null for the actual world alone.</summary>
    public string? ViewLayerId { get; }

    public long LastSequence { get; private set; }

    public IReadOnlyCollection<ModelObject> Objects => objects.Values;
    public IReadOnlyCollection<Relationship> Relationships => relationships.Values;
    public IReadOnlyCollection<Connection> Connections => connections.Values;
    public IReadOnlyCollection<DesignLayer> Layers => layers.Values;

    /// <summary>Design changes that no longer fit the actual world (for example a part was retired after the design moved it). Kept visible, never dropped silently.</summary>
    public IReadOnlyList<LayerConflict> Conflicts => conflicts;

    public ModelObject? Find(string objectId) => objects.GetValueOrDefault(objectId);

    public ModelObject Get(string objectId) => Find(objectId) ?? throw new KeyNotFoundException($"No object '{objectId}'.");

    public ModelObject? FindBound(MonitoringSubjectKind kind, string subjectId) =>
        boundSubjects.TryGetValue((kind, subjectId), out var id) ? objects[id] : null;

    /// <summary>The containment relationship that holds <paramref name="objectId"/>, if any.</summary>
    public Relationship? ContainerOf(string objectId) =>
        relationships.Values.FirstOrDefault(r => r.TypeId == RelationshipTypes.Contains && r.TargetId == objectId);

    public IEnumerable<Relationship> ChildrenOf(string objectId) =>
        relationships.Values.Where(r => r.TypeId == RelationshipTypes.Contains && r.SourceId == objectId);

    public IEnumerable<Relationship> RelationshipsOf(string objectId) =>
        relationships.Values.Where(r => r.SourceId == objectId || r.TargetId == objectId);

    public IEnumerable<Connection> ConnectionsOf(string objectId) =>
        connections.Values.Where(c => c.A.ObjectId == objectId || c.B.ObjectId == objectId);

    public IEnumerable<Connection> ConnectionsAt(PortRef port) =>
        connections.Values.Where(c => c.A == port || c.B == port);

    /// <summary>Objects whose strongest value for a text property equals <paramref name="value"/> (ordinal, case-insensitive). Used for exact identity matches.</summary>
    public IEnumerable<ModelObject> WithText(string key, string value) =>
        objects.Values.Where(o => o.Strongest(key) is { Value.Type: PropertyValueType.Text } p &&
            string.Equals(p.Value.Text, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies a stored change. Returns null when applied, otherwise the reason it does not apply.</summary>
    internal string? Apply(ModelChangeRecord record)
    {
        if (record.LayerId is not null && !string.Equals(record.LayerId, ViewLayerId, StringComparison.Ordinal))
            return null;

        var error = Validate(record.Change, record.LayerId);
        if (error is null)
            Mutate(record);
        else if (record.LayerId is not null)
            conflicts.Add(new LayerConflict(record.Sequence, record.ChangeId, error));

        LastSequence = Math.Max(LastSequence, record.Sequence);
        return error;
    }

    private string? Validate(ModelChange change, string? layer)
    {
        if (layer is not null && !layers.ContainsKey(layer))
            return $"Design layer '{layer}' does not exist.";

        switch (change)
        {
            case ObjectCreated c:
                if (string.IsNullOrWhiteSpace(c.ObjectId)) return "An object needs an id.";
                if (string.IsNullOrWhiteSpace(c.Name)) return "An object needs a name.";
                if (objects.TryGetValue(c.ObjectId, out var existing))
                {
                    // An actual-world creation of an object first proposed in the viewed design adopts it (realization).
                    return layer is null && existing.CreatedInLayer is not null ? null : $"Object '{c.ObjectId}' already exists.";
                }
                if (layer is not null && c.Lifecycle > Lifecycle.Planned)
                    return "An object proposed in a design can be at most Planned; ordering and owning happen in the actual world.";
                return null;
            case ObjectRenamed c:
                return Exists(c.ObjectId) ?? (string.IsNullOrWhiteSpace(c.Name) ? "A name cannot be empty." : null);
            case ObjectKindChanged c:
                return Exists(c.ObjectId) ?? (string.IsNullOrWhiteSpace(c.KindId) ? "A kind cannot be empty." : null);
            case ObjectTagged c:
                return Exists(c.ObjectId) ?? (string.IsNullOrWhiteSpace(c.Tag) ? "A tag cannot be empty." : null);
            case ObjectUntagged c:
                return Exists(c.ObjectId);
            case PropertyAsserted c:
                return Exists(c.ObjectId) ?? ValidateProperty(c);
            case PropertyRetracted c:
                return Exists(c.ObjectId);
            case PortAdded c:
                {
                    if (Exists(c.ObjectId) is { } missing) return missing;
                    if (string.IsNullOrWhiteSpace(c.Port.PortId) || string.IsNullOrWhiteSpace(c.Port.Name)) return "A port needs an id and a name.";
                    var current = objects[c.ObjectId].Port(c.Port.PortId);
                    if (current is null || SamePort(current, c.Port)) return null;
                    return $"Port '{c.Port.PortId}' already exists on '{c.ObjectId}'.";
                }
            case PortRemoved c:
                if (Exists(c.ObjectId) is { } absent) return absent;
                if (objects[c.ObjectId].Port(c.PortId) is null) return $"Port '{c.PortId}' does not exist on '{c.ObjectId}'.";
                return ConnectionsAt(new PortRef(c.ObjectId, c.PortId)).Any()
                    ? $"Port '{c.PortId}' is connected; remove its connections first."
                    : null;
            case RelationshipAdded c:
                return ValidateRelationship(c);
            case RelationshipRemoved c:
                return relationships.ContainsKey(c.RelationshipId) ? null : $"Relationship '{c.RelationshipId}' does not exist.";
            case PlacementChanged c:
                if (!relationships.TryGetValue(c.RelationshipId, out var placed)) return $"Relationship '{c.RelationshipId}' does not exist.";
                return placed.TypeId == RelationshipTypes.Contains ? null : "Only containment carries a placement.";
            case ConnectionAdded c:
                return ValidateConnection(c);
            case ConnectionRemoved c:
                return connections.ContainsKey(c.ConnectionId) ? null : $"Connection '{c.ConnectionId}' does not exist.";
            case LifecycleMoved c:
                return ValidateLifecycle(c, layer);
            case MonitoringBound c:
                if (layer is not null) return "Monitoring identity belongs to the actual world, not a design.";
                if (Exists(c.ObjectId) is { } gone) return gone;
                if (string.IsNullOrWhiteSpace(c.SubjectId)) return "A monitoring subject id is required.";
                if (boundSubjects.TryGetValue((c.Kind, c.SubjectId), out var holder))
                    return holder == c.ObjectId ? null : $"Monitoring subject '{c.SubjectId}' is already bound to '{holder}'.";
                return null;
            case DesignLayerCreated c:
                if (layer is not null) return "Design layers are created in the actual world.";
                if (string.IsNullOrWhiteSpace(c.LayerId) || string.IsNullOrWhiteSpace(c.Name)) return "A design layer needs an id and a name.";
                if (layers.ContainsKey(c.LayerId)) return $"Design layer '{c.LayerId}' already exists.";
                return Exists(c.ProjectObjectId);
            default:
                return $"Unsupported change '{change.GetType().Name}'.";
        }
    }

    private string? Exists(string objectId) =>
        objects.ContainsKey(objectId) ? null : $"Object '{objectId}' does not exist.";

    private static bool SamePort(PortSpec a, PortSpec b) =>
        a with { Signals = null } == b with { Signals = null } &&
        (a.Signals ?? []).SequenceEqual(b.Signals ?? [], StringComparer.Ordinal);

    private string? ValidateProperty(PropertyAsserted c)
    {
        if (string.IsNullOrWhiteSpace(c.Key)) return "A property needs a key.";
        if (c.Value.ShapeError() is { } shape) return shape;
        if (string.IsNullOrWhiteSpace(c.Evidence.Source)) return "Every value needs a source.";
        if (c.Scope is not null && objects[c.ObjectId].Port(c.Scope) is null) return $"Port '{c.Scope}' does not exist on '{c.ObjectId}'.";

        // Library keys are checked so engines can trust them; unknown keys are stored as written.
        if (c.Value.Type is PropertyValueType.Unknown || !Library.TryGetProperty(c.Key, out var definition)) return null;
        if (definition!.Type != c.Value.Type) return $"'{c.Key}' expects a {definition.Type.ToString().ToLowerInvariant()} value.";
        if (definition.Dimension is { } dimension && c.Value.Quantity is { } q && Units.DimensionOf(q.Unit) != dimension)
            return $"'{c.Key}' needs a {dimension} unit; '{q.Unit}' is not one.";
        return null;
    }

    private string? ValidateRelationship(RelationshipAdded c)
    {
        if (string.IsNullOrWhiteSpace(c.RelationshipId) || string.IsNullOrWhiteSpace(c.TypeId)) return "A relationship needs an id and a type.";
        if (relationships.ContainsKey(c.RelationshipId)) return $"Relationship '{c.RelationshipId}' already exists.";
        if ((Exists(c.SourceId) ?? Exists(c.TargetId)) is { } missing) return missing;
        if (c.SourceId == c.TargetId) return "An object cannot relate to itself.";
        if (c.Placement is not null && c.TypeId != RelationshipTypes.Contains) return "Only containment carries a placement.";
        if (c.TypeId != RelationshipTypes.Contains) return null;

        if (objects[c.TargetId].Lifecycle is Lifecycle.Retired or Lifecycle.Disposed) return $"'{c.TargetId}' is retired and cannot be placed.";
        if (ContainerOf(c.TargetId) is { } holder) return $"'{c.TargetId}' is already inside '{holder.SourceId}'; move it instead of placing it twice.";
        for (var parent = c.SourceId; parent is not null; parent = ContainerOf(parent)?.SourceId)
            if (parent == c.TargetId) return "Containment cannot form a loop.";
        return null;
    }

    private string? ValidateConnection(ConnectionAdded c)
    {
        if (string.IsNullOrWhiteSpace(c.ConnectionId)) return "A connection needs an id.";
        if (connections.ContainsKey(c.ConnectionId)) return $"Connection '{c.ConnectionId}' already exists.";
        if (c.A == c.B) return "A port cannot connect to itself.";
        foreach (var end in new[] { c.A, c.B })
        {
            if (Exists(end.ObjectId) is { } missing) return missing;
            var owner = objects[end.ObjectId];
            if (owner.Port(end.PortId) is null) return $"Port '{end.PortId}' does not exist on '{end.ObjectId}'.";
            if (owner.Lifecycle is Lifecycle.Retired or Lifecycle.Disposed) return $"'{end.ObjectId}' is retired and cannot be connected.";
            if (c.Medium == ConnectionMedium.Physical && ConnectionsAt(end).Any(x => x.Medium == ConnectionMedium.Physical))
                return $"Port {end} already has a physical connection.";
        }
        return null;
    }

    private string? ValidateLifecycle(LifecycleMoved c, string? layer)
    {
        if (Exists(c.ObjectId) is { } missing) return missing;
        var target = objects[c.ObjectId];
        if (layer is not null && target.CreatedInLayer is null) return "A design cannot change the lifecycle of a real object.";
        if (layer is null && target.CreatedInLayer is not null) return "This object exists only in a design; realize it first.";
        if (layer is not null && c.To > Lifecycle.Planned) return "An object proposed in a design can be at most Planned.";
        return LifecycleRules.Check(target.Lifecycle, c.To);
    }

    private void Mutate(ModelChangeRecord record)
    {
        var at = record.RecordedAt;
        switch (record.Change)
        {
            case ObjectCreated c:
                if (objects.TryGetValue(c.ObjectId, out var adopted))
                {
                    adopted.CreatedInLayer = null;
                    adopted.Name = c.Name;
                    adopted.KindId = c.KindId ?? adopted.KindId;
                    if (adopted.Lifecycle != c.Lifecycle) adopted.LifecycleHistory.Add(new LifecycleStep(adopted.Lifecycle, c.Lifecycle, at, "Realized from design"));
                    adopted.Lifecycle = c.Lifecycle;
                }
                else
                {
                    objects[c.ObjectId] = new ModelObject(c.ObjectId, c.KindId ?? "thing", c.Name, c.Lifecycle, at, record.LayerId);
                }
                break;
            case ObjectRenamed c: objects[c.ObjectId].Name = c.Name; break;
            case ObjectKindChanged c: objects[c.ObjectId].KindId = c.KindId; break;
            case ObjectTagged c:
                if (!objects[c.ObjectId].TagList.Contains(c.Tag, StringComparer.Ordinal)) objects[c.ObjectId].TagList.Add(c.Tag);
                break;
            case ObjectUntagged c: objects[c.ObjectId].TagList.RemoveAll(t => t == c.Tag); break;
            case PropertyAsserted c:
                {
                    var list = objects[c.ObjectId].PropertyList;
                    // A newer value from the same source for the same property replaces the older one; other sources are kept.
                    list.RemoveAll(p => p.Key == c.Key && p.Scope == c.Scope && p.Evidence.Source == c.Evidence.Source);
                    list.Add(new PropertyAssertion(c.Key, c.Value, c.Evidence, at, c.Scope));
                    break;
                }
            case PropertyRetracted c:
                objects[c.ObjectId].PropertyList.RemoveAll(p => p.Key == c.Key && p.Scope == c.Scope && (c.Source is null || p.Evidence.Source == c.Source));
                break;
            case PortAdded c: objects[c.ObjectId].PortMap[c.Port.PortId] = c.Port; break;
            case PortRemoved c:
                objects[c.ObjectId].PortMap.Remove(c.PortId);
                objects[c.ObjectId].PropertyList.RemoveAll(p => p.Scope == c.PortId);
                break;
            case RelationshipAdded c:
                relationships[c.RelationshipId] = new Relationship(c.RelationshipId, c.TypeId, c.SourceId, c.TargetId, c.Placement, record.LayerId);
                break;
            case RelationshipRemoved c: relationships.Remove(c.RelationshipId); break;
            case PlacementChanged c: relationships[c.RelationshipId] = relationships[c.RelationshipId] with { Placement = c.Placement }; break;
            case ConnectionAdded c: connections[c.ConnectionId] = new Connection(c.ConnectionId, c.A, c.B, c.Medium, record.LayerId); break;
            case ConnectionRemoved c: connections.Remove(c.ConnectionId); break;
            case LifecycleMoved c:
                {
                    var target = objects[c.ObjectId];
                    target.LifecycleHistory.Add(new LifecycleStep(target.Lifecycle, c.To, at, c.Note));
                    target.Lifecycle = c.To;
                    break;
                }
            case MonitoringBound c:
                if (boundSubjects.TryAdd((c.Kind, c.SubjectId), c.ObjectId))
                    objects[c.ObjectId].BindingList.Add(new MonitoringBinding(c.Kind, c.SubjectId, at));
                break;
            case DesignLayerCreated c: layers[c.LayerId] = new DesignLayer(c.LayerId, c.ProjectObjectId, c.Name, at); break;
        }
    }
}

/// <summary>Which lifecycle moves make sense. Skipping ahead is allowed (people record things late); going back is limited to real reversals.</summary>
public static class LifecycleRules
{
    private static readonly HashSet<(Lifecycle From, Lifecycle To)> Reversals =
    [
        (Lifecycle.Candidate, Lifecycle.Idea),
        (Lifecycle.Planned, Lifecycle.Candidate),
        (Lifecycle.Planned, Lifecycle.Idea),
        (Lifecycle.Ordered, Lifecycle.Planned),   // order cancelled
        (Lifecycle.Installed, Lifecycle.Owned),   // taken out
        (Lifecycle.Retired, Lifecycle.Owned),     // brought back into use
    ];

    /// <summary>Returns null when the move is allowed, otherwise the reason.</summary>
    public static string? Check(Lifecycle from, Lifecycle to)
    {
        if (from == to) return $"Already {to}.";
        if (from == Lifecycle.Disposed) return "A disposed object stays disposed; its history is kept.";
        if (to > from || Reversals.Contains((from, to))) return null;
        return $"Cannot move from {from} back to {to}.";
    }
}
