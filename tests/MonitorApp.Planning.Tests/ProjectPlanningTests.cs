using MonitorApp.Planning.Projects;
using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class ProjectPlanningTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProjectScenario Scenario(PlanOfferSnapshot? offer = null) =>
        new("project:rack", "candidate-a", 4, "Rack reorganization",
            [new PlanRequirement("r1", "Provide redundant power"),
             new PlanRequirement("r2", "Improve cabling", Required: false)],
            [new PlanPart("ups", "UPS", 2, "product:ups-model", "offer:ups", null, "r1")],
            [new PlanTask("measure", "Measure current rack", ProjectWorkState.NotStarted, [], []),
             new PlanTask("install", "Install UPS", ProjectWorkState.NotStarted, ["measure"], ["ups"])],
            [offer ?? new PlanOfferSnapshot("offer:ups", "product:ups-model", "example seller", "USD",
                100m, null, null, OfferAvailability.InStock, ObservedAt, "fixture", SourceKind.UserEntered, null)]);

    [Fact]
    public void TaskGraphFindsReadyWorkWithoutTreatingPlanAsActualInventory()
    {
        var plan = Scenario();
        var result = ProjectScenarioEvaluator.Evaluate(plan);
        Assert.Equal("candidate-a", result.ScenarioId);
        Assert.Equal(4, result.Revision);
        Assert.Equal(["measure"], result.ReadyTaskIds);
        Assert.False(result.HasErrors);
        var cost = Assert.Single(result.Costs);
        Assert.Equal(200m, cost.KnownUnitSubtotal);
        Assert.Null(cost.GrandTotal); // shipping and tax are unknown, not $0.
        Assert.Contains("offer:ups", cost.MissingChargesOfferIds);
        Assert.Null(Assert.Single(plan.Parts).ExistingInstanceReferenceId);
    }

    [Fact]
    public void TaskCycleAndMissingDependenciesAreErrors()
    {
        var plan = Scenario() with
        {
            Tasks =
            [
                new PlanTask("a", "A", ProjectWorkState.NotStarted, ["b"], []),
                new PlanTask("b", "B", ProjectWorkState.NotStarted, ["a", "not-found"], []),
            ],
        };
        var errors = ProjectScenarioEvaluator.Evaluate(plan).Findings;
        Assert.Contains(errors, x => x.Code == "dependency-cycle");
        Assert.Contains(errors, x => x.Code == "missing-dependency");
    }

    [Fact]
    public void CurrencyNeverSilentlyConvertsAndMissingPricesRemainExplicit()
    {
        var plan = Scenario() with
        {
            Parts =
            [
                new PlanPart("usd", "UPS", 1, "product:ups-model", "offer:ups", null, "r1"),
                new PlanPart("eur", "Mount", 1, "product:mount", "offer:mount", null, null),
            ],
            Offers =
            [
                new PlanOfferSnapshot("offer:ups", "product:ups-model", "seller", "USD",
                    125m, 5m, 10m, OfferAvailability.InStock, ObservedAt, "fixture", SourceKind.UserEntered, null),
                new PlanOfferSnapshot("offer:mount", "product:mount", "seller", "EUR",
                    null, null, null, OfferAvailability.Unknown, ObservedAt, "fixture", SourceKind.VendorApi, null),
            ],
        };
        var evaluation = ProjectScenarioEvaluator.Evaluate(plan);
        Assert.Equal(2, evaluation.Costs.Count);
        var dollars = Assert.Single(evaluation.Costs, x => x.Currency == "USD");
        Assert.Equal(140m, dollars.GrandTotal);
        var euros = Assert.Single(evaluation.Costs, x => x.Currency == "EUR");
        Assert.Null(euros.GrandTotal);
        Assert.Contains("eur", euros.MissingPricePartIds);
        Assert.Contains(evaluation.Findings, x => x.Code == "offer-unavailable-status");
    }

    [Fact]
    public void ProductAndSellerOfferIdentityMustAgree()
    {
        var plan = Scenario() with
        {
            Parts = [new PlanPart("ups", "UPS", 1, "product:different", "offer:ups", null, "r1")],
        };
        Assert.Contains(ProjectScenarioEvaluator.Evaluate(plan).Findings, x => x.Code == "product-offer-mismatch");
    }

    [Fact]
    public void RackPlanCannotLeakIntoAnotherScenarioRevision()
    {
        var rack = new RackLayout("other-scenario", 1, [], [], []);
        var plan = Scenario() with { RackPlan = rack };
        Assert.Contains(ProjectScenarioEvaluator.Evaluate(plan).Findings, x => x.Code == "rack-revision-mismatch");
        var matching = plan with { RackPlan = rack with { ScenarioId = plan.ScenarioId, Revision = plan.Revision } };
        Assert.DoesNotContain(ProjectScenarioEvaluator.Evaluate(matching).Findings, x => x.Code == "rack-revision-mismatch");
    }

    [Fact]
    public void AudioPlanCannotLeakIntoAnotherScenarioRevision()
    {
        var audio = new MonitorApp.Planning.Audio.AudioPlan("other-scenario", 1, [], [], [], [], [], [], []);
        var plan = Scenario() with { AudioPlan = audio };
        Assert.Contains(ProjectScenarioEvaluator.Evaluate(plan).Findings, x => x.Code == "audio-revision-mismatch");
        var matching = plan with
        {
            AudioPlan = audio with { ScenarioId = plan.ScenarioId, Revision = plan.Revision }
        };
        Assert.DoesNotContain(ProjectScenarioEvaluator.Evaluate(matching).Findings,
            x => x.Code == "audio-revision-mismatch");
    }
}
