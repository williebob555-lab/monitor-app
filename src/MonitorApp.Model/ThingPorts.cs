using System.Globalization;
using System.Text.RegularExpressions;

namespace MonitorApp.Model;

/// <summary>
/// A port as the Things screen shows it: where something plugs in, what it is (connector, plug or socket, which way),
/// what it carries, its own values with evidence, and what it is plugged into right now.
/// </summary>
public sealed record ThingPort(
    string PortId,
    string Name,
    string? ConnectorTypeId,
    string Gender,
    string Direction,
    IReadOnlyList<string> Signals,
    IReadOnlyList<ThingFact> Values,
    string? ConnectionId = null,
    string? OtherId = null,
    string? OtherName = null,
    string? OtherPortName = null);

/// <summary>
/// A port to add. Values are optional and go on the port with evidence: typed by the person, or read from a spec sheet
/// when <paramref name="FromSpec"/> is set. Quantities need a unit ("9 V", "500 mA").
/// </summary>
public sealed record ThingPortEdit(
    string Name,
    string? ConnectorTypeId = null,
    string? Gender = null,
    string? Direction = null,
    IReadOnlyList<string>? Signals = null,
    string? Level = null,
    string? Voltage = null,
    string? Polarity = null,
    string? CurrentMax = null,
    string? CurrentDraw = null,
    bool FromSpec = false);

/// <summary>Plug one of this thing's ports into a port of another thing (directly, or into a cable or adapter).</summary>
public sealed record ThingConnectEdit(string PortId, string OtherId, string OtherPortId);

/// <summary>Port and connection edits for <see cref="Things.EditAsync"/>: the person's side of the connection model.</summary>
public static partial class ThingPorts
{
    public const int MaxSignals = 16;

    /// <summary>The port values the screen can set, as library property keys and the dimension each needs.</summary>
    private static readonly (string Key, Func<ThingPortEdit, string?> Read, string? Dimension, string Label)[] PortValues =
    [
        ("audio.level", e => e.Level, null, "Level"),
        ("electrical.voltage", e => e.Voltage, "voltage", "Voltage"),
        ("electrical.polarity", e => e.Polarity, null, "Polarity"),
        ("electrical.current.max", e => e.CurrentMax, "current", "Can supply"),
        ("electrical.current.draw", e => e.CurrentDraw, "current", "Draws"),
    ];

    public static IReadOnlyList<ThingPort> Describe(SystemModel model, ModelObject thing)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(thing);
        return thing.Ports
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(port =>
            {
                var here = new PortRef(thing.Id, port.PortId);
                var connection = model.ConnectionsAt(here).OrderBy(c => c.Medium).FirstOrDefault();
                var other = connection is null ? null : connection.A == here ? connection.B : connection.A;
                var otherThing = other is null ? null : model.Find(other.ObjectId);
                return new ThingPort(
                    port.PortId,
                    port.Name,
                    port.ConnectorTypeId,
                    port.Gender.ToString(),
                    port.Direction.ToString(),
                    port.Signals ?? [],
                    PortValues
                        .Select(v => thing.Properties.Where(p => p.Key == v.Key && p.Scope == port.PortId && p.Value.Type != PropertyValueType.Unknown)
                            .OrderByDescending(p => p.Evidence.Level).ThenByDescending(p => p.RecordedAt).FirstOrDefault() is { } best
                            ? new ThingFact(v.Key, v.Label, Things.Format(best.Value), best.Evidence.Level.ToString(), Things.SourceText(best.Evidence), best.RecordedAt, 0)
                            : null)
                        .OfType<ThingFact>()
                        .ToArray(),
                    connection?.ConnectionId,
                    otherThing?.Id,
                    otherThing?.Name,
                    other is null ? null : otherThing?.Port(other.PortId)?.Name);
            })
            .ToArray();
    }

    internal static IEnumerable<ModelChange> Changes(SystemModel model, ModelObject thing, ThingEdit edit, Func<string, bool> knownConnector)
    {
        foreach (var connectionId in edit.Disconnect ?? [])
        {
            if (!model.ConnectionsOf(thing.Id).Any(c => c.ConnectionId == connectionId))
            {
                throw new ModelValidationException("That connection is not on this thing.");
            }

            yield return new ConnectionRemoved(connectionId);
        }

        foreach (var portId in edit.RemovePorts ?? [])
        {
            if (thing.Port(portId) is null)
            {
                throw new ModelValidationException("That port is not on this thing.");
            }

            // Unplug it first, unless this same edit already did.
            foreach (var connection in model.ConnectionsAt(new PortRef(thing.Id, portId)).Where(c => !(edit.Disconnect ?? []).Contains(c.ConnectionId)))
            {
                yield return new ConnectionRemoved(connection.ConnectionId);
            }

            yield return new PortRemoved(thing.Id, portId);
        }

        var added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in edit.AddPorts ?? [])
        {
            foreach (var change in Add(thing, port, knownConnector, added))
            {
                yield return change;
            }
        }

        foreach (var connect in edit.Connect ?? [])
        {
            // A port added in this same edit can be named by its name; its id is not known to the screen yet.
            var mine = thing.Port(connect.PortId)?.PortId ?? added.GetValueOrDefault(connect.PortId)
                ?? throw new ModelValidationException("Pick one of this thing's ports to plug in.");
            if (model.Find(connect.OtherId) is not { } other || other.Port(connect.OtherPortId) is null)
            {
                throw new ModelValidationException("The port to plug into is not in the model.");
            }

            yield return new ConnectionAdded($"user:{Guid.NewGuid():N}", new PortRef(thing.Id, mine), new PortRef(other.Id, connect.OtherPortId));
        }
    }

    private static IEnumerable<ModelChange> Add(ModelObject thing, ThingPortEdit port, Func<string, bool> knownConnector, Dictionary<string, string> added)
    {
        var name = (port.Name ?? string.Empty).Replace('\n', ' ').Trim();
        if (name.Length == 0 || name.Length > Things.MaxNameLength)
        {
            throw new ModelValidationException("Give the port a short name, like \"DC in\" or \"Line out L\".");
        }

        if (thing.Ports.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) || added.ContainsKey(name))
        {
            throw new ModelValidationException($"This thing already has a port called \"{name}\".");
        }

        var connector = string.IsNullOrWhiteSpace(port.ConnectorTypeId) ? null : port.ConnectorTypeId.Trim();
        if (connector is not null && !knownConnector(connector))
        {
            throw new ModelValidationException($"'{connector}' is not a connector in the library.");
        }

        var signals = (port.Signals ?? [])
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (signals.Length > MaxSignals || signals.Any(s => s.Length > 40))
        {
            throw new ModelValidationException($"List at most {MaxSignals} short signal names, like \"usb-3\" or \"analog-audio\".");
        }

        var id = $"port:{Guid.NewGuid():N}";
        added[name] = id;
        yield return new PortAdded(thing.Id, new PortSpec(id, name, connector,
            ParseEnum(port.Gender, PortGender.Unknown, "plug or socket"),
            ParseEnum(port.Direction, PortDirection.Unknown, "output, input or both"),
            signals.Length == 0 ? null : signals));

        var evidence = port.FromSpec ? new Evidence(EvidenceLevel.Specified, Things.SpecSource) : Evidence.UserEntered;
        foreach (var (key, read, dimension, label) in PortValues)
        {
            if (read(port)?.Trim() is not { Length: > 0 } text)
            {
                continue;
            }

            var value = dimension is null ? PropertyValue.Of(text) : PropertyValue.Number(Quantity(text, dimension, label).Value, Quantity(text, dimension, label).Unit);
            yield return new PropertyAsserted(thing.Id, key, value, evidence, id);
        }
    }

    private static T ParseEnum<T>(string? text, T fallback, string words)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ModelValidationException($"Say {words}.");
    }

    /// <summary>"9 V", "9V", "0.5 A", "500 mA": a number and a unit of the right kind. Units are never assumed.</summary>
    internal static Quantity Quantity(string text, string dimension, string label)
    {
        var match = QuantityPattern().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || Units.DimensionOf(match.Groups[2].Value) != dimension)
        {
            throw new ModelValidationException($"{label} needs a number and a unit, like \"{(dimension == "voltage" ? "9 V" : "500 mA")}\".");
        }

        return new Quantity(number, match.Groups[2].Value);
    }

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*([A-Za-z%]+)\s*$")]
    private static partial Regex QuantityPattern();
}
