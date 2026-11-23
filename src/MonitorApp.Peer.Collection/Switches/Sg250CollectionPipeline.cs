using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Collection.Switches;

/// <summary>
/// An opt-in, peer-local composition of SG250 collection and the existing SQLite topology store.
/// A provisioned adapter and current collector assignment are mandatory before any SNMP request.
/// Node scheduling, UI and signed origin-observation delivery are deliberately separate concerns.
/// </summary>
public sealed class Sg250CollectionPipeline
{
    private const string AdapterId = "monitor.cisco.sg250";
    private readonly CiscoSg250Adapter adapter;
    private readonly string adapterInstanceId;
    private readonly PeerStore store;

    public Sg250CollectionPipeline(CiscoSg250Adapter adapter, string adapterInstanceId, PeerStore store)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterInstanceId);
        this.adapterInstanceId = adapterInstanceId;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<Sg250Snapshot> CollectAndPersistAsync(CancellationToken cancellationToken = default)
    {
        // Check before contacting the device. Knowing a switch or its management IP is not a grant.
        if (!await IsAuthorizedAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Switch collection requires an enabled adapter and current explicit collector assignment.");

        var snapshot = await adapter.CollectAsync(cancellationToken).ConfigureAwait(false);
        // Ownership or adapter enablement can change while the network request is running.
        // A stale collector may finish its read, but it must not persist status or topology.
        if (!await IsAuthorizedAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Switch collector assignment changed during collection.");

        if (snapshot.Status != Sg250CollectionStatus.Available)
        {
            await RecordStatusAsync(snapshot, cancellationToken).ConfigureAwait(false);
            // An unsuccessful poll is not evidence that a formerly present port has disappeared.
            return snapshot;
        }

        // Fail closed if the same configured entity acquires a conflicting strong identity.
        // An unknown bridge address is allowed, but a different reported one is not silently merged.
        foreach (var claim in snapshot.Topology.Claims.Where(c => c.Strength == IdentityStrength.Strong))
        {
            var existing = await store.ReadIdentityClaimsAsync(claim.EntityId, cancellationToken).ConfigureAwait(false);
            if (existing.Any(c => c.Strength == IdentityStrength.Strong &&
                                  c.ClaimType == claim.ClaimType &&
                                  c.NormalizedValue != claim.NormalizedValue))
            {
                var rejected = snapshot with
                {
                    Status = Sg250CollectionStatus.Error,
                    BridgeMac = null,
                    Ports = [],
                    Topology = new(snapshot.CollectorPeerId, [], [], [], []),
                    SafeReason = "Reported switch identity conflicts with previous evidence.",
                };
                await RecordStatusAsync(rejected, cancellationToken).ConfigureAwait(false);
                return rejected;
            }
        }

        var storedEntities = (await store.ReadObservedEntitiesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(entity => entity.EntityId, StringComparer.Ordinal);
        if (snapshot.Topology.Entities.Any(entity =>
                storedEntities.TryGetValue(entity.Entity.EntityId, out var stored) &&
                stored.EntityKind != entity.Entity.EntityKind))
        {
            return await RejectEntityConflictAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        foreach (var entity in snapshot.Topology.Entities)
        {
            try
            {
                await store.UpsertObservedEntityAsync(entity.Entity, cancellationToken).ConfigureAwait(false);
            }
            catch (PeerPersistenceConflictException)
            {
                // The object was created during an earlier poll. Creation time is immutable.
                // Accept that only when the stored entity still has the same kind. A colliding
                // id for another kind must not be relabelled and attached as a switch port.
                var stored = (await store.ReadObservedEntitiesAsync(cancellationToken).ConfigureAwait(false))
                    .SingleOrDefault(candidate => candidate.EntityId == entity.Entity.EntityId);
                if (stored is null || stored.EntityKind != entity.Entity.EntityKind)
                    return await RejectEntityConflictAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var claim in snapshot.Topology.Claims)
            await store.AddIdentityClaimAsync(claim, cancellationToken).ConfigureAwait(false);

        var existingRelationships = await store.ReadRelationshipsAsync(adapter.SwitchEntityId, cancellationToken)
            .ConfigureAwait(false);
        var ownPortPrefix = adapter.SwitchEntityId + ":if:";
        var ownRelationships = existingRelationships
            .Where(r => r.ObserverPeerId == adapter.CollectorPeerId &&
                        r.Kind == RelationshipKind.AttachedTo &&
                        r.TargetEntityId == adapter.SwitchEntityId &&
                        r.SourceEntityId.StartsWith(ownPortPrefix, StringComparison.Ordinal))
            .ToDictionary(r => r.RelationshipId, StringComparer.Ordinal);
        var current = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (_, observed) in snapshot.Topology.Relationships)
        {
            current.Add(observed.RelationshipId);
            var since = ownRelationships.TryGetValue(observed.RelationshipId, out var previous)
                ? previous.ValidFrom : observed.ValidFrom;
            await store.UpsertRelationshipAsync(observed with { ValidFrom = since, ValidTo = null }, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var previous in ownRelationships.Values)
        {
            if (previous.ValidTo is not null || current.Contains(previous.RelationshipId)) continue;
            var endedAt = snapshot.ObservedAt > previous.ValidFrom
                ? snapshot.ObservedAt : previous.ValidFrom.AddTicks(1);
            await store.UpsertRelationshipAsync(previous with { ValidTo = endedAt }, cancellationToken)
                .ConfigureAwait(false);
        }

        await RecordStatusAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken)
    {
        var configured = await store.ReadAdapterReplicationDescriptorAsync(adapterInstanceId, cancellationToken)
            .ConfigureAwait(false);
        var assignment = await store.ReadCollectorAssignmentAsync(adapter.SwitchEntityId, cancellationToken)
            .ConfigureAwait(false);
        return configured is not null && configured.Enabled &&
               configured.AdapterId == AdapterId &&
               configured.ObservedEntityId == adapter.SwitchEntityId &&
               configured.CollectorPeerId == adapter.CollectorPeerId &&
               assignment is not null &&
               assignment.AdapterInstanceId == adapterInstanceId &&
               assignment.ObservedEntityId == adapter.SwitchEntityId &&
               assignment.CollectorPeerId == adapter.CollectorPeerId;
    }

    private async Task<Sg250Snapshot> RejectEntityConflictAsync(
        Sg250Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var rejected = snapshot with
        {
            Status = Sg250CollectionStatus.Error,
            BridgeMac = null,
            Ports = [],
            Topology = new(snapshot.CollectorPeerId, [], [], [], []),
            SafeReason = "Stored entity identity conflicts with switch topology.",
        };
        await RecordStatusAsync(rejected, cancellationToken).ConfigureAwait(false);
        return rejected;
    }

    private Task RecordStatusAsync(Sg250Snapshot snapshot, CancellationToken cancellationToken) =>
        store.RecordAdapterStatusAsync(new AdapterStatus(
            adapterInstanceId,
            snapshot.Status switch
            {
                Sg250CollectionStatus.Available => "available",
                Sg250CollectionStatus.Unavailable => "unavailable",
                Sg250CollectionStatus.PermissionDenied => "permission_denied",
                _ => "error",
            },
            snapshot.SafeReason,
            snapshot.ObservedAt.ToUniversalTime()), cancellationToken);
}
