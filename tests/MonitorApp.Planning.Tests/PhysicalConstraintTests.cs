using MonitorApp.Planning.Constraints;
using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class PhysicalConstraintTests
{
    private static RackLayout Layout() =>
        new("design", 1,
            [new RackZone
            {
                Id = "rack", Format = RackFormat.Eia19,
                WidthMm = 482.6m, HeightMm = 533.4m, UsableDepthMm = 650,
                RackUnits = 12, MaxWeightKg = 120, PowerBudgetWatts = 1000,
                HeatRemovalWatts = 500
            }],
            [new RackEquipment
            {
                Id = "server", Name = "server",
                MountFormat = RackFormat.Eia19, MountMethod = MountingMethod.FrontRails,
                WidthMm = 440, HeightMm = 88, BodyDepthMm = 430,
                RackUnitsTall = 2, WeightKg = 12, PowerWatts = 350, HeatWatts = 290
            }],
            [RackDimensions.AtRackUnits("p1", "rack", "server", 2)]);

    private static RackPhysicalContext Context(
        IReadOnlyList<VentilationProfile>? vents = null,
        IReadOnlyList<ThermalEnvironment>? thermal = null,
        IReadOnlyList<ElectricalFeed>? feeds = null,
        IReadOnlyList<InterferenceExposure>? emi = null,
        IReadOnlyList<PhysicalCableRun>? cables = null,
        IReadOnlyList<AcousticZone>? acoustic = null) =>
        new(Layout(), vents ?? [], thermal ?? [], feeds ?? [], emi ?? [], cables ?? [], acoustic ?? []);

    [Fact]
    public void ThermalUnknownIsNotSafeAndMeasuredHeatCanExposeCapacityExcess()
    {
        var unknown = new RackPhysicalEvaluator().Evaluate(Context());
        Assert.Contains(unknown, x => x.RuleId == "thermal-envelope" && x.Level == PhysicalFindingLevel.Unresolved);

        var result = new RackPhysicalEvaluator().Evaluate(Context(
            thermal: [new ThermalEnvironment("rack", 28, 32, 200, 20, PhysicalEvidence.Estimated)]));
        Assert.Contains(result, x => x.RuleId == "thermal-envelope" &&
                                     x.Level == PhysicalFindingLevel.Warning &&
                                     x.Description.Contains("removal capacity"));
        Assert.Contains(result, x => x.RuleId == "thermal-envelope" &&
                                     x.Level == PhysicalFindingLevel.Warning &&
                                     x.Description.Contains("First-order"));
    }

    [Fact]
    public void AirflowDirectionAndRequiredClearancesAreEvaluatedIndependently()
    {
        var findings = new RackPhysicalEvaluator().Evaluate(Context(
            vents: [new VentilationProfile("server", AirflowDirection.FrontToRear, 10, 30,
                30, 25, null, PhysicalEvidence.ManufacturerSpecification)]));
        Assert.Contains(findings, f => f.RuleId == "airflow-clearance" &&
                                       f.Level == PhysicalFindingLevel.Warning);
        var unknown = new RackPhysicalEvaluator().Evaluate(Context(
            vents: [new VentilationProfile("server", AirflowDirection.Unknown, null, null,
                null, null, null, PhysicalEvidence.UserEntered)]));
        Assert.Contains(unknown, x => x.Level == PhysicalFindingLevel.Unresolved &&
                                      x.Description.Contains("direction"));
    }

    [Fact]
    public void ElectricalDemandUsesBothConfiguredWAndAmpsRatherThanConflatingRatings()
    {
        var findings = new RackPhysicalEvaluator().Evaluate(Context(
            feeds: [new ElectricalFeed("pdu-1", 300, 120, 2, 0.8m, ["p1"], PhysicalEvidence.UserEntered)]));
        Assert.Contains(findings, x => x.RuleId == "electrical-feed" &&
                                       x.Level == PhysicalFindingLevel.Critical);

        var missing = new RackPhysicalEvaluator().Evaluate(Context(
            feeds: [new ElectricalFeed("pdu-1", 500, 120, null, null, ["p1"], PhysicalEvidence.UserEntered)]));
        Assert.Contains(missing, x => x.RuleId == "electrical-feed" &&
                                     x.Level == PhysicalFindingLevel.Unresolved);
    }

    [Fact]
    public void EmiRiskNeedsExplicitLocalTargetAndNeverReturnsFalseCertifiedCompatibility()
    {
        var risks = new RackPhysicalEvaluator().Evaluate(Context(
            emi: [new InterferenceExposure("AC-power", "instrument-input", "mains-to-analog",
                30, null, 150, null, PhysicalEvidence.UserEntered)]));
        Assert.Contains(risks, x => x.RuleId == "emi-rf-proximity" &&
                                   x.Level == PhysicalFindingLevel.Warning);
        Assert.Contains(risks, x => x.Level == PhysicalFindingLevel.Unresolved &&
                                   x.Description.Contains("Shield"));
        var unknown = new RackPhysicalEvaluator().Evaluate(Context(
            emi: [new InterferenceExposure("RF-antenna", "receiver", "RF desense",
                null, 140, null, true, PhysicalEvidence.UserEntered)]));
        Assert.Contains(unknown, x => x.Level == PhysicalFindingLevel.Unresolved);
    }

    [Fact]
    public void CableRoutingChecksBendRadiusSlackAndUnknownLengths()
    {
        var findings = new RackPhysicalEvaluator().Evaluate(Context(
            cables: [new PhysicalCableRun("cat6-a", 900, 830, 150, 20, 35, PhysicalEvidence.UserEntered),
                     new PhysicalCableRun("fiber-a", null, 500, 50, null, 55, PhysicalEvidence.UserEntered)]));
        Assert.Contains(findings, x => x.RuleId == "cable-routing" &&
                                       x.Description.Contains("too short"));
        Assert.Contains(findings, x => x.RuleId == "cable-routing" &&
                                       x.Description.Contains("bend radius"));
        Assert.Contains(findings, x => x.RuleId == "cable-routing" &&
                                       x.Level == PhysicalFindingLevel.Unresolved);
    }

    [Fact]
    public void SoundAndMechanicalStabilityRemainMeasuredNotInvented()
    {
        var findings = new RackPhysicalEvaluator().Evaluate(Context(
            acoustic: [new AcousticZone("rack", 35, null, PhysicalEvidence.ManufacturerSpecification)]));
        Assert.Contains(findings, x => x.RuleId == "acoustic-comfort" &&
                                       x.Level == PhysicalFindingLevel.Unresolved);
        Assert.Contains(findings, x => x.RuleId == "rack-stability" &&
                                       x.Level == PhysicalFindingLevel.Unresolved);
    }
}
