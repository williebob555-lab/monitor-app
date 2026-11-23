namespace MonitorApp.Model;

/// <summary>Where an object is in its life. Online/offline are observations, never lifecycle (docs/ARCHITECTURE-MODEL.md 3.6).</summary>
public enum Lifecycle
{
    Idea = 1,
    Candidate = 2,
    Planned = 3,
    Ordered = 4,
    Owned = 5,
    Installed = 6,
    Retired = 7,
    Disposed = 8,
}

public enum PortDirection
{
    Unknown = 0,
    Output = 1,
    Input = 2,
    Both = 3,
}

public enum PortGender
{
    Unknown = 0,
    Plug = 1,
    Socket = 2,
    Genderless = 3,
}

public enum PortFace
{
    Unspecified = 0,
    Front = 1,
    Rear = 2,
    Top = 3,
    Bottom = 4,
    Left = 5,
    Right = 6,
    Internal = 7,
}

/// <summary>How two ports are joined. Physical mates are exclusive; wireless and logical links are not.</summary>
public enum ConnectionMedium
{
    Physical = 1,
    Wireless = 2,
    Logical = 3,
}

/// <summary>Position, in millimetres, relative to an origin on a face of the parent.</summary>
public sealed record Point3(double X, double Y, double Z);

/// <summary>
/// A port: a place where something connects. Each compatibility layer is separate:
/// physical (connector id from the shared connector library + gender), signal/protocol
/// capabilities, direction, and position. Electrical limits are port properties with evidence.
/// </summary>
public sealed record PortSpec(
    string PortId,
    string Name,
    string? ConnectorTypeId = null,
    PortGender Gender = PortGender.Unknown,
    PortDirection Direction = PortDirection.Unknown,
    IReadOnlyList<string>? Signals = null,
    PortFace Face = PortFace.Unspecified,
    Point3? Position = null);

/// <summary>Where a child sits inside its container (docs/ARCHITECTURE-MODEL.md 3.5).</summary>
/// <param name="Slot">Mounting-grid position when the parent has one, for example "12U" or "row2:HP14". Null for free placement.</param>
/// <param name="Rotation">Rotation about the vertical axis in degrees.</param>
public sealed record Placement(PortFace Face = PortFace.Front, Point3? Position = null, double Rotation = 0, string? Slot = null);

public sealed record PropertyAssertion(string Key, PropertyValue Value, Evidence Evidence, DateTimeOffset RecordedAt, string? Scope = null);

/// <summary>Monitoring identity bound to a model object. The monitoring id is never replaced.</summary>
public enum MonitoringSubjectKind
{
    Peer = 1,
    ObservedDevice = 2,
}

public sealed record MonitoringBinding(MonitoringSubjectKind Kind, string SubjectId, DateTimeOffset BoundAt);

public sealed class ModelObject
{
    internal ModelObject(string id, string kindId, string name, Lifecycle lifecycle, DateTimeOffset createdAt, string? createdInLayer)
    {
        Id = id;
        KindId = kindId;
        Name = name;
        Lifecycle = lifecycle;
        CreatedAt = createdAt;
        CreatedInLayer = createdInLayer;
    }

    public string Id { get; }
    public string KindId { get; internal set; }
    public string Name { get; internal set; }
    public Lifecycle Lifecycle { get; internal set; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>The design layer the object was proposed in, or null when it exists in the actual world.</summary>
    public string? CreatedInLayer { get; internal set; }

    internal List<string> TagList { get; } = [];
    internal List<PropertyAssertion> PropertyList { get; } = [];
    internal Dictionary<string, PortSpec> PortMap { get; } = new(StringComparer.Ordinal);
    internal List<MonitoringBinding> BindingList { get; } = [];
    internal List<LifecycleStep> LifecycleHistory { get; } = [];

    public IReadOnlyList<string> Tags => TagList;
    public IReadOnlyList<PropertyAssertion> Properties => PropertyList;
    public IReadOnlyCollection<PortSpec> Ports => PortMap.Values;
    public IReadOnlyList<MonitoringBinding> MonitoringBindings => BindingList;
    public IReadOnlyList<LifecycleStep> LifecycleSteps => LifecycleHistory;

    /// <summary>The strongest value for a property: highest evidence level, then the most recent.</summary>
    public PropertyAssertion? Strongest(string key) =>
        PropertyList.Where(p => string.Equals(p.Key, key, StringComparison.Ordinal))
            .OrderByDescending(p => p.Evidence.Level)
            .ThenByDescending(p => p.RecordedAt)
            .FirstOrDefault();

    public IEnumerable<PropertyAssertion> AllValues(string key) =>
        PropertyList.Where(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    public PortSpec? Port(string portId) => PortMap.GetValueOrDefault(portId);
}

public sealed record LifecycleStep(Lifecycle From, Lifecycle To, DateTimeOffset At, string? Note);

public sealed record Relationship(string RelationshipId, string TypeId, string SourceId, string TargetId, Placement? Placement, string? CreatedInLayer);

public sealed record PortRef(string ObjectId, string PortId)
{
    public override string ToString() => $"{ObjectId}#{PortId}";
}

public sealed record Connection(string ConnectionId, PortRef A, PortRef B, ConnectionMedium Medium, string? CreatedInLayer);

public sealed record DesignLayer(string LayerId, string ProjectObjectId, string Name, DateTimeOffset CreatedAt);

/// <summary>A layer change that no longer applies on top of the current actual world, kept visible instead of silently dropped.</summary>
public sealed record LayerConflict(long Sequence, string ChangeId, string Reason);

/// <summary>Built-in relationship types. Others are allowed; these are the ones the model itself enforces.</summary>
public static class RelationshipTypes
{
    /// <summary>Physical containment: a child has at most one container and containment never forms a cycle.</summary>
    public const string Contains = "contains";
    public const string InstanceOf = "instance-of";
    public const string OfferedAs = "offered-as";
    public const string PartOfProject = "part-of-project";
    public const string CandidateFor = "candidate-for";
    public const string Satisfies = "satisfies";
    public const string DependsOn = "depends-on";
    public const string PoweredBy = "powered-by";
    public const string HostedOn = "hosted-on";
    public const string About = "about";
    public const string Replaces = "replaces";
}
