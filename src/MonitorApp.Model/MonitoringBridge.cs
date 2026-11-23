using MonitorApp.Peer.Persistence;

namespace MonitorApp.Model;

/// <summary>Something monitoring already knows, described by its existing monitoring identity.</summary>
/// <param name="StrongIdentity">Exact identity facts (for example a serial number) as library property key to value.</param>
/// <param name="InsidePeerId">When monitoring reports the subject as part of a peer (a computer's network adapter), that peer's id;
/// the bridge then places the object inside the peer's object.</param>
public sealed record MonitoredSubject(
    MonitoringSubjectKind Kind,
    string SubjectId,
    string DisplayName,
    string? KindHint,
    IReadOnlyDictionary<string, string> StrongIdentity,
    string? InsidePeerId = null);

public sealed record BridgeResult(int Created, int BoundExisting, int AlreadyBound);

/// <summary>
/// Turns monitored peers and observed devices into model objects without giving them a new identity
/// (docs/ARCHITECTURE-MODEL.md 3.8). The monitoring id stays the key for readings; the binding is permanent.
/// An existing object (entered by hand before monitoring found it) is bound instead of duplicated when a strong
/// identity fact matches exactly. Weak facts such as an IP address are never used.
/// </summary>
public static class MonitoringBridge
{
    public const string Origin = "monitoring-bridge";

    /// <summary>The object id given to a subject that had no object yet. Deterministic, so every device derives the same id.</summary>
    public static string ObjectIdFor(MonitoredSubject subject) =>
        subject.Kind == MonitoringSubjectKind.Peer ? $"peer:{subject.SubjectId}" : $"observed:{subject.SubjectId}";

    public static async Task<BridgeResult> EnsureAsync(ModelStore store, IEnumerable<MonitoredSubject> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subjects);
        int created = 0, bound = 0, already = 0;
        foreach (var subject in subjects)
        {
            var model = await store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (model.FindBound(subject.Kind, subject.SubjectId) is { } existing)
            {
                already++;
                // The container may have been bridged after the part, so placement is checked on every pass.
                if (Placement(model, subject, existing.Id) is { } late)
                    await store.AppendAsync(late, Origin, cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            var match = subject.StrongIdentity
                .SelectMany(fact => model.WithText(fact.Key, fact.Value))
                .Where(o => o.MonitoringBindings.All(b => b.Kind != subject.Kind))
                .Distinct()
                .ToList();
            var evidence = new Evidence(EvidenceLevel.Measured, Origin, subject.SubjectId);
            var changes = new List<ModelChange>();
            string objectId;
            if (match.Count == 1)
            {
                objectId = match[0].Id;
                bound++;
            }
            else
            {
                // Zero matches, or an ambiguous match that a person must resolve: create a separate object.
                objectId = ObjectIdFor(subject);
                if (model.Find(objectId) is null)
                {
                    changes.Add(new ObjectCreated(objectId, subject.DisplayName, subject.KindHint ?? "device", Lifecycle.Owned));
                    created++;
                }
            }

            foreach (var fact in subject.StrongIdentity)
                changes.Add(new PropertyAsserted(objectId, fact.Key, PropertyValue.Of(fact.Value), evidence));
            changes.Add(new MonitoringBound(objectId, subject.Kind, subject.SubjectId));
            model = await store.AppendAsync(changes, Origin, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Placement(model, subject, objectId) is { } placement)
                await store.AppendAsync(placement, Origin, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return new BridgeResult(created, bound, already);
    }

    /// <summary>Containment for a part monitoring reports inside a peer, unless the object is already placed somewhere (a person may have moved it).</summary>
    private static RelationshipAdded? Placement(SystemModel model, MonitoredSubject subject, string objectId)
    {
        if (subject.InsidePeerId is null || model.ContainerOf(objectId) is not null) return null;
        if (model.FindBound(MonitoringSubjectKind.Peer, subject.InsidePeerId) is not { } parent || parent.Id == objectId) return null;
        return new RelationshipAdded($"bridge:{parent.Id}>{objectId}", RelationshipTypes.Contains, parent.Id, objectId);
    }

    /// <summary>
    /// Reads this peer, its active trusted peers and what they observed from the monitoring store and bridges them.
    /// Network adapters become parts inside their computer, networks become network segments and gateways become routers.
    /// A peer's own LAN record and the "unknown upstream" placeholders are not things and are skipped.
    /// </summary>
    /// <param name="localName">Name for this device's object, such as the machine name; "This device" when null.</param>
    /// <param name="localKind">Kind for this device's object ("device.phone" on a phone); a computer when null.</param>
    public static async Task<BridgeResult> SyncFromPeerStoreAsync(
        PeerStore peers, ModelStore store, string? localName = null, string? localKind = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var subjects = new List<MonitoredSubject>();
        if (await peers.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) is { } local)
            subjects.Add(PeerSubject(local.PeerId, string.IsNullOrWhiteSpace(localName) ? "This device" : localName, local.PublicKeyFingerprint, localKind));

        foreach (var peer in await peers.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false))
            if (peer.TrustState == PeerTrustState.Active)
                subjects.Add(PeerSubject(peer.PeerId, peer.DisplayName ?? peer.PeerId, peer.PublicKeyFingerprint));

        foreach (var entity in await peers.ReadObservedEntitiesAsync(cancellationToken).ConfigureAwait(false))
        {
            var shape = Describe(entity);
            if (shape is null) continue;
            var claims = await peers.ReadIdentityClaimsAsync(entity.EntityId, cancellationToken).ConfigureAwait(false);
            var strong = claims.Where(c => c.Strength == IdentityStrength.Strong)
                .GroupBy(c => "identity." + c.ClaimType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().NormalizedValue, StringComparer.Ordinal);
            string? inside = null;
            if (shape.Value.IsPart)
            {
                var attached = (await peers.ReadRelationshipsAsync(entity.EntityId, cancellationToken).ConfigureAwait(false))
                    .Where(r => r.Kind == RelationshipKind.AttachedTo && r.SourceEntityId == entity.EntityId
                        && r.TargetEntityId.StartsWith(PeerEntityPrefix, StringComparison.Ordinal))
                    .OrderByDescending(r => r.ValidFrom)
                    .FirstOrDefault();
                inside = attached?.TargetEntityId[PeerEntityPrefix.Length..];
            }

            subjects.Add(new MonitoredSubject(MonitoringSubjectKind.ObservedDevice, entity.EntityId, shape.Value.Name, shape.Value.Kind, strong, inside));
        }

        var result = await EnsureAsync(store, subjects, cancellationToken).ConfigureAwait(false);
        await EnsureMeasuredPartsAsync(peers, store, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The property that names which of the container's readings belong to a part (a signal id prefix).</summary>
    public const string SignalsKey = "monitoring.signals";

    /// <summary>
    /// Parts whose existence a device's own readings prove: a device that reports processor load has a processor. Each rule is a
    /// signal id prefix and the part it shows. Data, not per-device code: a new collector adds a row.
    /// </summary>
    public static IReadOnlyList<(string SignalPrefix, string Slot, string KindId, string Name)> MeasuredPartRules { get; } =
    [
        ("system.cpu.", "processor", "part.processor", "Processor"),
        ("system.memory.", "memory", "part.memory", "Memory"),
        ("system.storage.", "storage", "part.storage", "Storage"),
        ("android.battery.", "battery", "part.battery", "Battery"),
    ];

    /// <summary>
    /// Adds the parts that readings prove (processor, memory, storage, battery) inside each monitored device, and tags every
    /// part with the readings that belong to it (<see cref="SignalsKey"/>), so a screen can show live values per part.
    /// Idempotent: ids are derived from the device's object id, and nothing is re-asserted once present.
    /// </summary>
    private static async Task EnsureMeasuredPartsAsync(PeerStore peers, ModelStore store, CancellationToken cancellationToken)
    {
        var signalsBySubject = (await peers.ReadCurrentObservationsAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(o => o.SubjectEntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(o => o.SignalId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var model = await store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = new List<ModelChange>();
        foreach (var device in model.Objects.Where(o => o.CreatedInLayer is null))
        {
            foreach (var binding in device.MonitoringBindings)
            {
                if (binding.Kind == MonitoringSubjectKind.Peer
                    && signalsBySubject.TryGetValue(PeerEntityPrefix + binding.SubjectId, out var signals))
                {
                    foreach (var rule in MeasuredPartRules.Where(r => signals.Any(id => id.StartsWith(r.SignalPrefix, StringComparison.Ordinal))))
                    {
                        var partId = $"{device.Id}/{rule.Slot}";
                        if (model.Find(partId) is not null) continue;
                        var evidence = new Evidence(EvidenceLevel.Measured, Origin, binding.SubjectId);
                        changes.Add(new ObjectCreated(partId, rule.Name, rule.KindId, Lifecycle.Installed));
                        changes.Add(new PropertyAsserted(partId, SignalsKey, PropertyValue.Of(rule.SignalPrefix), evidence));
                        changes.Add(new RelationshipAdded($"bridge:{device.Id}>{partId}", RelationshipTypes.Contains, device.Id, partId));
                    }
                }
                else if (binding.Kind == MonitoringSubjectKind.ObservedDevice && device.Strongest(SignalsKey) is null
                    && InterfaceName(binding.SubjectId) is { } name)
                {
                    changes.Add(new PropertyAsserted(device.Id, SignalsKey, PropertyValue.Of($"lan.link.{name}."),
                        new Evidence(EvidenceLevel.Measured, Origin, binding.SubjectId)));
                }
            }
        }

        if (changes.Count > 0)
            await store.AppendAsync(changes, Origin, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"lan:peer:if:wlan0" gives "wlan0"; anything that is not an interface gives null.</summary>
    private static string? InterfaceName(string entityId)
    {
        var parts = entityId.Split(':');
        return parts.Length >= 4 && parts[0] == "lan" && parts[2] == "if" ? string.Join(':', parts[3..]) : null;
    }

    private const string PeerEntityPrefix = "peer:";

    /// <summary>Name and kind for an observed entity, or null when it is not a thing of its own.</summary>
    private static (string Name, string Kind, bool IsPart)? Describe(ObservedEntity entity)
    {
        var tail = LastIdPart(entity.EntityId);
        return entity.EntityKind switch
        {
            "lan.peer" or "lan.unknown_upstream_segment" => null,
            "lan.network_interface" => ($"Network adapter {tail}", "part.network", true),
            "lan.overlay_interface" => ($"Virtual network adapter {tail}", "part.network.virtual", true),
            "lan.segment" => ($"Network {tail}", "network.segment", false),
            "lan.vlan" => ($"VLAN {tail}", "network.segment", false),
            "lan.gateway" => ($"Router at {tail}", "device.network.router", false),
            "lan.switch" => ($"Network switch {tail}", "device.network.switch", false),
            "lan.peripheral" => ($"Device {tail}", "device", false),
            _ => (entity.EntityId, KindFor(entity.EntityKind), false),
        };
    }

    /// <summary>The readable end of a LAN entity id ("lan:peer:if:eth0" gives "eth0"; a CIDR keeps its slash).</summary>
    private static string LastIdPart(string entityId)
    {
        var parts = entityId.Split(':');
        return parts.Length >= 4 && parts[0] == "lan" ? string.Join(':', parts[3..]) : entityId;
    }

    private static MonitoredSubject PeerSubject(string peerId, string name, byte[] fingerprint, string? kind = null) =>
        new(MonitoringSubjectKind.Peer, peerId, name, kind ?? "device.computer",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["identity.peer-key-fingerprint"] = Convert.ToHexString(fingerprint) });

    private static string KindFor(string monitoringKind) => monitoringKind.ToUpperInvariant() switch
    {
        "UPS" => "device.power.ups",
        "SWITCH" or "MANAGED_SWITCH" => "device.network.switch",
        "AUDIO_INTERFACE" => "device.audio.interface",
        "SERVICE" => "software.service",
        _ => "device",
    };
}
