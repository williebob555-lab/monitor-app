namespace MonitorApp.Contracts;

/// <summary>Physical-LAN entity kinds. Overlay (Tailscale/WireGuard) interfaces are a separate kind and never join LAN segments.</summary>
public enum LanEntityKind
{
    Peer = 1,
    NetworkInterface = 2,
    OverlayInterface = 3,
    Segment = 4,
    Vlan = 5,
    Gateway = 6,
    Switch = 7,
    Port = 8,
    Peripheral = 9,
    UnknownUpstreamSegment = 10,
}

public enum LanRelationshipKind
{
    AttachedTo = 1,
    MemberOfSegment = 2,
    MemberOfVlan = 3,
    RoutesVia = 4,
    UplinkTo = 5,
    ConnectedToPort = 6,
    ProbableSameSegment = 7,
}

public enum LanEvidenceKind
{
    Reported = 1,
    Inferred = 2,
}

public sealed record LanEntityRef(string EntityId, LanEntityKind Kind);

public sealed record LanRelationship(
    string RelationshipId,
    LanEntityRef Source,
    LanEntityRef Target,
    LanRelationshipKind Kind,
    LanEvidenceKind Evidence,
    double Confidence);

public static class LanTopologyValidator
{
    public static ContractValidationResult Validate(LanRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(relationship.RelationshipId)) errors.Add("Relationship id is required.");
        if (string.IsNullOrWhiteSpace(relationship.Source?.EntityId) || string.IsNullOrWhiteSpace(relationship.Target?.EntityId))
        {
            errors.Add("Relationship endpoints require entity ids.");
            return ContractValidationResult.Failure(errors);
        }

        if (!double.IsFinite(relationship.Confidence) || relationship.Confidence is < 0 or > 1) errors.Add("Confidence must be within 0..1.");
        if (!Enum.IsDefined(relationship.Evidence)) errors.Add("Evidence kind is undefined.");
        if (relationship.Source.EntityId == relationship.Target.EntityId) errors.Add("A relationship cannot be self-referential.");

        var s = relationship.Source.Kind;
        var t = relationship.Target.Kind;
        var allowed = relationship.Kind switch
        {
            LanRelationshipKind.AttachedTo =>
                (s is LanEntityKind.NetworkInterface or LanEntityKind.OverlayInterface && t == LanEntityKind.Peer)
                || (s is LanEntityKind.Peripheral or LanEntityKind.Port && t is LanEntityKind.Peer or LanEntityKind.Switch),
            LanRelationshipKind.MemberOfSegment => s == LanEntityKind.NetworkInterface && t == LanEntityKind.Segment,
            LanRelationshipKind.MemberOfVlan => s == LanEntityKind.NetworkInterface && t == LanEntityKind.Vlan,
            LanRelationshipKind.RoutesVia => s == LanEntityKind.Segment && t == LanEntityKind.Gateway,
            LanRelationshipKind.UplinkTo =>
                s is LanEntityKind.Segment or LanEntityKind.Port
                && t is LanEntityKind.Switch or LanEntityKind.Port or LanEntityKind.UnknownUpstreamSegment,
            LanRelationshipKind.ConnectedToPort => s == LanEntityKind.NetworkInterface && t == LanEntityKind.Port,
            LanRelationshipKind.ProbableSameSegment => s == LanEntityKind.Segment && t == LanEntityKind.Segment,
            _ => false,
        };
        if (!allowed) errors.Add($"{relationship.Kind} is not valid from {s} to {t}.");

        if (relationship.Kind == LanRelationshipKind.ProbableSameSegment &&
            (relationship.Evidence != LanEvidenceKind.Inferred || relationship.Confidence >= 1))
        {
            errors.Add("ProbableSameSegment is inferred and must have confidence below 1; it is never a hard merge.");
        }

        if (t == LanEntityKind.UnknownUpstreamSegment && relationship.Evidence != LanEvidenceKind.Inferred)
        {
            errors.Add("An unknown upstream segment placeholder can only be inferred.");
        }

        return errors.Count == 0 ? ContractValidationResult.Success() : ContractValidationResult.Failure(errors);
    }
}
