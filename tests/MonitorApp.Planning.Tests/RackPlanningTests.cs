using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

public sealed class RackPlanningTests
{
    private static RackZone Eia() => new()
    {
        Id = "rack",
        Format = RackFormat.Eia19,
        WidthMm = 482.6m,
        HeightMm = 12 * RackDimensions.RackUnitMm,
        UsableDepthMm = 650,
        FrontDoorClearanceMm = 35,
        RearReserveMm = 60,
        RackUnits = 12,
        HasRearRails = true,
        FrontToRearRailMm = 480,
        HolePattern = "square",
        MaxWeightKg = 60,
        HeatRemovalWatts = 500,
        PowerBudgetWatts = 1000,
    };

    private static RackEquipment Server(string id) => new()
    {
        Id = id,
        Name = id,
        MountFormat = RackFormat.Eia19,
        MountMethod = MountingMethod.FourPost,
        WidthMm = 440,
        HeightMm = 43,
        BodyDepthMm = 450,
        RearCableClearanceMm = 70,
        FrontProtrusionMm = 10,
        MinRailSpacingMm = 400,
        MaxRailSpacingMm = 550,
        RequiredHolePattern = "square",
        RackUnitsTall = 1,
        WeightKg = 8,
        PowerWatts = 160,
        HeatWatts = 130,
    };

    private static RackLayout Layout(
        RackZone? zone = null, IReadOnlyList<RackEquipment>? items = null,
        IReadOnlyList<RackPlacement>? positions = null) =>
        new("design-a", 2, [zone ?? Eia()], items ?? [Server("server")],
            positions ?? [RackDimensions.AtRackUnits("p", "rack", "server", 1)]);

    [Fact]
    public void Valid19Inch1UHasClearFrontAndRearAndStableProjection()
    {
        var layout = Layout();
        var result = RackLayoutValidator.Evaluate(layout);
        Assert.Empty(result.Findings);
        Assert.True(result.IsUnqualifiedFit);
        var scene = RackPanelProjection.Build(layout, "rack");
        Assert.True(scene.IsDesign);
        Assert.Equal(2, scene.Revision);
        Assert.Equal(RackDimensions.RackUnitMm, scene.VerticalGridPitchMm);
        Assert.Null(scene.HorizontalGridPitchMm);
        var item = Assert.Single(scene.Items);
        Assert.Equal(-10, item.FrontMm);
        Assert.Equal(520, item.BackMm);
        Assert.Equal(440, item.WidthMm);
    }

    [Fact]
    public void OverlappingBodiesAndRearServiceSpaceProduceDistinctFindings()
    {
        var s1 = Server("s1");
        var s2 = Server("s2") with { BodyDepthMm = 200, WeightKg = 2, HeatWatts = 10, PowerWatts = 10 };
        var layout = Layout(items: [s1, s2], positions:
        [
            RackDimensions.AtRackUnits("one", "rack", "s1", 1),
            RackDimensions.AtRackUnits("two", "rack", "s2", 1),
        ]);
        Assert.Contains(RackLayoutValidator.Evaluate(layout).Findings, f => f.Code == "volume-collision");

        var tooDeep = Layout(items: [s1 with { BodyDepthMm = 600 }]);
        Assert.Contains(RackLayoutValidator.Evaluate(tooDeep).Findings, f => f.Code == "rear-clearance");
    }

    [Fact]
    public void WidthFormatAndRailStandardsAreNotAutomaticallyInterchangeable()
    {
        var mismatched = Layout(items: [Server("server") with { MountFormat = RackFormat.Eia23 }]);
        Assert.Contains(RackLayoutValidator.Evaluate(mismatched).Findings, x => x.Code == "mount-interface");
        var withoutRear = Layout(zone: Eia() with { HasRearRails = false, FrontToRearRailMm = null });
        var results = RackLayoutValidator.Evaluate(withoutRear).Findings;
        Assert.Contains(results, x => x.Code == "rear-rails-missing");
        Assert.Contains(results, x => x.Code == "unknown-rail-depth");
        Assert.Equal(254m, RackDimensions.NominalPanelWidthMm(RackFormat.Eia10));
        Assert.Equal(584.2m, RackDimensions.NominalPanelWidthMm(RackFormat.Eia23));
        Assert.Null(RackDimensions.NominalPanelWidthMm(RackFormat.OpenRack21));
    }

    [Fact]
    public void EurorackHpRowsAndNested3uAdapterRemainDifferentMountInterfaces()
    {
        var outer = Eia();
        var chassis = Server("euro-case") with
        {
            Name = "Eurorack chassis in 19in shelf",
            HeightMm = 3 * RackDimensions.RackUnitMm,
            RackUnitsTall = 3,
            InternalWidthMm = 84 * RackDimensions.EurorackHpMm,
            InternalHeightMm = 133.35m,
            InternalDepthMm = 80,
        };
        var inner = new RackZone
        {
            Id = "euro",
            Format = RackFormat.Eurorack,
            ParentPlacementId = "case",
            WidthMm = 84 * RackDimensions.EurorackHpMm,
            HeightMm = 133.35m,
            UsableDepthMm = 80,
            EurorackHpPerRow = 84,
            EurorackRows = 1,
            EurorackRowPitchMm = 133.35m,
            MaxWeightKg = 10,
            PowerBudgetWatts = 100,
            HeatRemovalWatts = 70,
        };
        var module = new RackEquipment
        {
            Id = "module",
            Name = "8HP sound module",
            MountFormat = RackFormat.Eurorack,
            MountMethod = MountingMethod.FrontRails,
            WidthMm = 8 * RackDimensions.EurorackHpMm,
            HeightMm = RackDimensions.EurorackThreeUPanelHeightMm,
            BodyDepthMm = 40,
            EurorackHpWide = 8,
            EurorackRowsTall = 1,
            WeightKg = 0.4m,
            PowerWatts = 5,
            HeatWatts = 5,
        };
        var layout = new RackLayout("design-a", 2, [outer, inner], [chassis, module],
        [
            RackDimensions.AtRackUnits("case", "rack", chassis.Id, 1),
            RackDimensions.AtEurorack("module-p", "euro", module.Id, 9, 1, 133.35m),
        ]);
        Assert.True(RackLayoutValidator.Evaluate(layout).IsUnqualifiedFit);
        var panel = RackPanelProjection.Build(layout, "euro");
        Assert.Equal(RackDimensions.EurorackHpMm, panel.HorizontalGridPitchMm);
        Assert.Equal(8 * RackDimensions.EurorackHpMm, Assert.Single(panel.Items).LeftMm);

        var illegallyPlaced = layout with
        {
            Placements = [RackDimensions.AtRackUnits("bad", "rack", module.Id, 4)],
        };
        Assert.Contains(RackLayoutValidator.Evaluate(illegallyPlaced).Findings, x => x.Code == "mount-interface");
    }

    [Fact]
    public void MissingOrStaleSpecificationsNeverAppearAsSafeFit()
    {
        var layout = Layout(items: [Server("server") with { BodyDepthMm = null, HeatWatts = null }]);
        var assessed = RackLayoutValidator.Evaluate(layout);
        Assert.True(assessed.HasUnknowns);
        Assert.False(assessed.IsUnqualifiedFit);
        Assert.Contains(assessed.Findings, f => f.Code == "unknown-equipment-dimensions");
        Assert.Contains(assessed.Findings, f => f.Code == "unknown-heat-load");
        Assert.Single(RackPanelProjection.Build(layout, "rack").PlacementsWithUnknownGeometry);
    }

    [Fact]
    public void RackBudgetsArePerRegionAndUnknownIsNotZero()
    {
        var server = Server("server") with { WeightKg = 80, PowerWatts = 1100, HeatWatts = 520 };
        var findings = RackLayoutValidator.Evaluate(Layout(items: [server])).Findings;
        Assert.Contains(findings, f => f.Code == "weight-over-budget");
        Assert.Contains(findings, f => f.Code == "power-over-budget");
        Assert.Contains(findings, f => f.Code == "heat-over-budget");
    }
}
