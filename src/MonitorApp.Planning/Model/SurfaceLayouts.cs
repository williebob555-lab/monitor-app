using System.Globalization;
using MonitorApp.Model;

namespace MonitorApp.Planning.Model;

/// <summary>
/// A thing on a board or desk, seen from above: its left-front corner at (<paramref name="X"/>, <paramref name="Y"/>) mm
/// from the board's left-front corner, <paramref name="Width"/> by <paramref name="Depth"/> mm. <paramref name="SizeKnown"/>
/// is false when its size was not recorded and it is drawn at an assumed pedal size.
/// </summary>
public sealed record SurfaceItem(string ThingId, string Name, double X, double Y, double Width, double Depth, bool SizeKnown, bool Problem);

/// <summary>A pedalboard or desk as the Things screen draws it (T082), with fit findings.</summary>
public sealed record SurfaceLayoutView(
    string SurfaceId,
    double Width,
    double Depth,
    bool SizeKnown,
    IReadOnlyList<SurfaceItem> Items,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Pedalboards and desks from the shared model: containment with an "x,y" position in mm is the placement, sizes are
/// ordinary width and depth values. The same rules serve any flat surface kind.
/// </summary>
public static class SurfaceLayouts
{
    public const string Engine = "surface-layout/1";
    public static readonly IReadOnlyList<string> SurfaceKinds = ["assembly.pedalboard", "assembly.desk"];

    public const string WidthKey = "mechanical.width";
    public const string DepthKey = "mechanical.depth";

    /// <summary>Drawn sizes when nothing is recorded: a mid-size board, and a standard compact pedal.</summary>
    public const double AssumedBoardWidth = 600, AssumedBoardDepth = 300, AssumedItemWidth = 70, AssumedItemDepth = 120;

    public static bool IsSurface(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return SurfaceKinds.Any(k => item.KindId == k || item.KindId.StartsWith(k + ".", StringComparison.Ordinal));
    }

    public static IReadOnlyList<SurfaceLayoutView> All(SystemModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Objects
            .Where(o => o.CreatedInLayer is null && IsSurface(o))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => For(model, o.Id))
            .ToArray();
    }

    public static SurfaceLayoutView For(SystemModel model, string surfaceId)
    {
        ArgumentNullException.ThrowIfNull(model);
        var board = model.Get(surfaceId);
        var findings = new List<Finding>();
        void Add(string code, FindingCertainty certainty, string text, params string[] subjects) =>
            findings.Add(new Finding(code, certainty, text, subjects, Engine));

        var (bw, bwEvidence) = Mm(board, WidthKey);
        var (bd, bdEvidence) = Mm(board, DepthKey);
        var boardKnown = bw is not null && bd is not null;
        var width = bw ?? AssumedBoardWidth;
        var depth = bd ?? AssumedBoardDepth;
        if (!boardKnown)
        {
            Add("surface-size-unknown", FindingCertainty.Unknown,
                $"The size of {board.Name} is not recorded; it is drawn {Format(width)} × {Format(depth)} mm.", board.Id);
        }

        var placed = new List<(ModelObject Item, double X, double Y, double W, double D, bool Known, EvidenceLevel Evidence)>();
        var unplaced = new List<string>();
        foreach (var (relationship, item) in model.ChildrenOf(board.Id)
                     .Select(r => (r, model.Find(r.TargetId)))
                     .Where(c => c.Item2 is not null)
                     .Select(c => (c.r, c.Item2!))
                     .OrderBy(c => c.Item2.Name, StringComparer.Ordinal))
        {
            if (relationship.Placement?.Position is not { } at)
            {
                unplaced.Add(item.Name);
                continue;
            }

            var (iw, iwEvidence) = Mm(item, WidthKey);
            var (id, idEvidence) = Mm(item, DepthKey);
            var known = iw is not null && id is not null;
            if (!known)
            {
                Add("item-size-unknown", FindingCertainty.Unknown,
                    $"The size of {item.Name} is not recorded; it is drawn {Format(iw ?? AssumedItemWidth)} × {Format(id ?? AssumedItemDepth)} mm.", item.Id);
            }

            placed.Add((item, at.X, at.Y, iw ?? AssumedItemWidth, id ?? AssumedItemDepth, known, Weakest(iwEvidence, idEvidence)));
        }

        var problems = new HashSet<string>(StringComparer.Ordinal);
        var boardEvidence = Weakest(bwEvidence, bdEvidence);
        foreach (var p in placed)
        {
            if (p.X < 0 || p.Y < 0 || p.X + p.W > width + 0.5 || p.Y + p.D > depth + 0.5)
            {
                var sure = boardKnown && p.Known && boardEvidence >= EvidenceLevel.Specified && p.Evidence >= EvidenceLevel.Specified;
                Add("off-the-edge", sure ? FindingCertainty.VerifiedProblem : boardKnown && p.Known ? FindingCertainty.LikelyProblem : FindingCertainty.Unknown,
                    $"{p.Item.Name} at {Format(p.X)}, {Format(p.Y)} mm ({Format(p.W)} × {Format(p.D)} mm) hangs over the edge of {board.Name} ({Format(width)} × {Format(depth)} mm).", p.Item.Id);
                problems.Add(p.Item.Id);
            }
        }

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var (a, b) = (placed[i], placed[j]);
                if (a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.D && b.Y < a.Y + a.D)
                {
                    var sure = a.Known && b.Known && a.Evidence >= EvidenceLevel.Specified && b.Evidence >= EvidenceLevel.Specified;
                    Add("overlap", sure ? FindingCertainty.VerifiedProblem : FindingCertainty.LikelyProblem,
                        $"{a.Item.Name} and {b.Item.Name} take the same space.", a.Item.Id, b.Item.Id);
                    problems.Add(a.Item.Id);
                    problems.Add(b.Item.Id);
                }
            }
        }

        foreach (var name in unplaced)
        {
            Add("not-placed", FindingCertainty.Unknown, $"{name} is on {board.Name} but has no position yet.", board.Id);
        }

        return new SurfaceLayoutView(board.Id, width, depth, boardKnown,
            placed.Select(p => new SurfaceItem(p.Item.Id, p.Item.Name, p.X, p.Y, p.W, p.D, p.Known, problems.Contains(p.Item.Id))).ToArray(),
            unplaced, findings);
    }

    private static (double? Value, EvidenceLevel Evidence) Mm(ModelObject item, string key) =>
        item.Strongest(key) is { Value.Quantity: { } q } p && Units.TryConvert(q, "mm", out var mm) && mm.Value > 0
            ? (mm.Value, p.Evidence.Level)
            : (null, EvidenceLevel.Unknown);

    private static EvidenceLevel Weakest(EvidenceLevel a, EvidenceLevel b) => (EvidenceLevel)Math.Min((int)a, (int)b);

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
