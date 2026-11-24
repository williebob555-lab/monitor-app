using System.Globalization;
using MonitorApp.Model;

namespace MonitorApp.Planning.Model;

/// <summary>Where a part of a project stands: still to get, on its way, in hand, or gone (retired or disposed of).</summary>
public enum PartState
{
    ToBuy = 0,
    Ordered = 1,
    Have = 2,
    Gone = 3,
}

/// <summary>
/// One line of a project's parts list. <paramref name="Quantity"/> is 1 when nothing was recorded
/// (<paramref name="QuantityKnown"/> false). <paramref name="UnitPrice"/> is in <paramref name="Currency"/>, with how the
/// price is known; null when no price is recorded.
/// </summary>
public sealed record ProjectPart(
    string ThingId,
    string Name,
    string KindId,
    string Lifecycle,
    PartState State,
    double Quantity,
    bool QuantityKnown,
    double? UnitPrice,
    string? Currency,
    EvidenceLevel PriceEvidence);

/// <summary>A money total in one currency. <paramref name="Firm"/> is false when any price in it is only estimated.</summary>
public sealed record MoneyTotal(string Currency, double Amount, bool Firm);

/// <summary>
/// A project's parts list (T084, P1 and P2): everything linked "part of" it or put inside it, how many of them are in hand,
/// and what the rest costs. Totals are kept per currency; nothing is converted.
/// </summary>
public sealed record ProjectPartsView(
    string ProjectId,
    IReadOnlyList<ProjectPart> Parts,
    int Have,
    int Ordered,
    int ToBuy,
    IReadOnlyList<MoneyTotal> ToBuyCost,
    int ToBuyPricesUnknown,
    IReadOnlyList<MoneyTotal> TotalCost,
    string Summary,
    string CostText);

/// <summary>Parts lists for projects, from the shared model. The same rules serve any project, whatever it builds.</summary>
public static class ProjectParts
{
    public const string ProjectKind = "project";
    public const string PriceKey = "commerce.price";
    public const string QuantityKey = "commerce.quantity-needed";

    public static bool IsProject(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.KindId == ProjectKind || item.KindId.StartsWith(ProjectKind + ".", StringComparison.Ordinal);
    }

    public static IReadOnlyList<ProjectPartsView> All(SystemModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Objects
            .Where(o => o.CreatedInLayer is null && IsProject(o))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => For(model, o.Id))
            .ToArray();
    }

    public static ProjectPartsView For(SystemModel model, string projectId)
    {
        ArgumentNullException.ThrowIfNull(model);
        var project = model.Get(projectId);
        var ids = model.Relationships
            .Where(r => r.CreatedInLayer is null && r.TypeId == RelationshipTypes.PartOfProject && r.TargetId == project.Id)
            .Select(r => r.SourceId)
            .Concat(model.ChildrenOf(project.Id).Select(r => r.TargetId))
            .Where(id => id != project.Id)
            .Distinct(StringComparer.Ordinal);

        var parts = ids
            .Select(model.Find)
            .OfType<ModelObject>()
            .Where(o => o.KindId != "note")
            .Select(Part)
            .OrderBy(p => p.State)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var toBuy = parts.Where(p => p.State == PartState.ToBuy).ToArray();
        var toBuyCost = Totals(toBuy);
        var unknown = toBuy.Count(p => p.UnitPrice is null);
        var have = parts.Count(p => p.State == PartState.Have);
        var ordered = parts.Count(p => p.State == PartState.Ordered);
        var counted = parts.Count(p => p.State != PartState.Gone);

        var summary = counted == 0
            ? "No parts yet. Link things \"Part of\" this project, or put them inside it."
            : string.Join(", ", new[]
            {
                $"{have} of {counted} in hand",
                ordered > 0 ? $"{ordered} ordered" : null,
                toBuy.Length > 0 ? $"{toBuy.Length} to buy" : null,
            }.OfType<string>());

        return new ProjectPartsView(project.Id, parts, have, ordered, toBuy.Length, toBuyCost, unknown,
            Totals(parts.Where(p => p.State != PartState.Gone)), summary, CostText(toBuy.Length, toBuyCost, unknown));
    }

    private static ProjectPart Part(ModelObject item)
    {
        var quantity = item.Strongest(QuantityKey) is { Value.Quantity: { Unit: "count" } q } && q.Value >= 1 ? q.Value : (double?)null;
        var price = item.Strongest(PriceKey);
        var money = price?.Value.Quantity is { } p && Units.DimensionOf(p.Unit) is { } d && d.StartsWith("currency-", StringComparison.Ordinal) ? p : (Quantity?)null;
        return new ProjectPart(
            item.Id,
            item.Name,
            item.KindId,
            item.Lifecycle.ToString(),
            StateOf(item),
            quantity ?? 1,
            quantity is not null,
            money?.Value,
            money?.Unit,
            money is null ? EvidenceLevel.Unknown : price!.Evidence.Level);
    }

    /// <summary>A requirement is a need nobody has met yet, so it is always to buy until it is replaced by a real thing.</summary>
    private static PartState StateOf(ModelObject item) => item.Lifecycle switch
    {
        _ when item.KindId == "requirement" => PartState.ToBuy,
        Lifecycle.Ordered => PartState.Ordered,
        Lifecycle.Owned or Lifecycle.Installed => PartState.Have,
        Lifecycle.Retired or Lifecycle.Disposed => PartState.Gone,
        _ => PartState.ToBuy,
    };

    private static MoneyTotal[] Totals(IEnumerable<ProjectPart> parts) =>
        parts.Where(p => p.UnitPrice is not null)
            .GroupBy(p => p.Currency!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MoneyTotal(g.Key, Math.Round(g.Sum(p => p.UnitPrice!.Value * p.Quantity), 2), g.All(p => p.PriceEvidence >= EvidenceLevel.Entered)))
            .ToArray();

    private static string CostText(int toBuy, MoneyTotal[] cost, int unknown)
    {
        if (toBuy == 0)
        {
            return string.Empty;
        }

        if (cost.Length == 0)
        {
            return unknown == 1 ? "To buy: no price recorded yet." : $"To buy: no prices recorded yet ({unknown} parts).";
        }

        var amounts = string.Join(" + ", cost.Select(c => (c.Firm ? string.Empty : "about ") + Money(c.Amount, c.Currency)));
        var missing = unknown switch
        {
            0 => string.Empty,
            1 => " (1 price unknown)",
            _ => $" ({unknown} prices unknown)",
        };
        return $"To buy: {amounts}{missing}.";
    }

    private static string Money(double amount, string currency) =>
        KnownValues.Money(new Quantity(amount, currency)) ?? amount.ToString("0.##", CultureInfo.InvariantCulture) + " " + currency;
}
