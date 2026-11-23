using MonitorApp.Planning.Audio;
using MonitorApp.Planning.Racks;

namespace MonitorApp.Planning.Projects;

public enum ProjectWorkState { NotStarted = 1, InProgress = 2, Blocked = 3, Completed = 4, Cancelled = 5 }
public enum SourceKind { UserEntered = 1, VendorApi = 2, ImportedDocument = 3, Calculated = 4 }
public enum OfferAvailability { InStock = 1, Backorder = 2, OutOfStock = 3, Unknown = 4, Restricted = 5 }

/// <summary>A stable product definition is not a seller offer or a physical owned instance.</summary>
public sealed record PlanPart(
    string Id,
    string Name,
    int Quantity,
    string? ProductDefinitionId,
    string? SelectedOfferId,
    string? ExistingInstanceReferenceId,
    string? RequirementId,
    bool Optional = false);

public sealed record PlanRequirement(string Id, string Description, bool Required = true);

public sealed record PlanTask(
    string Id,
    string Title,
    ProjectWorkState State,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> RelatedPartIds);

/// <summary>
/// Read-only external offer facts; no offer becomes owned merely because it was selected in a plan.
/// Prices, tax and shipping can each be unknown, independently. No implicit currency conversion.
/// </summary>
public sealed record PlanOfferSnapshot(
    string OfferId,
    string ProductDefinitionId,
    string Seller,
    string Currency,
    decimal? UnitPrice,
    decimal? Shipping,
    decimal? Tax,
    OfferAvailability Availability,
    DateTimeOffset ObservedAt,
    string SourceProvider,
    SourceKind SourceKind,
    string? SourceUrl);

public sealed record ProjectScenario(
    string ProjectId,
    string ScenarioId,
    int Revision,
    string Name,
    IReadOnlyList<PlanRequirement> Requirements,
    IReadOnlyList<PlanPart> Parts,
    IReadOnlyList<PlanTask> Tasks,
    IReadOnlyList<PlanOfferSnapshot> Offers,
    RackLayout? RackPlan = null,
    AudioPlan? AudioPlan = null);

public enum ProjectFindingSeverity { Error = 1, Warning = 2, Unresolved = 3 }
public sealed record ProjectFinding(string Code, ProjectFindingSeverity Severity, string? ObjectId, string Description);

/// <summary>Totals are grouped by currency; missing parts stay explicit rather than pretending to cost zero.</summary>
public sealed record ProjectCostGroup(
    string Currency,
    decimal KnownUnitSubtotal,
    decimal KnownShippingAndTax,
    IReadOnlyList<string> MissingPricePartIds,
    IReadOnlyList<string> MissingChargesOfferIds)
{
    public bool Complete => MissingPricePartIds.Count == 0 && MissingChargesOfferIds.Count == 0;
    public decimal? GrandTotal => Complete ? KnownUnitSubtotal + KnownShippingAndTax : null;
}

public sealed record ProjectEvaluation(
    string ProjectId,
    string ScenarioId,
    int Revision,
    IReadOnlyList<ProjectFinding> Findings,
    IReadOnlyList<ProjectCostGroup> Costs,
    IReadOnlyList<string> ReadyTaskIds,
    RackLayoutEvaluation? RackEvaluation,
    AudioEvaluation? AudioEvaluation)
{
    public bool HasErrors => Findings.Any(x => x.Severity == ProjectFindingSeverity.Error) ||
                             RackEvaluation?.HasErrors == true || AudioEvaluation?.HasErrors == true;
    public bool HasUnresolved => Findings.Any(x => x.Severity == ProjectFindingSeverity.Unresolved) ||
                                 RackEvaluation?.HasUnknowns == true || AudioEvaluation?.HasUnknowns == true;
}

public static class ProjectScenarioEvaluator
{
    public static ProjectEvaluation Evaluate(ProjectScenario plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<ProjectFinding>();
        if (string.IsNullOrWhiteSpace(plan.ProjectId) || string.IsNullOrWhiteSpace(plan.ScenarioId) ||
            string.IsNullOrWhiteSpace(plan.Name) || plan.Revision <= 0)
            Issue("scenario-id", ProjectFindingSeverity.Error, null, "A named, versioned design scenario is required.");

        var tasks = Index(plan.Tasks, x => x.Id, "duplicate-task");
        var parts = Index(plan.Parts, x => x.Id, "duplicate-part");
        var requirements = Index(plan.Requirements, x => x.Id, "duplicate-requirement");
        var offers = Index(plan.Offers, x => x.OfferId, "duplicate-offer");

        foreach (var p in parts.Values)
        {
            if (p.Quantity < 1) Issue("invalid-quantity", ProjectFindingSeverity.Error, p.Id, "Quantities must be positive.");
            if (p.RequirementId is not null && !requirements.ContainsKey(p.RequirementId))
                Issue("missing-requirement", ProjectFindingSeverity.Error, p.Id, "Part refers to an unknown project requirement.");
            if (p.SelectedOfferId is { } offerId)
            {
                if (!offers.TryGetValue(offerId, out var offer))
                    Issue("missing-offer", ProjectFindingSeverity.Error, p.Id, "Selected seller offer is not present.");
                else if (p.ProductDefinitionId is null || offer.ProductDefinitionId != p.ProductDefinitionId)
                    Issue("product-offer-mismatch", ProjectFindingSeverity.Error, p.Id, "Seller offer and canonical product definition must agree.");
            }
            if (p.SelectedOfferId is null && p.ExistingInstanceReferenceId is null && !p.Optional)
                Issue("unsourced-part", ProjectFindingSeverity.Unresolved, p.Id, "Required planned part has no offer or actual-instance reference.");
        }

        foreach (var r in requirements.Values.Where(x => x.Required))
        {
            if (!parts.Values.Any(p => p.RequirementId == r.Id && !p.Optional))
                Issue("uncovered-requirement", ProjectFindingSeverity.Unresolved, r.Id, "A required project objective has no planned part.");
        }

        foreach (var task in tasks.Values)
        {
            if (!Enum.IsDefined(task.State))
                Issue("invalid-task-state", ProjectFindingSeverity.Error, task.Id, "Unknown task state.");
            foreach (var dep in task.DependsOn)
            {
                if (!tasks.ContainsKey(dep))
                    Issue("missing-dependency", ProjectFindingSeverity.Error, task.Id, $"Dependency {dep} does not exist.");
                if (dep == task.Id)
                    Issue("self-dependency", ProjectFindingSeverity.Error, task.Id, "A task cannot depend on itself.");
            }
            foreach (var partId in task.RelatedPartIds)
                if (!parts.ContainsKey(partId))
                    Issue("missing-task-part", ProjectFindingSeverity.Error, task.Id, "Task refers to an unknown planned part.");
        }

        var colors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var taskId in tasks.Keys)
            Visit(taskId);

        var ready = tasks.Values
            .Where(t => t.State == ProjectWorkState.NotStarted &&
                        t.DependsOn.All(dep => tasks.TryGetValue(dep, out var precedent) &&
                                               precedent.State == ProjectWorkState.Completed))
            .Select(t => t.Id).Order(StringComparer.Ordinal).ToArray();

        var costGroups = new Dictionary<string, CostAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Values.Where(x => x.SelectedOfferId is not null))
        {
            if (!offers.TryGetValue(part.SelectedOfferId!, out var offer)) continue;
            var currency = offer.Currency.ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            {
                Issue("invalid-currency", ProjectFindingSeverity.Error, part.Id, "Offer currency must be an explicit ISO-style three-letter code.");
                continue;
            }
            if (!costGroups.TryGetValue(currency, out var group))
                costGroups.Add(currency, group = new CostAccumulator());
            if (offer.UnitPrice is not >= 0)
                group.MissingPrices.Add(part.Id);
            else
                group.UnitSubtotal += offer.UnitPrice.Value * part.Quantity;
            if (offer.Shipping is not >= 0 || offer.Tax is not >= 0)
                group.MissingCharges.Add(offer.OfferId);
            else
                group.Charges += offer.Shipping.Value + offer.Tax.Value;
            if (offer.Availability is OfferAvailability.Unknown or OfferAvailability.Restricted)
                Issue("offer-unavailable-status", ProjectFindingSeverity.Unresolved, part.Id, "Offer availability is unknown or access-restricted.");
            if (offer.Availability == OfferAvailability.OutOfStock)
                Issue("offer-out-of-stock", ProjectFindingSeverity.Warning, part.Id, "The selected offer reports out of stock.");
        }
        var costs = costGroups.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            new ProjectCostGroup(x.Key, x.Value.UnitSubtotal, x.Value.Charges,
                x.Value.MissingPrices, x.Value.MissingCharges.Distinct(StringComparer.Ordinal).ToArray())).ToArray();

        RackLayoutEvaluation? rack = null;
        if (plan.RackPlan is not null)
        {
            if (plan.RackPlan.ScenarioId != plan.ScenarioId || plan.RackPlan.Revision != plan.Revision)
                Issue("rack-revision-mismatch", ProjectFindingSeverity.Error, null, "Rack layout must belong to this exact design scenario revision.");
            else
                rack = RackLayoutValidator.Evaluate(plan.RackPlan);
        }

        AudioEvaluation? audio = null;
        if (plan.AudioPlan is not null)
        {
            if (plan.AudioPlan.ScenarioId != plan.ScenarioId || plan.AudioPlan.Revision != plan.Revision)
                Issue("audio-revision-mismatch", ProjectFindingSeverity.Error, null,
                    "Audio layout must belong to this exact design scenario revision.");
            else
                audio = AudioPlanEvaluator.Evaluate(plan.AudioPlan);
        }

        return new ProjectEvaluation(plan.ProjectId, plan.ScenarioId, plan.Revision, issues, costs, ready, rack, audio);

        Dictionary<string, T> Index<T>(IEnumerable<T> sequence, Func<T, string> key, string error)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in sequence)
            {
                var id = key(item);
                if (string.IsNullOrWhiteSpace(id) || !result.TryAdd(id, item))
                    Issue(error, ProjectFindingSeverity.Error, id, "Identifiers must be nonempty and unique.");
            }
            return result;
        }

        bool Visit(string id)
        {
            if (colors.TryGetValue(id, out var color))
            {
                if (color == 1) Issue("dependency-cycle", ProjectFindingSeverity.Error, id, "Project task dependency cycle detected.");
                return color == 2;
            }
            colors[id] = 1;
            foreach (var predecessor in tasks[id].DependsOn)
                if (tasks.ContainsKey(predecessor)) Visit(predecessor);
            colors[id] = 2;
            return true;
        }

        void Issue(string code, ProjectFindingSeverity severity, string? objId, string description) =>
            issues.Add(new ProjectFinding(code, severity, objId, description));
    }

    private sealed class CostAccumulator
    {
        public decimal UnitSubtotal { get; set; }
        public decimal Charges { get; set; }
        public List<string> MissingPrices { get; } = [];
        public List<string> MissingCharges { get; } = [];
    }
}
