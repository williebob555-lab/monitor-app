using System.Globalization;
using MonitorApp.Contracts;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Collection.Switches;

public enum Sg250CollectionStatus
{
    Available = 1,
    Unavailable = 2,
    PermissionDenied = 3,
    Error = 4,
}

/// <summary>A typed SNMP result. A transport supplies values without exposing its local authentication configuration.</summary>
public sealed record Sg250SnmpValue(string Oid, string Value);

public interface ISg250SnmpReader
{
    Task<IReadOnlyList<Sg250SnmpValue>> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class Sg250ReadException(Sg250CollectionStatus status) : Exception("Managed switch SNMP collection failed.")
{
    public Sg250CollectionStatus Status { get; } = status;
}

public static class Sg250Oids
{
    public const string IfDescr = ".1.3.6.1.2.1.2.2.1.2";
    public const string IfType = ".1.3.6.1.2.1.2.2.1.3";
    public const string IfAdminStatus = ".1.3.6.1.2.1.2.2.1.7";
    public const string IfOperStatus = ".1.3.6.1.2.1.2.2.1.8";
    public const string IfName = ".1.3.6.1.2.1.31.1.1.1.1";
    public const string IfHighSpeed = ".1.3.6.1.2.1.31.1.1.1.15";
    public const string BridgePortIfIndex = ".1.3.6.1.2.1.17.1.4.1.2";
    public const string BridgePortPvid = ".1.3.6.1.2.1.17.7.1.4.5.1.1";
    public const string BridgeAddress = ".1.3.6.1.2.1.17.1.1.0";

    public static readonly IReadOnlyList<string> WalkRoots =
    [
        IfDescr, IfType, IfName, IfAdminStatus, IfOperStatus, IfHighSpeed, BridgePortIfIndex, BridgePortPvid,
    ];
}

public sealed record Sg250Port(
    int IfIndex,
    string Name,
    bool? AdminUp,
    bool? OperUp,
    long? SpeedMbps,
    int? Pvid);

/// <summary>
/// A local, read-only observation of one explicitly configured switch, never proof of a physical uplink.
/// A globally administered bridge MAC is strong identity evidence; the management address and interface indices are not.
/// </summary>
public sealed record Sg250Snapshot(
    Sg250CollectionStatus Status,
    string SwitchEntityId,
    string CollectorPeerId,
    DateTimeOffset ObservedAt,
    string? BridgeMac,
    IReadOnlyList<Sg250Port> Ports,
    LanTopologyFragment Topology,
    string? SafeReason);

public sealed class CiscoSg250Adapter
{
    private readonly string switchEntityId;
    private readonly string collectorPeerId;
    private readonly ISg250SnmpReader reader;
    private readonly TimeProvider clock;

    public string SwitchEntityId => switchEntityId;
    public string CollectorPeerId => collectorPeerId;

    public CiscoSg250Adapter(
        string switchEntityId,
        string collectorPeerId,
        ISg250SnmpReader reader,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(switchEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectorPeerId);
        this.switchEntityId = switchEntityId;
        this.collectorPeerId = collectorPeerId;
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<Sg250Snapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var observedAt = clock.GetUtcNow();
        try
        {
            var values = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(values);
            return BuildSnapshot(values, observedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Sg250ReadException ex)
        {
            return Empty(ex.Status, observedAt);
        }
        catch (Exception)
        {
            // Neither local credentials nor raw SNMP exception text may flow into replicated data.
            return Empty(Sg250CollectionStatus.Error, observedAt);
        }
    }

    private Sg250Snapshot BuildSnapshot(IReadOnlyList<Sg250SnmpValue> values, DateTimeOffset at)
    {
        var byOid = values
            .Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Oid))
            .GroupBy(v => v.Oid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        var indexes = Indexes(byOid, Sg250Oids.IfName)
            .Concat(Indexes(byOid, Sg250Oids.IfDescr))
            .Distinct().OrderBy(i => i).ToList();

        if (indexes.Count == 0)
            return Empty(Sg250CollectionStatus.Unavailable, at);

        // dot1qPvid is keyed by bridge port, not by ifIndex. Never join without dot1dBasePortIfIndex.
        var pvidByInterface = new Dictionary<int, int>();
        foreach (var bridgePort in Indexes(byOid, Sg250Oids.BridgePortIfIndex))
        {
            var ifIndex = GetNumber(byOid, Sg250Oids.BridgePortIfIndex, bridgePort);
            var pvid = GetNumber(byOid, Sg250Oids.BridgePortPvid, bridgePort);
            if (ifIndex is null or < 1 or > int.MaxValue || pvid is null or < 1 or > 4094)
                continue;
            pvidByInterface[(int)ifIndex.Value] = (int)pvid.Value;
        }

        var ports = new List<Sg250Port>();
        foreach (var i in indexes)
        {
            // The IF-MIB also lists VLAN interfaces, loopbacks and LAGs. A port needs
            // a reported ethernetCsmacd(6) type; unknown types are not physical ports.
            if (GetNumber(byOid, Sg250Oids.IfType, i) != 6) continue;
            var name = GetText(byOid, Sg250Oids.IfName, i) ?? GetText(byOid, Sg250Oids.IfDescr, i);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var speed = GetNumber(byOid, Sg250Oids.IfHighSpeed, i);
            ports.Add(new Sg250Port(
                i, name.Length > 128 ? name[..128] : name,
                Status(GetNumber(byOid, Sg250Oids.IfAdminStatus, i)),
                Status(GetNumber(byOid, Sg250Oids.IfOperStatus, i)),
                speed is > 0 ? speed : null,
                pvidByInterface.TryGetValue(i, out var pvid) ? pvid : (int?)null));
        }

        if (ports.Count == 0) return Empty(Sg250CollectionStatus.Unavailable, at);

        var mac = ParseBridgeMac(byOid.GetValueOrDefault(Sg250Oids.BridgeAddress));
        var entities = new List<LanEntity>
        {
            new(new ObservedEntity(switchEntityId, "lan.switch", at), LanEntityKind.Switch),
        };
        var claims = new List<IdentityClaim>();
        var relationships = new List<(LanRelationship, RelationshipObservation)>();
        if (mac is not null)
            claims.Add(new IdentityClaim(switchEntityId, "lan.switch.bridge_mac", mac,
                IdentityStrength.Strong, collectorPeerId, RelationshipEvidenceKind.Reported, 0.95, at));

        foreach (var port in ports)
        {
            var portId = switchEntityId + ":if:" + port.IfIndex.ToString(CultureInfo.InvariantCulture);
            entities.Add(new LanEntity(new ObservedEntity(portId, "lan.port", at), LanEntityKind.Port));
            claims.Add(new IdentityClaim(portId, "lan.port.if_index", port.IfIndex.ToString(CultureInfo.InvariantCulture),
                IdentityStrength.Weak, collectorPeerId, RelationshipEvidenceKind.Reported, 1, at));
            claims.Add(new IdentityClaim(portId, "lan.port.if_name", port.Name,
                IdentityStrength.Weak, collectorPeerId, RelationshipEvidenceKind.Reported, 0.9, at));
            var relationship = new LanRelationship(portId + "|AttachedTo|" + switchEntityId,
                new LanEntityRef(portId, LanEntityKind.Port), new LanEntityRef(switchEntityId, LanEntityKind.Switch),
                LanRelationshipKind.AttachedTo, LanEvidenceKind.Reported, 1);
            var validation = LanTopologyValidator.Validate(relationship);
            if (!validation.IsValid) throw new InvalidOperationException("Switch topology contract is invalid.");
            relationships.Add((relationship,
                new RelationshipObservation(relationship.RelationshipId, portId, switchEntityId,
                    RelationshipKind.AttachedTo, RelationshipEvidenceKind.Reported,
                    collectorPeerId, 1, at, null)));
        }

        return new Sg250Snapshot(Sg250CollectionStatus.Available, switchEntityId, collectorPeerId, at,
            mac, ports, new LanTopologyFragment(collectorPeerId, entities, claims, relationships, []), null);
    }

    private Sg250Snapshot Empty(Sg250CollectionStatus state, DateTimeOffset at) =>
        new(state, switchEntityId, collectorPeerId, at, null, [],
            new LanTopologyFragment(collectorPeerId, [], [], [], []),
            state switch
            {
                Sg250CollectionStatus.PermissionDenied => "SNMP read not authorized.",
                Sg250CollectionStatus.Unavailable => "Switch or required interface MIB unavailable.",
                _ => "Switch probe failed.",
            });

    private static IEnumerable<int> Indexes(Dictionary<string, string> values, string prefix)
    {
        var start = prefix + ".";
        foreach (var oid in values.Keys)
            if (oid.StartsWith(start, StringComparison.Ordinal) &&
                int.TryParse(oid.AsSpan(start.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) &&
                i > 0)
                yield return i;
    }

    private static string? GetText(Dictionary<string, string> values, string prefix, int index)
    {
        if (!values.TryGetValue(prefix + "." + index.ToString(CultureInfo.InvariantCulture), out var text))
            return null;
        if (!text.StartsWith("STRING:", StringComparison.OrdinalIgnoreCase)) return null;
        var name = text["STRING:".Length..].Trim().Trim('"');
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static long? GetNumber(Dictionary<string, string> values, string prefix, int index)
    {
        if (!values.TryGetValue(prefix + "." + index.ToString(CultureInfo.InvariantCulture), out var text))
            return null;
        var colon = text.IndexOf(':');
        if (colon < 0) return null;
        var type = text[..colon].Trim();
        if (type is not ("INTEGER" or "Gauge32" or "Counter32" or "Unsigned32")) return null;
        var raw = text[(colon + 1)..].Trim();
        // net-snmp may display INTEGER: up(1) instead of INTEGER: 1.
        var open = raw.LastIndexOf('(');
        if (open >= 0 && raw.EndsWith(')')) raw = raw[(open + 1)..^1];
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool? Status(long? n) => n switch { 1 => true, 2 => false, _ => null };

    private static string? ParseBridgeMac(string? value)
    {
        if (value is null || !value.StartsWith("Hex-STRING:", StringComparison.OrdinalIgnoreCase)) return null;
        var bytes = value["Hex-STRING:".Length..].Trim();
        var hex = new string(bytes.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || hex.All(c => c == '0') ||
            !byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) ||
            (first & 0x03) != 0) return null; // not a stable globally administered unicast MAC
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToLowerInvariant()));
    }
}
