using MonitorApp.Model;
using MonitorApp.Planning.Connections;

namespace MonitorApp.Planning.Model;

/// <summary>
/// T301's planning records as views of model objects (T064): the ids are model ids ("objectId#portId"), the connector
/// ids are the model port's ids from the shared <see cref="ConnectorCatalog"/>, and electrical values are the port's own
/// properties. Nothing here is stored; the model store is the only place these facts live.
/// </summary>
public static class ModelPlanningViews
{
    public static ConnectionPort ToConnectionPort(ModelObject owner, PortSpec port)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(port);
        var voltage = ConnectionChains.PortValue(owner, port.PortId, "electrical.voltage");
        var min = Volts(ConnectionChains.PortValue(owner, port.PortId, "electrical.voltage.min") ?? voltage);
        var max = Volts(ConnectionChains.PortValue(owner, port.PortId, "electrical.voltage.max") ?? voltage);
        var supply = Amps(ConnectionChains.PortValue(owner, port.PortId, "electrical.current.max"));
        var draw = Amps(ConnectionChains.PortValue(owner, port.PortId, ConnectionChains.CurrentDrawKey));
        var polarity = ConnectionChains.PortValue(owner, port.PortId, "electrical.polarity")?.Value.Text;
        var power = min is null && max is null && supply is null && draw is null && polarity is null
            ? null
            : new ElectricalRange(min, max, supply, draw, polarity);
        var specified = owner.Properties.Any(p => p.Scope == port.PortId && p.Evidence.Level >= EvidenceLevel.Specified)
            ? owner.Properties.First(p => p.Scope == port.PortId && p.Evidence.Level >= EvidenceLevel.Specified).Evidence.Source
            : null;
        return new ConnectionPort(
            new PortRef(owner.Id, port.PortId).ToString(),
            owner.Id,
            port.ConnectorTypeId ?? string.Empty,
            port.Gender switch
            {
                PortGender.Plug => ConnectorGender.Plug,
                PortGender.Socket => ConnectorGender.Receptacle,
                PortGender.Genderless => ConnectorGender.Genderless,
                _ => ConnectorGender.Unknown,
            },
            port.Direction switch
            {
                PortDirection.Output => ConnectionDirection.Source,
                PortDirection.Input => ConnectionDirection.Sink,
                PortDirection.Both => ConnectionDirection.Bidirectional,
                _ => ConnectionDirection.Unknown,
            },
            ConnectionChains.Signals(port),
            power,
            ConnectionChains.PortValue(owner, port.PortId, "connector.pinout")?.Value.Text,
            specified);
    }

    private static decimal? Volts(PropertyAssertion? value) =>
        value?.Value.Quantity is { } q && Units.TryConvert(q, "V", out var v) ? (decimal)v.Value : null;

    private static decimal? Amps(PropertyAssertion? value) =>
        value?.Value.Quantity is { } q && Units.TryConvert(q, "A", out var a) ? (decimal)a.Value : null;
}
