using MonitorApp.Contracts;

namespace MonitorApp.Contracts.Tests;

public sealed class LanTopologyValidatorTests
{
    private static LanEntityRef E(string id, LanEntityKind k) => new(id, k);

    [Fact]
    public void AcceptsInterfaceMembershipAndSegmentGateway()
    {
        Assert.True(LanTopologyValidator.Validate(new("r1", E("i", LanEntityKind.NetworkInterface), E("s", LanEntityKind.Segment), LanRelationshipKind.MemberOfSegment, LanEvidenceKind.Reported, 0.9)).IsValid);
        Assert.True(LanTopologyValidator.Validate(new("r2", E("s", LanEntityKind.Segment), E("g", LanEntityKind.Gateway), LanRelationshipKind.RoutesVia, LanEvidenceKind.Reported, 0.8)).IsValid);
        Assert.True(LanTopologyValidator.Validate(new("r3", E("p", LanEntityKind.Port), E("sw", LanEntityKind.Switch), LanRelationshipKind.AttachedTo, LanEvidenceKind.Reported, 0.9)).IsValid);
    }

    [Fact]
    public void RejectsOverlayInterfaceJoiningSegmentOrVlan()
    {
        Assert.False(LanTopologyValidator.Validate(new("r", E("o", LanEntityKind.OverlayInterface), E("s", LanEntityKind.Segment), LanRelationshipKind.MemberOfSegment, LanEvidenceKind.Reported, 0.9)).IsValid);
        Assert.False(LanTopologyValidator.Validate(new("r", E("o", LanEntityKind.OverlayInterface), E("v", LanEntityKind.Vlan), LanRelationshipKind.MemberOfVlan, LanEvidenceKind.Reported, 0.9)).IsValid);
    }

    [Fact]
    public void ProbableSameSegmentMustBeInferredAndBelowOne()
    {
        var a = E("a", LanEntityKind.Segment);
        var b = E("b", LanEntityKind.Segment);
        Assert.True(LanTopologyValidator.Validate(new("r", a, b, LanRelationshipKind.ProbableSameSegment, LanEvidenceKind.Inferred, 0.6)).IsValid);
        Assert.False(LanTopologyValidator.Validate(new("r", a, b, LanRelationshipKind.ProbableSameSegment, LanEvidenceKind.Reported, 0.6)).IsValid);
        Assert.False(LanTopologyValidator.Validate(new("r", a, b, LanRelationshipKind.ProbableSameSegment, LanEvidenceKind.Inferred, 1.0)).IsValid);
    }

    [Fact]
    public void UnknownUpstreamPlaceholderCannotBeReportedAndConfidenceIsBounded()
    {
        var s = E("s", LanEntityKind.Segment);
        var u = E("u", LanEntityKind.UnknownUpstreamSegment);
        Assert.True(LanTopologyValidator.Validate(new("r", s, u, LanRelationshipKind.UplinkTo, LanEvidenceKind.Inferred, 0.2)).IsValid);
        Assert.False(LanTopologyValidator.Validate(new("r", s, u, LanRelationshipKind.UplinkTo, LanEvidenceKind.Reported, 0.2)).IsValid);
        Assert.False(LanTopologyValidator.Validate(new("r", s, u, LanRelationshipKind.UplinkTo, LanEvidenceKind.Inferred, 1.5)).IsValid);
    }
}
