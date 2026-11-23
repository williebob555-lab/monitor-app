using MonitorApp.Planning.Audio;
using MonitorApp.Planning.Racks;

namespace MonitorApp.Planning.Projects;

public enum PlanChangeKind { Added = 1, Removed = 2, Modified = 3 }
public enum PlanFindingChangeKind { Introduced = 1, Resolved = 2, Modified = 3 }

public sealed record PlanObjectChange(string Category, string ObjectId, PlanChangeKind Kind);
public sealed record PlanFindingSnapshot(
    string Domain, string Code, string Severity, string? SubjectId, string Description);
public sealed record PlanFindingChange(
    PlanFindingChangeKind Kind, PlanFindingSnapshot? Before, PlanFindingSnapshot? After);
public sealed record PlanCostChange(
    string Currency,
    decimal? BeforeKnownSubtotal,
    decimal? AfterKnownSubtotal,
    decimal? BeforeGrandTotal,
    decimal? AfterGrandTotal,
    bool BeforeComplete,
    bool AfterComplete);

public sealed record ProjectPlanComparison(
    string ProjectId,
    string ScenarioId,
    int BeforeRevision,
    int AfterRevision,
    IReadOnlyList<PlanObjectChange> ObjectChanges,
    IReadOnlyList<PlanFindingChange> FindingChanges,
    IReadOnlyList<PlanCostChange> CostChanges);

/// <summary>Deterministic semantic comparison of two revisions of the same named design scenario.</summary>
public static class ProjectPlanComparer
{
    public static ProjectPlanComparison Compare(ProjectScenario before, ProjectScenario after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.ProjectId != after.ProjectId || before.ScenarioId != after.ScenarioId)
            throw new ArgumentException("Plan comparison requires the same project and scenario identity.", nameof(after));
        if (after.Revision <= before.Revision)
            throw new ArgumentException("The compared revision must be newer than the baseline revision.", nameof(after));

        var objectChanges = new List<PlanObjectChange>();
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            objectChanges.Add(new("scenario", after.ScenarioId, PlanChangeKind.Modified));

        CompareObjects("requirement", before.Requirements, after.Requirements, x => x.Id,
            static (x, y) => x == y, objectChanges);
        CompareObjects("part", before.Parts, after.Parts, x => x.Id,
            static (x, y) => x == y, objectChanges);
        CompareObjects("task", before.Tasks, after.Tasks, x => x.Id, TaskEquals, objectChanges);
        CompareObjects("offer", before.Offers, after.Offers, x => x.OfferId,
            static (x, y) => x == y, objectChanges);
        CompareRack(before.RackPlan, after.RackPlan, objectChanges);
        CompareAudio(before.AudioPlan, after.AudioPlan, objectChanges);

        var beforeEvaluation = ProjectScenarioEvaluator.Evaluate(before);
        var afterEvaluation = ProjectScenarioEvaluator.Evaluate(after);
        var findings = CompareFindings(FlattenFindings(beforeEvaluation), FlattenFindings(afterEvaluation));
        var costs = CompareCosts(beforeEvaluation.Costs, afterEvaluation.Costs);

        return new ProjectPlanComparison(before.ProjectId, before.ScenarioId, before.Revision, after.Revision,
            objectChanges.OrderBy(x => x.Category, StringComparer.Ordinal)
                .ThenBy(x => x.ObjectId, StringComparer.Ordinal).ThenBy(x => x.Kind).ToArray(),
            findings,
            costs);
    }

    private static void CompareRack(RackLayout? before, RackLayout? after, List<PlanObjectChange> changes)
    {
        if (before is null && after is null) return;
        if (before is null)
        {
            AddRackPlan(after!, PlanChangeKind.Added, changes);
            return;
        }
        if (after is null)
        {
            AddRackPlan(before, PlanChangeKind.Removed, changes);
            return;
        }
        CompareObjects("rack-zone", before.Zones, after.Zones, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("rack-equipment", before.Equipment, after.Equipment, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("rack-placement", before.Placements, after.Placements, x => x.Id,
            static (x, y) => x == y, changes);
    }

    private static void AddRackPlan(
        RackLayout plan,
        PlanChangeKind kind,
        List<PlanObjectChange> changes)
    {
        changes.Add(new("rack-plan", plan.ScenarioId, kind));
        AddObjects("rack-zone", plan.Zones, x => x.Id, kind, changes);
        AddObjects("rack-equipment", plan.Equipment, x => x.Id, kind, changes);
        AddObjects("rack-placement", plan.Placements, x => x.Id, kind, changes);
    }

    private static void CompareAudio(AudioPlan? before, AudioPlan? after, List<PlanObjectChange> changes)
    {
        if (before is null && after is null) return;
        if (before is null)
        {
            AddAudioPlan(after!, PlanChangeKind.Added, changes);
            return;
        }
        if (after is null)
        {
            AddAudioPlan(before, PlanChangeKind.Removed, changes);
            return;
        }
        CompareObjects("audio-device", before.Devices, after.Devices, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("audio-port", before.Ports, after.Ports, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("audio-cable", before.Cables, after.Cables, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("audio-patch", before.Patches, after.Patches, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("dc-load", before.DcLoads, after.DcLoads, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("dc-output", before.DcOutputs, after.DcOutputs, x => x.Id,
            static (x, y) => x == y, changes);
        CompareObjects("dc-assignment", before.DcAssignments, after.DcAssignments, x => x.LoadId,
            static (x, y) => x == y, changes);
        if (before.MaximumDesiredChainLatencyMs != after.MaximumDesiredChainLatencyMs)
            changes.Add(new("audio-latency-budget", after.ScenarioId, PlanChangeKind.Modified));
    }

    private static void AddAudioPlan(
        AudioPlan plan,
        PlanChangeKind kind,
        List<PlanObjectChange> changes)
    {
        changes.Add(new("audio-plan", plan.ScenarioId, kind));
        AddObjects("audio-device", plan.Devices, x => x.Id, kind, changes);
        AddObjects("audio-port", plan.Ports, x => x.Id, kind, changes);
        AddObjects("audio-cable", plan.Cables, x => x.Id, kind, changes);
        AddObjects("audio-patch", plan.Patches, x => x.Id, kind, changes);
        AddObjects("dc-load", plan.DcLoads, x => x.Id, kind, changes);
        AddObjects("dc-output", plan.DcOutputs, x => x.Id, kind, changes);
        AddObjects("dc-assignment", plan.DcAssignments, x => x.LoadId, kind, changes);
        if (plan.MaximumDesiredChainLatencyMs is not null)
            changes.Add(new("audio-latency-budget", plan.ScenarioId, kind));
    }

    private static bool TaskEquals(PlanTask first, PlanTask second) =>
        first.Title == second.Title && first.State == second.State &&
        first.DependsOn.Order(StringComparer.Ordinal).SequenceEqual(
            second.DependsOn.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        first.RelatedPartIds.Order(StringComparer.Ordinal).SequenceEqual(
            second.RelatedPartIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void CompareObjects<T>(
        string category,
        IEnumerable<T> before,
        IEnumerable<T> after,
        Func<T, string> id,
        Func<T, T, bool> equals,
        List<PlanObjectChange> changes)
    {
        var oldIndex = Index(before, id, category);
        var newIndex = Index(after, id, category);
        foreach (var oldItem in oldIndex)
        {
            if (!newIndex.TryGetValue(oldItem.Key, out var newValue))
                changes.Add(new(category, oldItem.Key, PlanChangeKind.Removed));
            else if (!equals(oldItem.Value, newValue))
                changes.Add(new(category, oldItem.Key, PlanChangeKind.Modified));
        }
        foreach (var newItem in newIndex)
            if (!oldIndex.ContainsKey(newItem.Key))
                changes.Add(new(category, newItem.Key, PlanChangeKind.Added));
    }

    private static void AddObjects<T>(
        string category,
        IEnumerable<T> values,
        Func<T, string> id,
        PlanChangeKind kind,
        List<PlanObjectChange> changes)
    {
        foreach (var item in Index(values, id, category))
            changes.Add(new(category, item.Key, kind));
    }

    private static Dictionary<string, T> Index<T>(
        IEnumerable<T> values,
        Func<T, string> id,
        string category)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var objectId = id(value);
            if (string.IsNullOrWhiteSpace(objectId))
                throw new ArgumentException(
                    $"Cannot compare {category} objects with an empty stable identifier.");
            if (!result.TryAdd(objectId, value))
                throw new ArgumentException(
                    $"Cannot compare {category} objects with duplicate stable identifier '{objectId}'.");
        }
        return result;
    }

    private static List<PlanFindingSnapshot> FlattenFindings(ProjectEvaluation evaluation)
    {
        var result = evaluation.Findings.Select(x => new PlanFindingSnapshot(
            "project", x.Code, x.Severity.ToString(), x.ObjectId, x.Description)).ToList();
        if (evaluation.RackEvaluation is not null)
            result.AddRange(evaluation.RackEvaluation.Findings.Select(x => new PlanFindingSnapshot(
                "rack", x.Code, x.Severity.ToString(), x.PlacementId ?? x.ZoneId, x.Explanation)));
        if (evaluation.AudioEvaluation is not null)
            result.AddRange(evaluation.AudioEvaluation.Findings.Select(x => new PlanFindingSnapshot(
                "audio", x.Code, x.Level.ToString(), x.SubjectId, x.Detail)));
        return result;
    }

    private static PlanFindingChange[] CompareFindings(
        IReadOnlyList<PlanFindingSnapshot> before,
        IReadOnlyList<PlanFindingSnapshot> after)
    {
        static string Key(PlanFindingSnapshot value) =>
            string.Join('\n', value.Domain, value.Code, value.SubjectId ?? string.Empty);
        var oldIndex = Index(before, Key, "finding");
        var newIndex = Index(after, Key, "finding");
        var changes = new List<PlanFindingChange>();
        foreach (var oldFinding in oldIndex)
        {
            if (!newIndex.TryGetValue(oldFinding.Key, out var newFinding))
                changes.Add(new(PlanFindingChangeKind.Resolved, oldFinding.Value, null));
            else if (oldFinding.Value != newFinding)
                changes.Add(new(PlanFindingChangeKind.Modified, oldFinding.Value, newFinding));
        }
        foreach (var newFinding in newIndex)
            if (!oldIndex.ContainsKey(newFinding.Key))
                changes.Add(new(PlanFindingChangeKind.Introduced, null, newFinding.Value));
        return changes.OrderBy(x => x.After?.Domain ?? x.Before!.Domain, StringComparer.Ordinal)
            .ThenBy(x => x.After?.Code ?? x.Before!.Code, StringComparer.Ordinal)
            .ThenBy(x => x.After?.SubjectId ?? x.Before?.SubjectId, StringComparer.Ordinal).ToArray();
    }

    private static PlanCostChange[] CompareCosts(
        IReadOnlyList<ProjectCostGroup> before,
        IReadOnlyList<ProjectCostGroup> after)
    {
        var oldIndex = before.ToDictionary(x => x.Currency, StringComparer.OrdinalIgnoreCase);
        var newIndex = after.ToDictionary(x => x.Currency, StringComparer.OrdinalIgnoreCase);
        return oldIndex.Keys.Union(newIndex.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(currency =>
            {
                oldIndex.TryGetValue(currency, out var oldCost);
                newIndex.TryGetValue(currency, out var newCost);
                return new PlanCostChange(
                    currency.ToUpperInvariant(),
                    oldCost?.KnownUnitSubtotal,
                    newCost?.KnownUnitSubtotal,
                    oldCost?.GrandTotal,
                    newCost?.GrandTotal,
                    oldCost?.Complete == true,
                    newCost?.Complete == true);
            })
            .Where(x => x.BeforeKnownSubtotal != x.AfterKnownSubtotal ||
                        x.BeforeGrandTotal != x.AfterGrandTotal || x.BeforeComplete != x.AfterComplete)
            .ToArray();
    }
}
