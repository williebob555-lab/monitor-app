using System.Globalization;
using MonitorApp.Model;
using MonitorApp.Planning.Connections;

namespace MonitorApp.Planning.Model;

/// <summary>One join in a path: the port it leaves from, the port it arrives at, and the model connection between them.</summary>
public sealed record ConnectionHop(PortRef From, PortRef To, Connection Connection);

/// <summary>
/// A connection followed end to end through the model: device port, then any cables, adapters and converters, then the
/// port at the far end. <see cref="End"/> is null when the path stops at a loose cable end or an element whose way
/// through is not clear.
/// </summary>
public sealed record ConnectionPath(PortRef Start, PortRef? End, IReadOnlyList<ConnectionHop> Hops)
{
    /// <summary>The cables and adapters the path runs through, in order.</summary>
    public IReadOnlyList<string> Inline => (End is null ? Hops : Hops.Take(Hops.Count - 1)).Select(h => h.To.ObjectId).ToList();
}

/// <summary>What the engine concluded about a whole path, in the words a person reads.</summary>
public enum ConnectionVerdict
{
    WorksAsRecorded = 1,
    CannotTellYet = 2,
    ProbablyWillNotWork = 3,
    WillNotWork = 4,
}

public sealed record ConnectionReport(ConnectionPath Path, string Route, ConnectionVerdict Verdict, IReadOnlyList<Finding> Findings)
{
    public string VerdictText => Verdict switch
    {
        ConnectionVerdict.WorksAsRecorded => "Works, as far as the records go",
        ConnectionVerdict.CannotTellYet => "Can't tell yet",
        ConnectionVerdict.ProbablyWillNotWork => "Probably won't work",
        _ => "Won't work",
    };
}

/// <summary>
/// The connection engine (T064, T067). It follows a path through the model and checks each layer separately, because
/// a plug that fits proves nothing else (docs/ARCHITECTURE-MODEL.md 3.3):
/// <list type="bullet">
/// <item>physical: each join mates by the shared connector library's fit keys and by gender;</item>
/// <item>direction: an output feeds an input;</item>
/// <item>signal: what the source offers must survive every cable and passive adapter and be something the far end takes;
/// an active converter takes one signal in and puts its own out;</item>
/// <item>values that must agree end to end (audio level, voltage, polarity, current), read from port properties with
/// their evidence, so a problem from datasheet values is verified and one from typed values is likely.</item>
/// </list>
/// Nothing here knows about USB-C, guitars or pedals in particular: the examples are data in the model and the library.
/// </summary>
public static class ConnectionChains
{
    public const string Engine = "connection-chain/1";

    /// <summary>Flag on a cable or adapter object: it converts (DI box, DAC, active USB-C to HDMI) rather than passing through.</summary>
    public const string ActiveKey = "adapter.active";

    /// <summary>Current a load draws through a port.</summary>
    public const string CurrentDrawKey = "electrical.current.draw";

    private const int MaxHops = 32;

    /// <summary>True for objects a connection passes through: cables, adapters and converters (by kind, so new kinds under them count too).</summary>
    public static bool IsInline(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsKind(item.KindId, "cable") || IsKind(item.KindId, "adapter");
    }

    /// <summary>
    /// Follows the connection at <paramref name="start"/> to its far end. The path is returned from its more upstream end
    /// (an output before a two-way port before an input), so source and destination always read left to right.
    /// </summary>
    public static ConnectionPath? Trace(SystemModel model, PortRef start)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(start);
        if (model.Find(start.ObjectId)?.Port(start.PortId) is null)
        {
            return null;
        }

        var hops = new List<ConnectionHop>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start.ObjectId };
        var current = start;
        PortRef? end = null;
        while (hops.Count < MaxHops)
        {
            var connection = model.ConnectionsAt(current).OrderBy(c => c.Medium).FirstOrDefault(c => !hops.Any(h => h.Connection.ConnectionId == c.ConnectionId));
            if (connection is null)
            {
                break;
            }

            var other = connection.A == current ? connection.B : connection.A;
            hops.Add(new ConnectionHop(current, other, connection));
            var owner = model.Get(other.ObjectId);
            if (!IsInline(owner))
            {
                end = other;
                break;
            }

            var onward = owner.Ports.Where(p => p.PortId != other.PortId).ToList();
            if (onward.Count != 1 || !visited.Add(owner.Id))
            {
                break; // a splitter or a loop: the way through is not a single path.
            }

            current = new PortRef(owner.Id, onward[0].PortId);
        }

        if (hops.Count == 0)
        {
            return null;
        }

        var path = new ConnectionPath(start, end, hops);
        return end is not null && Upstream(DirectionOf(model, end)) < Upstream(DirectionOf(model, start)) ? Reverse(path) : path;
    }

    /// <summary>Every distinct path that starts or ends at a port of <paramref name="objectId"/>, evaluated.</summary>
    public static IReadOnlyList<ConnectionReport> ReportsFor(SystemModel model, string objectId, ConnectorCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Find(objectId) is not { } item || IsInline(item))
        {
            return [];
        }

        var reports = new List<ConnectionReport>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in item.Ports.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (Trace(model, new PortRef(item.Id, port.PortId)) is { } path && seen.Add(Key(path)))
            {
                reports.Add(Evaluate(model, path, catalog));
            }
        }

        return reports;
    }

    public static ConnectionReport Evaluate(SystemModel model, ConnectionPath path, ConnectorCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(path);
        catalog ??= ConnectorCatalog.BuiltIn;
        var findings = new List<Finding>();
        void Add(string code, FindingCertainty certainty, string text, params string[] subjects) =>
            findings.Add(new Finding(code, certainty, text, subjects, Engine));

        foreach (var hop in path.Hops.Where(h => h.Connection.Medium == ConnectionMedium.Physical))
        {
            Mate(model, catalog, hop, Add);
        }

        if (path.End is not { } end)
        {
            var last = path.Hops[^1].To;
            Add("path-open", FindingCertainty.Unknown, $"The path stops at {PortName(model, last)}: nothing is recorded on the other side.", last.ToString());
            return Finish(model, path, findings);
        }

        var source = path.Start;
        CheckDirection(model, source, end, Add);

        // What reaches the far end: the source's offer, narrowed by each passive element and replaced by each converter.
        var flow = Flow.From(model, source);
        foreach (var element in path.Inline.Distinct(StringComparer.Ordinal))
        {
            var item = model.Get(element);
            var hop = path.Hops.First(h => h.To.ObjectId == element);
            var input = model.Get(element).Port(hop.To.PortId)!;
            var output = item.Ports.First(p => p.PortId != input.PortId);
            if (item.Strongest(ActiveKey)?.Value.Flag == true)
            {
                CheckArrives(model, flow, new PortRef(element, input.PortId), Add);
                flow = Flow.From(model, new PortRef(element, output.PortId));
            }
            else
            {
                flow = flow.Through(item, input, output, Add);
            }
        }

        CheckArrives(model, flow, end, Add);
        return Finish(model, path, findings);
    }

    /// <summary>The route in words: "Laptop USB-C → USB-C cable → Monitor USB-C in".</summary>
    public static string Route(SystemModel model, ConnectionPath path)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(path);
        var parts = new List<string> { PortName(model, path.Start) };
        parts.AddRange(path.Inline.Select(id => model.Find(id)?.Name ?? id));
        parts.Add(path.End is { } end ? PortName(model, end) : "(nothing)");
        return string.Join(" → ", parts);
    }

    private static ConnectionReport Finish(SystemModel model, ConnectionPath path, List<Finding> findings)
    {
        var verdict = findings.Any(f => f.Certainty == FindingCertainty.VerifiedProblem) ? ConnectionVerdict.WillNotWork
            : findings.Any(f => f.Certainty == FindingCertainty.LikelyProblem) ? ConnectionVerdict.ProbablyWillNotWork
            : findings.Any(f => f.Certainty == FindingCertainty.Unknown) ? ConnectionVerdict.CannotTellYet
            : ConnectionVerdict.WorksAsRecorded;
        if (verdict == ConnectionVerdict.WorksAsRecorded)
        {
            findings.Add(new Finding("ok-as-declared", FindingCertainty.OkAsDeclared,
                "Connectors, direction, signals and power agree as recorded. This is not a safety certificate.", [path.Start.ToString()], Engine));
        }

        return new ConnectionReport(path, Route(model, path), verdict, findings);
    }

    private static void Mate(SystemModel model, ConnectorCatalog catalog, ConnectionHop hop, Action<string, FindingCertainty, string, string[]> add)
    {
        var a = model.Get(hop.From.ObjectId).Port(hop.From.PortId)!;
        var b = model.Get(hop.To.ObjectId).Port(hop.To.PortId)!;
        var where = $"{PortName(model, hop.From)} to {PortName(model, hop.To)}";
        var subjects = new[] { hop.From.ToString(), hop.To.ToString() };
        ConnectorType? typeA = null;
        ConnectorType? typeB = null;
        if (a.ConnectorTypeId is null || b.ConnectorTypeId is null)
        {
            add("connector-unknown", FindingCertainty.Unknown, $"The connector type is not recorded for {where}.", subjects);
        }
        else if (!catalog.TryGet(a.ConnectorTypeId, out typeA) || !catalog.TryGet(b.ConnectorTypeId, out typeB))
        {
            add("connector-unlisted", FindingCertainty.Unknown, $"{(typeA is null ? a.ConnectorTypeId : b.ConnectorTypeId)} is not in the connector library, so {where} can't be checked.", subjects);
        }
        else if (!string.Equals(typeA!.FitKey, typeB!.FitKey, StringComparison.OrdinalIgnoreCase))
        {
            add("connector-mismatch", FindingCertainty.VerifiedProblem, $"{typeA.Label} does not plug into {typeB.Label} ({where}). An adapter is needed.", subjects);
        }

        if (a.Gender == PortGender.Unknown || b.Gender == PortGender.Unknown)
        {
            add("gender-unknown", FindingCertainty.Unknown, $"Whether each end of {where} is a plug or a socket is not recorded.", subjects);
        }
        else if (a.Gender == b.Gender && a.Gender != PortGender.Genderless)
        {
            add("gender-mismatch", FindingCertainty.VerifiedProblem, $"{where} joins two {(a.Gender == PortGender.Plug ? "plugs" : "sockets")}.", subjects);
        }
    }

    private static void CheckDirection(SystemModel model, PortRef source, PortRef end, Action<string, FindingCertainty, string, string[]> add)
    {
        var from = DirectionOf(model, source);
        var to = DirectionOf(model, end);
        if (from == PortDirection.Unknown || to == PortDirection.Unknown)
        {
            add("direction-unknown", FindingCertainty.Unknown,
                $"Which way the signal goes is not recorded for {(from == PortDirection.Unknown ? PortName(model, source) : PortName(model, end))}.",
                [(from == PortDirection.Unknown ? source : end).ToString()]);
        }
        else if (from == to && from != PortDirection.Both)
        {
            add("direction-mismatch", FindingCertainty.VerifiedProblem,
                $"{PortName(model, source)} and {PortName(model, end)} are both {(from == PortDirection.Output ? "outputs" : "inputs")}.",
                [source.ToString(), end.ToString()]);
        }
    }

    private static void CheckArrives(SystemModel model, Flow flow, PortRef at, Action<string, FindingCertainty, string, string[]> add)
    {
        var item = model.Get(at.ObjectId);
        var port = item.Port(at.PortId)!;
        var name = PortName(model, at);
        var subject = new[] { at.ToString() };
        var accepts = Signals(port);
        if (flow.Signals is null || accepts.Count == 0)
        {
            add("signal-unknown", FindingCertainty.Unknown,
                flow.Signals is null ? $"What {flow.Origin} sends is not recorded." : $"What {name} accepts is not recorded.", subject);
        }
        else if (!flow.Signals.Overlaps(accepts))
        {
            add("signal-mismatch", FindingCertainty.VerifiedProblem,
                $"{name} needs {Words(accepts)}, but what reaches it is {Words(flow.Signals)}.", subject);
        }

        Compare(flow.Level, PortValue(item, at.PortId, "audio.level"), (sent, wanted) =>
            $"{flow.Origin} sends {sent.Value.Text} level, but {name} expects {wanted.Value.Text} level.", "audio-level-mismatch", subject, add);
        Compare(flow.Polarity, PortValue(item, at.PortId, "electrical.polarity"), (sent, wanted) =>
            $"{flow.Origin} is {sent.Value.Text}, but {name} needs {wanted.Value.Text}. Reversed polarity can damage equipment.", "polarity-mismatch", subject, add);
        CheckVoltage(flow, item, at, name, subject, add);
        CheckCurrent(flow, item, at, name, subject, add);
    }

    private static void CheckVoltage(Flow flow, ModelObject item, PortRef at, string name, string[] subject, Action<string, FindingCertainty, string, string[]> add)
    {
        var accepts = Range(item, at.PortId);
        if (flow.Voltage is null && accepts is null)
        {
            return;
        }

        if (flow.Voltage is null || accepts is null)
        {
            add("voltage-unknown", FindingCertainty.Unknown,
                flow.Voltage is null ? $"The voltage {flow.Origin} supplies is not recorded." : $"The voltage {name} accepts is not recorded.", subject);
            return;
        }

        var (supply, load) = (flow.Voltage.Value, accepts.Value);
        var certainty = Certainty(supply.Evidence, load.Evidence);
        if (supply.Min > load.Max || supply.Max < load.Min)
        {
            add("voltage-mismatch", certainty,
                $"{flow.Origin} supplies {Volts(supply)}, but {name} accepts {Volts(load)}.", subject);
        }
        else if (supply.Max > load.Max)
        {
            add("voltage-may-exceed", FindingCertainty.LikelyProblem,
                $"{flow.Origin} can supply up to {Volts(supply.Max)}, more than the {Volts(load.Max)} {name} accepts.", subject);
        }
    }

    private static void CheckCurrent(Flow flow, ModelObject item, PortRef at, string name, string[] subject, Action<string, FindingCertainty, string, string[]> add)
    {
        var draw = PortValue(item, at.PortId, CurrentDrawKey);
        if (draw is null || Amps(draw) is not { } needed)
        {
            return;
        }

        if (flow.CurrentLimit is not { } limit || Amps(limit) is not { } available)
        {
            add("current-unknown", FindingCertainty.Unknown, $"{name} draws {Format(needed)} A, but how much {flow.Origin} can supply is not recorded.", subject);
        }
        else if (needed > available)
        {
            add("current-overload", Certainty(draw.Evidence.Level, limit.Evidence.Level),
                $"{name} draws {Format(needed)} A, more than the {Format(available)} A {flow.Origin} can supply.", subject);
        }
    }

    private static void Compare(PropertyAssertion? sent, PropertyAssertion? wanted, Func<PropertyAssertion, PropertyAssertion, string> text, string code,
        string[] subject, Action<string, FindingCertainty, string, string[]> add)
    {
        if (sent?.Value.Text is null || wanted?.Value.Text is null)
        {
            return; // only compared when both ends say; an absent level is not a finding on its own.
        }

        if (!string.Equals(Normalize(sent.Value.Text), Normalize(wanted.Value.Text), StringComparison.Ordinal))
        {
            add(code, Certainty(sent.Evidence.Level, wanted.Evidence.Level), text(sent, wanted), subject);
        }
    }

    private static FindingCertainty Certainty(EvidenceLevel a, EvidenceLevel b) =>
        a >= EvidenceLevel.Specified && b >= EvidenceLevel.Specified ? FindingCertainty.VerifiedProblem : FindingCertainty.LikelyProblem;

    private static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static PortDirection DirectionOf(SystemModel model, PortRef port) =>
        model.Find(port.ObjectId)?.Port(port.PortId)?.Direction ?? PortDirection.Unknown;

    /// <summary>How far upstream a port sits: outputs lead, inputs follow, two-way and unrecorded ports sit between.</summary>
    private static int Upstream(PortDirection direction) => direction switch
    {
        PortDirection.Output => 0,
        PortDirection.Input => 2,
        _ => 1,
    };

    private static ConnectionPath Reverse(ConnectionPath path)
    {
        var hops = path.Hops.Reverse().Select(h => new ConnectionHop(h.To, h.From, h.Connection)).ToList();
        return new ConnectionPath(path.End!, path.Start, hops);
    }

    private static string Key(ConnectionPath path)
    {
        var ends = new[] { path.Start.ToString(), path.End?.ToString() ?? path.Hops[^1].To.ToString() }.Order(StringComparer.Ordinal);
        return string.Join("|", ends);
    }

    internal static string PortName(SystemModel model, PortRef port)
    {
        var item = model.Find(port.ObjectId);
        var name = item?.Port(port.PortId)?.Name ?? port.PortId;
        return item is null ? name : $"{item.Name} {name}";
    }

    internal static HashSet<string> Signals(PortSpec port) =>
        (port.Signals ?? []).Select(Normalize).ToHashSet(StringComparer.Ordinal);

    internal static PropertyAssertion? PortValue(ModelObject item, string portId, string key) =>
        item.Properties.Where(p => p.Key == key && p.Scope == portId && p.Value.Type != PropertyValueType.Unknown)
            .OrderByDescending(p => p.Evidence.Level).ThenByDescending(p => p.RecordedAt).FirstOrDefault();

    private static VoltageRange? Range(ModelObject item, string portId)
    {
        var exact = PortValue(item, portId, "electrical.voltage");
        var min = PortValue(item, portId, "electrical.voltage.min") ?? exact;
        var max = PortValue(item, portId, "electrical.voltage.max") ?? exact;
        return min is not null && max is not null && VoltsOf(min) is { } low && VoltsOf(max) is { } high
            ? new VoltageRange(low, high, (EvidenceLevel)Math.Min((int)min.Evidence.Level, (int)max.Evidence.Level))
            : null;
    }

    private static double? VoltsOf(PropertyAssertion value) =>
        value.Value.Quantity is { } q && Units.TryConvert(q, "V", out var v) ? v.Value : null;

    private static double? Amps(PropertyAssertion value) =>
        value.Value.Quantity is { } q && Units.TryConvert(q, "A", out var a) ? a.Value : null;

    private static string Volts(VoltageRange range) =>
        range.Min == range.Max ? Volts(range.Min) : $"{Format(range.Min)} to {Volts(range.Max)}";

    private static string Volts(double value) => $"{Format(value)} V";

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Words(IEnumerable<string> signals) => string.Join(" or ", signals.Order(StringComparer.Ordinal));

    private static bool IsKind(string kindId, string root) =>
        kindId == root || kindId.StartsWith(root + ".", StringComparison.Ordinal);

    private readonly record struct VoltageRange(double Min, double Max, EvidenceLevel Evidence);

    /// <summary>What travels along the path at a given point, and who put it there.</summary>
    private sealed record Flow(
        string Origin, HashSet<string>? Signals, PropertyAssertion? Level, PropertyAssertion? Polarity,
        VoltageRange? Voltage, PropertyAssertion? CurrentLimit)
    {
        public static Flow From(SystemModel model, PortRef port)
        {
            var item = model.Get(port.ObjectId);
            var spec = item.Port(port.PortId)!;
            var signals = ConnectionChains.Signals(spec);
            return new Flow(PortName(model, port), signals.Count == 0 ? null : signals,
                PortValue(item, port.PortId, "audio.level"), PortValue(item, port.PortId, "electrical.polarity"),
                Range(item, port.PortId), PortValue(item, port.PortId, "electrical.current.max"));
        }

        /// <summary>A cable or passive adapter carries only what both of its ends carry, and never converts.</summary>
        public Flow Through(ModelObject element, PortSpec input, PortSpec output, Action<string, FindingCertainty, string, string[]> add)
        {
            var a = ConnectionChains.Signals(input);
            var b = ConnectionChains.Signals(output);
            if (a.Count == 0 && b.Count == 0)
            {
                add("carrier-unknown", FindingCertainty.Unknown, $"What {element.Name} can carry is not recorded.", [element.Id]);
                return this;
            }

            var carries = a.Count == 0 ? b : b.Count == 0 ? a : a.Intersect(b, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (Signals is null)
            {
                return this;
            }

            var left = Signals.Intersect(carries, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (left.Count == 0)
            {
                add("carrier-mismatch", FindingCertainty.VerifiedProblem,
                    $"{element.Name} carries {Words(carries)}, which does not include {Words(Signals)} from {Origin}. "
                    + "A passive cable or adapter cannot convert; use a converter.", [element.Id]);
            }

            return this with { Signals = left.Count == 0 ? Signals : left };
        }
    }
}
