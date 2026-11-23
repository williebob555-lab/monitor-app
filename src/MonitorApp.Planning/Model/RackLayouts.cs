using System.Globalization;
using MonitorApp.Model;

namespace MonitorApp.Planning.Model;

/// <summary>
/// One block in a rack drawn from the top down: a piece of equipment spanning <paramref name="Units"/> rack units, or an
/// empty unit (<paramref name="ThingId"/> null). <paramref name="HeightKnown"/> is false when the item's height was not
/// recorded and it is drawn one unit high.
/// </summary>
public sealed record RackBlock(int TopUnit, int Units, string? ThingId, string? Name, bool HeightKnown, bool Problem);

/// <summary>
/// A rack as the Things screen draws it (T082): its size, what sits at which unit, what is inside but not placed yet,
/// and fit findings (overlaps, past the top, too deep, sizes unknown).
/// </summary>
public sealed record RackLayoutView(
    string RackId,
    int Units,
    bool UnitsKnown,
    IReadOnlyList<RackBlock> Blocks,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<Finding> Findings);

/// <summary>Racks from the shared model: containment with a "U5" slot is the placement, sizes are ordinary values.</summary>
public static class RackLayouts
{
    public const string Engine = "rack-layout/1";
    public const string RackKind = "assembly.rack";
    public const string RackUnitsKey = "mechanical.rack-units";
    public const string DepthKey = "mechanical.depth";

    /// <summary>A rack with no recorded size is drawn this tall, and says so.</summary>
    public const int AssumedUnits = 12;

    public static bool IsRack(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.KindId == RackKind || item.KindId.StartsWith(RackKind + ".", StringComparison.Ordinal);
    }

    /// <summary>Every rack in the actual world, by id.</summary>
    public static IReadOnlyList<RackLayoutView> All(SystemModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Objects
            .Where(o => o.CreatedInLayer is null && IsRack(o))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => For(model, o.Id))
            .ToArray();
    }

    public static RackLayoutView For(SystemModel model, string rackId)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rack = model.Get(rackId);
        var findings = new List<Finding>();
        void Add(string code, FindingCertainty certainty, string text, params string[] subjects) =>
            findings.Add(new Finding(code, certainty, text, subjects, Engine));

        var size = Whole(rack.Strongest(RackUnitsKey));
        var units = size ?? AssumedUnits;
        if (size is null)
        {
            Add("rack-size-unknown", FindingCertainty.Unknown, $"How many units {rack.Name} has is not recorded; it is drawn {AssumedUnits}U.", rack.Id);
        }

        var placed = new List<(ModelObject Item, int Bottom, int Height, bool Known)>();
        var unplaced = new List<string>();
        foreach (var child in model.ChildrenOf(rack.Id).Select(r => (Relationship: r, Item: model.Find(r.TargetId))).Where(c => c.Item is not null).OrderBy(c => c.Item!.Name, StringComparer.Ordinal))
        {
            var item = child.Item!;
            if (Unit(child.Relationship.Placement?.Slot) is not { } bottom)
            {
                unplaced.Add(item.Name);
                continue;
            }

            var height = Whole(item.Strongest(RackUnitsKey));
            if (height is null)
            {
                Add("height-unknown", FindingCertainty.Unknown, $"How many units {item.Name} takes is not recorded; it is drawn 1U.", item.Id);
            }

            placed.Add((item, bottom, height ?? 1, height is not null));
            CheckDepth(rack, item, Add);
        }

        var problems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, bottom, height, known) in placed)
        {
            if (bottom + height - 1 > units && size is not null)
            {
                Add("past-the-top", FindingCertainty.VerifiedProblem,
                    $"{item.Name} at U{bottom} is {height}U tall, so it runs past the top of {rack.Name} ({units}U).", item.Id);
                problems.Add(item.Id);
            }
        }

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var (a, b) = (placed[i], placed[j]);
                if (a.Bottom <= b.Bottom + b.Height - 1 && b.Bottom <= a.Bottom + a.Height - 1)
                {
                    Add("overlap", a.Known && b.Known ? FindingCertainty.VerifiedProblem : FindingCertainty.LikelyProblem,
                        $"{a.Item.Name} (U{a.Bottom}, {a.Height}U) and {b.Item.Name} (U{b.Bottom}, {b.Height}U) take the same space.", a.Item.Id, b.Item.Id);
                    problems.Add(a.Item.Id);
                    problems.Add(b.Item.Id);
                }
            }
        }

        foreach (var name in unplaced)
        {
            Add("not-placed", FindingCertainty.Unknown, $"{name} is in {rack.Name} but has no unit yet.", rack.Id);
        }

        return new RackLayoutView(rack.Id, units, size is not null, Blocks(units, placed, problems), unplaced, findings);
    }

    /// <summary>Top to bottom: each item once at its top unit, an empty block for each free unit. Overlapping items are drawn where they start.</summary>
    private static List<RackBlock> Blocks(int units, List<(ModelObject Item, int Bottom, int Height, bool Known)> placed, HashSet<string> problems)
    {
        var blocks = new List<RackBlock>();
        var top = Math.Max(units, placed.Count == 0 ? 0 : placed.Max(p => p.Bottom + p.Height - 1));
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        for (var unit = top; unit >= 1;)
        {
            var here = placed.Where(p => p.Bottom + p.Height - 1 == unit && !drawn.Contains(p.Item.Id)).OrderBy(p => p.Item.Name, StringComparer.Ordinal).FirstOrDefault();
            if (here.Item is not null)
            {
                drawn.Add(here.Item.Id);
                blocks.Add(new RackBlock(unit, here.Height, here.Item.Id, here.Item.Name, here.Known, problems.Contains(here.Item.Id)));
                unit -= here.Height;
                continue;
            }

            if (placed.Any(p => !drawn.Contains(p.Item.Id) && p.Bottom <= unit && unit <= p.Bottom + p.Height - 1))
            {
                unit--; // covered by an item whose top is higher but which was drawn there already
                continue;
            }

            blocks.Add(new RackBlock(unit, 1, null, null, true, false));
            unit--;
        }

        // Items hidden by an overlap still appear, after the rack, so nothing placed goes missing from the drawing.
        foreach (var (item, bottom, height, known) in placed.Where(p => !drawn.Contains(p.Item.Id)))
        {
            blocks.Add(new RackBlock(bottom + height - 1, height, item.Id, item.Name, known, true));
        }

        return blocks;
    }

    private static void CheckDepth(ModelObject rack, ModelObject item, Action<string, FindingCertainty, string, string[]> add)
    {
        if (Millimetres(rack.Strongest(DepthKey)) is not { } room || Millimetres(item.Strongest(DepthKey)) is not { } needs)
        {
            return;
        }

        if (needs.Value > room.Value)
        {
            var sure = rack.Strongest(DepthKey)!.Evidence.Level >= EvidenceLevel.Specified && item.Strongest(DepthKey)!.Evidence.Level >= EvidenceLevel.Specified;
            add("too-deep", sure ? FindingCertainty.VerifiedProblem : FindingCertainty.LikelyProblem,
                $"{item.Name} is {Format(needs.Value)} mm deep, but {rack.Name} has {Format(room.Value)} mm.", [item.Id]);
        }
    }

    /// <summary>"U5" → 5. Anything else is not a rack position.</summary>
    public static int? Unit(string? slot) =>
        slot is { Length: > 1 } && slot[0] is 'U' or 'u' && int.TryParse(slot.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var unit) && unit >= 1
            ? unit
            : null;

    private static int? Whole(PropertyAssertion? value) =>
        value?.Value.Quantity is { } q && Units.TryConvert(q, "U", out var u) && u.Value >= 1 && u.Value <= 100 ? (int)Math.Ceiling(u.Value - 1e-9) : null;

    private static Quantity? Millimetres(PropertyAssertion? value) =>
        value?.Value.Quantity is { } q && Units.TryConvert(q, "mm", out var mm) ? mm : null;

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
