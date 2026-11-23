using System.Globalization;
using MonitorApp.Model;
using MonitorApp.Planning.Connections;

namespace MonitorApp.Planning.Model;

/// <summary>
/// Something a setup still needs (T069): a supply for an input, a cable or adapter between two ports, more current.
/// <paramref name="Need"/> is a short name for it ("Power for Fuzz", "Cable: 1/4-inch TS plug to 1/4-inch TS plug"), and is
/// what the person keeps when they add it to their list as a requirement. <paramref name="Things"/> are the object ids involved.
/// </summary>
public sealed record MissingItem(string Code, string Need, string Explanation, FindingCertainty Certainty, IReadOnlyList<string> Things);

/// <summary>
/// Works out what is needed but not there, from ports, connections and values alone. Deterministic: the same model always
/// gives the same list. Needs already on the person's list (a requirement with the same name) are left out.
/// </summary>
public static class WhatsMissing
{
    public const string RequirementKind = "requirement";

    public static IReadOnlyList<MissingItem> Derive(SystemModel model, ConnectorCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        catalog ??= ConnectorCatalog.BuiltIn;
        var items = new List<MissingItem>();
        var things = model.Objects
            .Where(o => o.CreatedInLayer is null && o.Lifecycle is not (Lifecycle.Retired or Lifecycle.Disposed))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var thing in things.Where(t => !ConnectionChains.IsInline(t)))
        {
            UnconnectedInputs(model, thing, items);
            PowerBudget(model, thing, items);
        }

        foreach (var thing in things.Where(t => !ConnectionChains.IsInline(t)))
        {
            foreach (var report in ConnectionChains.ReportsFor(model, thing.Id, catalog))
            {
                FromReport(model, catalog, report, items);
            }
        }

        var onList = things
            .Where(t => t.KindId == RequirementKind)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return items
            .Where(i => !onList.Contains(i.Need))
            .DistinctBy(i => i.Need, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.Certainty)
            .ThenBy(i => i.Need, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// A power input with nothing plugged in always needs a supply. Other inputs only count once the thing is part of a setup
    /// (something else on it is connected), so a computer with ports listed but nothing wired is not a wall of warnings.
    /// </summary>
    private static void UnconnectedInputs(SystemModel model, ModelObject thing, List<MissingItem> items)
    {
        var wired = model.ConnectionsOf(thing.Id).Any();
        foreach (var port in thing.Ports.Where(p => p.Direction == PortDirection.Input).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (model.ConnectionsAt(new PortRef(thing.Id, port.PortId)).Any())
            {
                continue;
            }

            if (IsPower(thing, port))
            {
                var needs = PowerWords(thing, port);
                items.Add(new MissingItem("power-missing", $"Power for {thing.Name}",
                    $"Nothing is plugged into {thing.Name} {port.Name}{(needs.Length == 0 ? "" : $" ({needs})")}. It needs a supply{(needs.Length == 0 ? "" : " that gives " + needs)}.",
                    FindingCertainty.Unknown, [thing.Id]));
            }
            else if (wired)
            {
                var carries = ConnectionChains.Signals(port);
                items.Add(new MissingItem("input-unconnected", $"Something into {thing.Name} {port.Name}",
                    $"{thing.Name} {port.Name} has nothing plugged in{(carries.Count == 0 ? "" : $"; it takes {string.Join(" or ", carries.Order(StringComparer.Ordinal))}")}.",
                    FindingCertainty.Unknown, [thing.Id]));
            }
        }
    }

    /// <summary>A supply with a total limit (an unscoped "can supply" value) feeding more than that through its outputs.</summary>
    private static void PowerBudget(SystemModel model, ModelObject supply, List<MissingItem> items)
    {
        var limit = supply.Properties
            .Where(p => p.Key == "electrical.current.max" && p.Scope is null && p.Value.Type != PropertyValueType.Unknown)
            .OrderByDescending(p => p.Evidence.Level).ThenByDescending(p => p.RecordedAt).FirstOrDefault();
        if (limit is null || Amps(limit) is not { } available)
        {
            return;
        }

        var total = 0.0;
        var weakest = limit.Evidence.Level;
        var loads = new List<string>();
        foreach (var port in supply.Ports.Where(p => p.Direction is PortDirection.Output or PortDirection.Both))
        {
            if (ConnectionChains.Trace(model, new PortRef(supply.Id, port.PortId)) is not { End: { } end }
                || model.Find(end.ObjectId) is not { } load
                || ConnectionChains.PortValue(load, end.PortId, ConnectionChains.CurrentDrawKey) is not { } draw
                || Amps(draw) is not { } amps)
            {
                continue;
            }

            total += amps;
            weakest = (EvidenceLevel)Math.Min((int)weakest, (int)draw.Evidence.Level);
            loads.Add(load.Name);
        }

        if (total > available)
        {
            items.Add(new MissingItem("power-shortfall", $"More power than {supply.Name} gives",
                $"{string.Join(", ", loads.Order(StringComparer.CurrentCultureIgnoreCase))} draw {Format(total)} A together, but {supply.Name} supplies {Format(available)} A in total: {Format(total - available)} A short.",
                weakest >= EvidenceLevel.Specified ? FindingCertainty.VerifiedProblem : FindingCertainty.LikelyProblem,
                [supply.Id]));
        }
    }

    /// <summary>Connection problems that are fixed by getting something: a cable, an adapter, a bigger supply, the other end plugged in.</summary>
    private static void FromReport(SystemModel model, ConnectorCatalog catalog, ConnectionReport report, List<MissingItem> items)
    {
        foreach (var hop in report.Path.Hops.Where(h => h.Connection.Medium == ConnectionMedium.Physical))
        {
            var a = model.Get(hop.From.ObjectId);
            var b = model.Get(hop.To.ObjectId);
            var pa = a.Port(hop.From.PortId)!;
            var pb = b.Port(hop.To.PortId)!;
            // The same hop is seen from both ends; name it the same way either way round.
            var ids = new[] { a.Id, b.Id }.Order(StringComparer.Ordinal).ToArray();
            var (labelA, labelB) = Ordered(Label(catalog, pa), Label(catalog, pb));
            if (pa.Gender == PortGender.Socket && pb.Gender == PortGender.Socket)
            {
                items.Add(new MissingItem("cable-needed", $"Cable: {labelA} plug to {labelB} plug",
                    $"{a.Name} {pa.Name} and {b.Name} {pb.Name} are both sockets, so a cable goes between them.",
                    FindingCertainty.VerifiedProblem, ids));
            }
            else if (catalog.TryGet(pa.ConnectorTypeId ?? "", out var ta) && catalog.TryGet(pb.ConnectorTypeId ?? "", out var tb)
                && !string.Equals(ta!.FitKey, tb!.FitKey, StringComparison.OrdinalIgnoreCase))
            {
                var (first, second) = Ordered(ta.Label, tb.Label);
                items.Add(new MissingItem("adapter-needed", $"Adapter: {first} to {second}",
                    $"{a.Name} {pa.Name} is {ta.Label} and {b.Name} {pb.Name} is {tb.Label}; they don't plug into each other.",
                    FindingCertainty.VerifiedProblem, ids));
            }
        }

        if (report.Path.End is null)
        {
            var last = report.Path.Hops[^1].To;
            if (model.Find(last.ObjectId) is { } loose && ConnectionChains.IsInline(loose))
            {
                items.Add(new MissingItem("loose-end", $"Plug in the other end of {loose.Name}",
                    $"{report.Route}: one end of {loose.Name} is not plugged into anything.",
                    FindingCertainty.Unknown, [loose.Id]));
            }
        }

        foreach (var finding in report.Findings.Where(f => f.Code == "current-overload"))
        {
            var source = model.Find(report.Path.Start.ObjectId);
            items.Add(new MissingItem("supply-too-weak", $"A stronger supply than {source?.Name ?? "this supply"}",
                finding.Explanation, finding.Certainty, [report.Path.Start.ObjectId]));
        }
    }

    private static bool IsPower(ModelObject thing, PortSpec port) =>
        ConnectionChains.Signals(port).Any(s => s.Contains("power", StringComparison.Ordinal) || s is "dc" or "ac" || s.StartsWith("dc-", StringComparison.Ordinal) || s.StartsWith("ac-", StringComparison.Ordinal))
        || ConnectionChains.PortValue(thing, port.PortId, "electrical.voltage") is not null
        || ConnectionChains.PortValue(thing, port.PortId, ConnectionChains.CurrentDrawKey) is not null;

    private static string PowerWords(ModelObject thing, PortSpec port)
    {
        var parts = new[] { "electrical.voltage", "electrical.polarity", ConnectionChains.CurrentDrawKey }
            .Select(key => ConnectionChains.PortValue(thing, port.PortId, key))
            .OfType<PropertyAssertion>()
            .Select(p => MonitorApp.Model.Things.Format(p.Value));
        return string.Join(", ", parts);
    }

    private static (string First, string Second) Ordered(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static string Label(ConnectorCatalog catalog, PortSpec port) =>
        port.ConnectorTypeId is { } id && catalog.TryGet(id, out var type) ? type!.Label : port.ConnectorTypeId ?? "unknown connector";

    private static double? Amps(PropertyAssertion value) =>
        value.Value.Quantity is { } q && Units.TryConvert(q, "A", out var a) ? a.Value : null;

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
