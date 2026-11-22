using MonitorApp.Contracts;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Collection.Lan;

public sealed record LanEntity(ObservedEntity Entity, LanEntityKind Kind);

public sealed record LanTopologyFragment(
    string OwnerPeerId,
    IReadOnlyList<LanEntity> Entities,
    IReadOnlyList<IdentityClaim> Claims,
    IReadOnlyList<(LanRelationship Contract, RelationshipObservation Observation)> Relationships,
    IReadOnlyList<SelfInterface> InterfaceAttributes);

public static class LanEntityIds
{
    public static string Peer(string peerId) => $"peer:{peerId}";
    public static string Interface(string peerId, string name) => $"lan:{peerId}:if:{name}";
    public static string Segment(string peerId, string cidr) => $"lan:{peerId}:seg:{cidr}";
    public static string Vlan(string peerId, int id) => $"lan:{peerId}:vlan:{id}";
    public static string Gateway(string peerId, string ip) => $"lan:{peerId}:gw:{ip}";
    public static string Upstream(string peerId, string cidr) => $"lan:{peerId}:upstream:{cidr}";
}

public static class LanClaimTypes
{
    public const string InterfaceMac = "lan.interface.mac";
    public const string InterfaceMedium = "lan.interface.medium";
    public const string SegmentSubnet = "lan.segment.subnet";
    public const string SegmentGatewayIp = "lan.segment.gateway_ip";
    public const string GatewayIp = "lan.gateway.ip";
    public const string VlanId = "lan.vlan.id";
}

/// <summary>
/// Builds the LAN entities a peer can honestly assert about itself. Every interface, claim and relationship is
/// observed and owned by <c>ownerPeerId</c>; nothing about other hosts is read or inferred beyond unknown-upstream placeholders.
/// </summary>
public static class LanTopologyBuilder
{
    public static LanTopologyFragment Build(string ownerPeerId, SelfInterfaceSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPeerId);
        ArgumentNullException.ThrowIfNull(snapshot);
        var at = snapshot.CollectedAt.ToUniversalTime();
        var entities = new Dictionary<string, LanEntity>(StringComparer.Ordinal);
        var claims = new List<IdentityClaim>();
        var rels = new List<(LanRelationship, RelationshipObservation)>();
        if (snapshot.State != SelfCollectionState.Collected) return new(ownerPeerId, [], [], [], []);

        var peerRef = AddEntity(LanEntityIds.Peer(ownerPeerId), LanEntityKind.Peer);
        var interfaces = snapshot.Interfaces.Where(i => i.Medium != InterfaceMedium.Loopback).ToList();

        foreach (var nic in interfaces)
        {
            var kind = nic.IsOverlay ? LanEntityKind.OverlayInterface : LanEntityKind.NetworkInterface;
            var ifRef = AddEntity(LanEntityIds.Interface(ownerPeerId, nic.Name), kind);
            Claim(ifRef.EntityId, LanClaimTypes.InterfaceMedium, nic.Medium.ToString().ToLowerInvariant(), IdentityStrength.Weak, 0.9);
            // The interface's own hardware MAC is strong identity for that interface, but only for hardware-backed links;
            // virtual/overlay MACs are random or reassigned. Gateway/neighbor MACs are never collected.
            if (nic.IsPhysical && nic.Mac is not null) Claim(ifRef.EntityId, LanClaimTypes.InterfaceMac, nic.Mac, IdentityStrength.Strong, 0.95);
            Relate(ifRef, peerRef, LanRelationshipKind.AttachedTo, LanEvidenceKind.Reported, 0.95);
            if (nic.IsOverlay) continue; // overlay stays separate: no segment, VLAN or gateway edges.

            if (nic.Medium == InterfaceMedium.Vlan && nic.VlanId is { } vid)
            {
                var vlan = AddEntity(LanEntityIds.Vlan(ownerPeerId, vid), LanEntityKind.Vlan);
                Claim(vlan.EntityId, LanClaimTypes.VlanId, vid.ToString(System.Globalization.CultureInfo.InvariantCulture), IdentityStrength.Weak, 0.6);
                Relate(ifRef, vlan, LanRelationshipKind.MemberOfVlan, LanEvidenceKind.Reported, 0.9);
            }

            if (!nic.IsUp || nic.Medium is not (InterfaceMedium.Ethernet or InterfaceMedium.WiFi or InterfaceMedium.Vlan)) continue;
            foreach (var cidr in nic.Subnets.Where(c => !SubnetMath.IsOverlayRange(c)))
            {
                var seg = AddEntity(LanEntityIds.Segment(ownerPeerId, cidr), LanEntityKind.Segment);
                Claim(seg.EntityId, LanClaimTypes.SegmentSubnet, cidr, IdentityStrength.Weak, 0.4);
                Relate(ifRef, seg, LanRelationshipKind.MemberOfSegment, LanEvidenceKind.Reported, 0.9);
                // Switch/port topology is unknowable without a managed switch; record only an inferred placeholder.
                var up = AddEntity(LanEntityIds.Upstream(ownerPeerId, cidr), LanEntityKind.UnknownUpstreamSegment);
                Relate(seg, up, LanRelationshipKind.UplinkTo, LanEvidenceKind.Inferred, 0.2);
                foreach (var gw in snapshot.Gateways.Where(g => g.InterfaceName == nic.Name).DistinctBy(g => g.Address))
                {
                    var g = AddEntity(LanEntityIds.Gateway(ownerPeerId, gw.Address), LanEntityKind.Gateway);
                    Claim(g.EntityId, LanClaimTypes.GatewayIp, gw.Address, IdentityStrength.Weak, 0.5);
                    Claim(seg.EntityId, LanClaimTypes.SegmentGatewayIp, gw.Address, IdentityStrength.Weak, 0.5);
                    Relate(seg, g, LanRelationshipKind.RoutesVia, LanEvidenceKind.Reported, 0.8);
                }
            }
        }

        return new(ownerPeerId, entities.Values.ToArray(), claims, rels, interfaces);

        LanEntityRef AddEntity(string id, LanEntityKind kind)
        {
            entities.TryAdd(id, new LanEntity(new ObservedEntity(id, ToKindString(kind), at), kind));
            return new LanEntityRef(id, kind);
        }

        void Claim(string entityId, string type, string value, IdentityStrength strength, double confidence) =>
            claims.Add(new IdentityClaim(entityId, type, value, strength, ownerPeerId, RelationshipEvidenceKind.Reported, confidence, at));

        void Relate(LanEntityRef source, LanEntityRef target, LanRelationshipKind kind, LanEvidenceKind evidence, double confidence)
        {
            var id = $"{source.EntityId}|{kind}|{target.EntityId}";
            var contract = new LanRelationship(id, source, target, kind, evidence, confidence);
            var result = LanTopologyValidator.Validate(contract);
            if (!result.IsValid) throw new InvalidOperationException(string.Join(' ', result.Errors));
            rels.Add((contract, ToObservation(contract, ownerPeerId, at)));
        }
    }

    public static string ToKindString(LanEntityKind kind) => "lan." + System.Text.RegularExpressions.Regex.Replace(kind.ToString(), "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    internal static RelationshipObservation ToObservation(LanRelationship r, string observer, DateTimeOffset at) =>
        new(r.RelationshipId, r.Source.EntityId, r.Target.EntityId, r.Kind switch
        {
            LanRelationshipKind.AttachedTo => RelationshipKind.AttachedTo,
            LanRelationshipKind.MemberOfSegment => RelationshipKind.MemberOfSegment,
            LanRelationshipKind.MemberOfVlan => RelationshipKind.MemberOfVlan,
            LanRelationshipKind.RoutesVia => RelationshipKind.RoutesVia,
            LanRelationshipKind.UplinkTo => RelationshipKind.UplinkTo,
            LanRelationshipKind.ConnectedToPort => RelationshipKind.ConnectedToPort,
            LanRelationshipKind.ProbableSameSegment => RelationshipKind.ProbableSameSegment,
            _ => throw new ArgumentOutOfRangeException(nameof(r)),
        }, r.Evidence == LanEvidenceKind.Reported ? RelationshipEvidenceKind.Reported : RelationshipEvidenceKind.Inferred,
        observer, r.Confidence, at, null);
}

/// <summary>
/// Segments are observed per peer. Two peers' segments are linked only as "probable same segment" with capped confidence;
/// matching IP-derived weak claims never merges entities or creates a strong identity.
/// </summary>
public static class LanSegmentReconciler
{
    public const double SubnetOnlyConfidence = 0.3;
    public const double SubnetAndGatewayConfidence = 0.6;

    public sealed record SegmentObservation(string ObserverPeerId, string SegmentEntityId, IReadOnlyList<IdentityClaim> Claims);

    public static IReadOnlyList<(LanRelationship Contract, RelationshipObservation Observation)> Reconcile(
        IReadOnlyList<SegmentObservation> segments, DateTimeOffset at)
    {
        var result = new List<(LanRelationship, RelationshipObservation)>();
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                var a = segments[i]; var b = segments[j];
                if (a.ObserverPeerId == b.ObserverPeerId) continue;
                var subnet = Common(a, b, LanClaimTypes.SegmentSubnet);
                if (!subnet) continue;
                var confidence = Common(a, b, LanClaimTypes.SegmentGatewayIp) ? SubnetAndGatewayConfidence : SubnetOnlyConfidence;
                var (lo, hi) = string.CompareOrdinal(a.SegmentEntityId, b.SegmentEntityId) <= 0 ? (a, b) : (b, a);
                var contract = new LanRelationship($"{lo.SegmentEntityId}|ProbableSameSegment|{hi.SegmentEntityId}",
                    new(lo.SegmentEntityId, LanEntityKind.Segment), new(hi.SegmentEntityId, LanEntityKind.Segment),
                    LanRelationshipKind.ProbableSameSegment, LanEvidenceKind.Inferred, confidence);
                var v = LanTopologyValidator.Validate(contract);
                if (!v.IsValid) throw new InvalidOperationException(string.Join(' ', v.Errors));
                // Observer is the lexically first peer so the record has one stable owner; both inputs were reported by their own peers.
                result.Add((contract, LanTopologyBuilder.ToObservation(contract, lo.ObserverPeerId, at)));
            }
        }

        return result;
    }

    private static bool Common(SegmentObservation a, SegmentObservation b, string type) =>
        a.Claims.Where(c => c.ClaimType == type).Select(c => c.NormalizedValue)
            .Intersect(b.Claims.Where(c => c.ClaimType == type).Select(c => c.NormalizedValue), StringComparer.Ordinal).Any();
}

/// <summary>
/// The collector a peer node registers: collects its own interfaces and persists them. Ownership is explicit:
/// every record is observed by <see cref="OwnerPeerId"/> and describes only that peer's own interfaces.
/// </summary>
public sealed class SelfLanCollector(string ownerPeerId, ISelfInterfaceCollector source, PeerStore store, TimeProvider? clock = null)
{
    public string OwnerPeerId { get; } = ownerPeerId;

    public async Task<LanTopologyFragment> CollectAndPersistAsync(CancellationToken cancellationToken = default)
    {
        var fragment = LanTopologyBuilder.Build(OwnerPeerId, source.Collect((clock ?? TimeProvider.System).GetUtcNow()));
        foreach (var e in fragment.Entities)
        {
            try { await store.UpsertObservedEntityAsync(e.Entity, cancellationToken).ConfigureAwait(false); }
            catch (PeerPersistenceConflictException) { /* entity already exists with its original creation time */ }
        }

        foreach (var c in fragment.Claims) await store.AddIdentityClaimAsync(c, cancellationToken).ConfigureAwait(false);
        foreach (var (_, o) in fragment.Relationships) await store.UpsertRelationshipAsync(o, cancellationToken).ConfigureAwait(false);
        return fragment;
    }
}
