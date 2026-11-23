using MonitorApp.Planning.Audio;
using MonitorApp.Planning.Projects;
using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class ProjectPlanComparisonTests
{
    [Fact]
    public void ComparisonRequiresSameScenarioAndForwardRevision()
    {
        var baseline = EmptyScenario(1);

        Assert.Throws<ArgumentException>(() =>
            ProjectPlanComparer.Compare(baseline, EmptyScenario(2) with { ProjectId = "another-project" }));
        Assert.Throws<ArgumentException>(() =>
            ProjectPlanComparer.Compare(baseline, EmptyScenario(2) with { ScenarioId = "another-scenario" }));
        Assert.Throws<ArgumentException>(() =>
            ProjectPlanComparer.Compare(baseline, EmptyScenario(1)));
    }

    [Fact]
    public void DuplicateStableIdentifiersFailClearlyInsteadOfCollapsingObjects()
    {
        var duplicatePart = new PlanPart("part", "Duplicate", 1, null, null, null, null);
        var baseline = EmptyScenario(1) with
        {
            Parts = [duplicatePart, duplicatePart with { Name = "Other value" }],
        };

        var error = Assert.Throws<ArgumentException>(() =>
            ProjectPlanComparer.Compare(baseline, EmptyScenario(2)));

        Assert.Contains("duplicate stable identifier 'part'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingOrRemovingAPlanIncludesItsContainedObjects()
    {
        var baseline = EmptyScenario(1);
        var planned = PlannedScenario(2);

        var added = ProjectPlanComparer.Compare(baseline, planned).ObjectChanges;

        AssertChange(added, "rack-plan", "scenario", PlanChangeKind.Added);
        AssertChange(added, "rack-zone", "rack", PlanChangeKind.Added);
        AssertChange(added, "rack-equipment", "device", PlanChangeKind.Added);
        AssertChange(added, "rack-placement", "placement", PlanChangeKind.Added);
        AssertChange(added, "audio-plan", "scenario", PlanChangeKind.Added);
        AssertChange(added, "audio-device", "device", PlanChangeKind.Added);
        AssertChange(added, "audio-port", "port", PlanChangeKind.Added);
        AssertChange(added, "audio-cable", "cable", PlanChangeKind.Added);
        AssertChange(added, "audio-patch", "patch", PlanChangeKind.Added);
        AssertChange(added, "dc-load", "load", PlanChangeKind.Added);
        AssertChange(added, "dc-output", "output", PlanChangeKind.Added);
        AssertChange(added, "dc-assignment", "load", PlanChangeKind.Added);
        AssertChange(added, "audio-latency-budget", "scenario", PlanChangeKind.Added);

        var removed = ProjectPlanComparer.Compare(planned, EmptyScenario(3)).ObjectChanges;
        foreach (var change in added)
            AssertChange(removed, change.Category, change.ObjectId, PlanChangeKind.Removed);
    }

    [Fact]
    public void EquivalentCollectionOrderDoesNotCreateChangesAndResultsAreDeterministic()
    {
        var first = EmptyScenario(1) with
        {
            Requirements =
            [
                new PlanRequirement("first", "First"),
                new PlanRequirement("second", "Second"),
            ],
            Parts =
            [
                new PlanPart("a", "A", 1, null, null, "actual:a", "first"),
                new PlanPart("b", "B", 1, null, null, "actual:b", "second"),
            ],
            Tasks =
            [
                new PlanTask("prepare", "Prepare", ProjectWorkState.Completed, [], ["a", "b"]),
                new PlanTask("install", "Install", ProjectWorkState.NotStarted, ["prepare"], ["a", "b"]),
            ],
        };
        var reordered = first with
        {
            Revision = 2,
            Requirements = first.Requirements.Reverse().ToArray(),
            Parts = first.Parts.Reverse().ToArray(),
            Tasks = first.Tasks.Reverse()
                .Select(x => x with { RelatedPartIds = x.RelatedPartIds.Reverse().ToArray() }).ToArray(),
        };

        var comparison = ProjectPlanComparer.Compare(first, reordered);
        var repeated = ProjectPlanComparer.Compare(first, reordered);

        Assert.Empty(comparison.ObjectChanges);
        Assert.Empty(comparison.FindingChanges);
        Assert.Empty(comparison.CostChanges);
        Assert.Equal(comparison.ObjectChanges, repeated.ObjectChanges);
        Assert.Equal(comparison.FindingChanges, repeated.FindingChanges);
        Assert.Equal(comparison.CostChanges, repeated.CostChanges);
    }

    private static ProjectScenario EmptyScenario(int revision) => new(
        "project", "scenario", revision, "Scenario", [], [], [], []);

    private static ProjectScenario PlannedScenario(int revision)
    {
        var rack = new RackLayout("scenario", revision,
            [new RackZone
            {
                Id = "rack",
                Format = RackFormat.Eia19,
                WidthMm = 482.6m,
                HeightMm = RackDimensions.RackUnitMm,
                UsableDepthMm = 400,
                RackUnits = 1,
            }],
            [new RackEquipment
            {
                Id = "device",
                Name = "Device",
                MountFormat = RackFormat.Eia19,
                MountMethod = MountingMethod.FrontRails,
                WidthMm = 440,
                HeightMm = 43,
                BodyDepthMm = 200,
                RackUnitsTall = 1,
            }],
            [RackDimensions.AtRackUnits("placement", "rack", "device", 1)]);
        var audio = new AudioPlan("scenario", revision,
            [new AudioDevice("device", "Device")],
            [new AudioPort
            {
                Id = "port",
                DeviceId = "device",
                Direction = AudioPortDirection.Output,
                Kind = AudioSignalKind.AnalogAudio,
                Connector = AudioConnector.Trs635,
                Level = AudioLevel.ProfessionalLine,
            }],
            [new AudioCable
            {
                Id = "cable",
                EndA = AudioConnector.Trs635,
                EndB = AudioConnector.Trs635,
            }],
            [new AudioPatch("patch", "port", "missing-input", "cable")],
            [new DcLoad
            {
                Id = "load",
                DeviceId = "device",
                Connector = AudioConnector.BarrelDc,
                Polarity = PowerPolarity.CenterNegative,
            }],
            [new DcSupplyOutput
            {
                Id = "output",
                SupplyDeviceId = "device",
                Connector = AudioConnector.BarrelDc,
                Polarity = PowerPolarity.CenterNegative,
            }],
            [new DcPowerAssignment("load", "output")],
            MaximumDesiredChainLatencyMs: 10);
        return EmptyScenario(revision) with { RackPlan = rack, AudioPlan = audio };
    }

    private static void AssertChange(
        IReadOnlyList<PlanObjectChange> changes,
        string category,
        string objectId,
        PlanChangeKind kind) =>
        Assert.Contains(changes,
            x => x.Category == category && x.ObjectId == objectId && x.Kind == kind);
}
