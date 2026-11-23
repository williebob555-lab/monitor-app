using System.Text.Json.Serialization;

namespace MonitorApp.Model;

/// <summary>
/// One change to the model. Changes are never edited or deleted; every state of the model,
/// past or present, actual or design, is rebuilt by replaying them (docs/ARCHITECTURE-MODEL.md 3.11).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ObjectCreated), "object.created")]
[JsonDerivedType(typeof(ObjectRenamed), "object.renamed")]
[JsonDerivedType(typeof(ObjectKindChanged), "object.kind-changed")]
[JsonDerivedType(typeof(ObjectTagged), "object.tagged")]
[JsonDerivedType(typeof(ObjectUntagged), "object.untagged")]
[JsonDerivedType(typeof(PropertyAsserted), "property.asserted")]
[JsonDerivedType(typeof(PropertyRetracted), "property.retracted")]
[JsonDerivedType(typeof(PortAdded), "port.added")]
[JsonDerivedType(typeof(PortRemoved), "port.removed")]
[JsonDerivedType(typeof(RelationshipAdded), "relationship.added")]
[JsonDerivedType(typeof(RelationshipRemoved), "relationship.removed")]
[JsonDerivedType(typeof(PlacementChanged), "relationship.placement-changed")]
[JsonDerivedType(typeof(ConnectionAdded), "connection.added")]
[JsonDerivedType(typeof(ConnectionRemoved), "connection.removed")]
[JsonDerivedType(typeof(LifecycleMoved), "lifecycle.moved")]
[JsonDerivedType(typeof(MonitoringBound), "monitoring.bound")]
[JsonDerivedType(typeof(DesignLayerCreated), "layer.created")]
public abstract record ModelChange;

public sealed record ObjectCreated(string ObjectId, string Name, string? KindId = null, Lifecycle Lifecycle = Lifecycle.Idea) : ModelChange;
public sealed record ObjectRenamed(string ObjectId, string Name) : ModelChange;
public sealed record ObjectKindChanged(string ObjectId, string KindId) : ModelChange;
public sealed record ObjectTagged(string ObjectId, string Tag) : ModelChange;
public sealed record ObjectUntagged(string ObjectId, string Tag) : ModelChange;

/// <param name="Scope">Optional sub-target, for example a port id, so ports can carry electrical limits with evidence.</param>
public sealed record PropertyAsserted(string ObjectId, string Key, PropertyValue Value, Evidence Evidence, string? Scope = null) : ModelChange;

/// <summary>Withdraws every value of a property from one source (or all sources when <paramref name="Source"/> is null).</summary>
public sealed record PropertyRetracted(string ObjectId, string Key, string? Source = null, string? Scope = null) : ModelChange;

public sealed record PortAdded(string ObjectId, PortSpec Port) : ModelChange;
public sealed record PortRemoved(string ObjectId, string PortId) : ModelChange;
public sealed record RelationshipAdded(string RelationshipId, string TypeId, string SourceId, string TargetId, Placement? Placement = null) : ModelChange;
public sealed record RelationshipRemoved(string RelationshipId) : ModelChange;
public sealed record PlacementChanged(string RelationshipId, Placement Placement) : ModelChange;
public sealed record ConnectionAdded(string ConnectionId, PortRef A, PortRef B, ConnectionMedium Medium = ConnectionMedium.Physical) : ModelChange;
public sealed record ConnectionRemoved(string ConnectionId) : ModelChange;
public sealed record LifecycleMoved(string ObjectId, Lifecycle To, string? Note = null) : ModelChange;
public sealed record MonitoringBound(string ObjectId, MonitoringSubjectKind Kind, string SubjectId) : ModelChange;
public sealed record DesignLayerCreated(string LayerId, string ProjectObjectId, string Name) : ModelChange;

/// <summary>A stored change: the change plus when, where (actual world or a design layer) and by whom.</summary>
public sealed record ModelChangeRecord(
    long Sequence,
    string ChangeId,
    DateTimeOffset RecordedAt,
    string? LayerId,
    string Origin,
    ModelChange Change);
