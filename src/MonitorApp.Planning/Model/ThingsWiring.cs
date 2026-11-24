using MonitorApp.Model;
using MonitorApp.Planning.Connections;

namespace MonitorApp.Planning.Model;

/// <summary>A connector a person can pick for a port, from the shared connector library.</summary>
public sealed record ConnectorChoice(string Id, string Label, string Domain);

/// <summary>One finding about a connection, in the words the Things screen shows.</summary>
public sealed record WiringFinding(string Code, string Certainty, string Explanation);

/// <summary>A connection path that starts or ends at a thing, with the engine's verdict and its reasons.</summary>
public sealed record ThingWiring(string ThingId, string Route, string Verdict, string VerdictText, IReadOnlyList<WiringFinding> Findings);

/// <summary>A need from <see cref="WhatsMissing"/>, in the words the Things screen shows.</summary>
public sealed record MissingNeed(string Code, string Need, string Explanation, string Certainty, IReadOnlyList<string> Things);

/// <summary>A rack from <see cref="RackLayouts"/>, with findings in the Things screen's words.</summary>
public sealed record RackDrawing(string RackId, int Units, bool UnitsKnown, IReadOnlyList<RackBlock> Blocks, IReadOnlyList<string> Unplaced, IReadOnlyList<WiringFinding> Findings);

/// <summary>A pedalboard or desk from <see cref="SurfaceLayouts"/>, with findings in the Things screen's words.</summary>
public sealed record SurfaceDrawing(string SurfaceId, double Width, double Depth, bool SizeKnown, IReadOnlyList<SurfaceItem> Items, IReadOnlyList<string> Unplaced, IReadOnlyList<WiringFinding> Findings);

/// <summary>One line of a project's parts list in the Things screen's words: the price is already written out ("$45").</summary>
public sealed record ProjectPartLine(string ThingId, string Name, string Lifecycle, string State, double Quantity, bool QuantityKnown, string? Price, string PriceEvidence);

/// <summary>A project's parts list from <see cref="ProjectParts"/>, with its one-line summary and cost.</summary>
public sealed record ProjectPartsList(string ProjectId, IReadOnlyList<ProjectPartLine> Parts, int Have, int Ordered, int ToBuy, string Summary, string CostText);

/// <summary>What the Things screen needs from the connection engine, for any head (local API or the phone).</summary>
public static class ThingsWiring
{
    public static IReadOnlyList<ConnectorChoice> Connectors(ConnectorCatalog? catalog = null) =>
        (catalog ?? ConnectorCatalog.BuiltIn).Types
            .OrderBy(t => t.Domain)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ConnectorChoice(t.Id, t.Label, t.Domain.ToString()))
            .ToArray();

    public static IReadOnlyList<MissingNeed> Missing(SystemModel model, ConnectorCatalog? catalog = null) =>
        WhatsMissing.Derive(model, catalog).Select(m => new MissingNeed(m.Code, m.Need, m.Explanation, m.Certainty.ToString(), m.Things)).ToArray();

    public static IReadOnlyList<RackDrawing> Racks(SystemModel model) =>
        RackLayouts.All(model).Select(r => new RackDrawing(r.RackId, r.Units, r.UnitsKnown, r.Blocks, r.Unplaced,
            r.Findings.OrderBy(f => f.Certainty).Select(f => new WiringFinding(f.Code, f.Certainty.ToString(), f.Explanation)).ToArray())).ToArray();

    public static IReadOnlyList<SurfaceDrawing> Surfaces(SystemModel model) =>
        SurfaceLayouts.All(model).Select(s => new SurfaceDrawing(s.SurfaceId, s.Width, s.Depth, s.SizeKnown, s.Items, s.Unplaced,
            s.Findings.OrderBy(f => f.Certainty).Select(f => new WiringFinding(f.Code, f.Certainty.ToString(), f.Explanation)).ToArray())).ToArray();

    public static IReadOnlyList<ProjectPartsList> Projects(SystemModel model) =>
        ProjectParts.All(model).Select(p => new ProjectPartsList(p.ProjectId,
            p.Parts.Select(l => new ProjectPartLine(l.ThingId, l.Name, l.Lifecycle, l.State.ToString(), l.Quantity, l.QuantityKnown,
                l.UnitPrice is { } price ? KnownValues.Money(new Quantity(price, l.Currency!)) : null, l.PriceEvidence.ToString())).ToArray(),
            p.Have, p.Ordered, p.ToBuy, p.Summary, p.CostText)).ToArray();

    public static bool IsKnownConnector(string id) => ConnectorCatalog.BuiltIn.TryGet(id, out _);

    /// <summary>Every connection path in the model, reported once for each thing at either end of it.</summary>
    public static IReadOnlyList<ThingWiring> For(SystemModel model, ConnectorCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var result = new List<ThingWiring>();
        var owners = model.Connections.SelectMany(c => new[] { c.A.ObjectId, c.B.ObjectId }).Distinct(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            foreach (var report in ConnectionChains.ReportsFor(model, owner, catalog))
            {
                result.Add(new ThingWiring(
                    owner,
                    report.Route,
                    report.Verdict.ToString(),
                    report.VerdictText,
                    report.Findings
                        .OrderBy(f => f.Certainty)
                        .Select(f => new WiringFinding(f.Code, f.Certainty.ToString(), f.Explanation))
                        .ToArray()));
            }
        }

        return result;
    }
}
