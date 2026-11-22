using MonitorApp.Peer.Persistence;

namespace MonitorApp.Model;

/// <summary>Something monitoring already knows, described by its existing monitoring identity.</summary>
/// <param name="StrongIdentity">Exact identity facts (for example a serial number) as library property key to value.</param>
public sealed record MonitoredSubject(
    MonitoringSubjectKind Kind,
    string SubjectId,
    string DisplayName,
    string? KindHint,
    IReadOnlyDictionary<string, string> StrongIdentity);

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
            if (model.FindBound(subject.Kind, subject.SubjectId) is not null)
            {
                already++;
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
            await store.AppendAsync(changes, Origin, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return new BridgeResult(created, bound, already);
    }

    /// <summary>Reads this peer, its active trusted peers and its observed devices from the monitoring store and bridges them.</summary>
    public static async Task<BridgeResult> SyncFromPeerStoreAsync(PeerStore peers, ModelStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var subjects = new List<MonitoredSubject>();
        if (await peers.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false) is { } local)
            subjects.Add(PeerSubject(local.PeerId, "This device", local.PublicKeyFingerprint));

        foreach (var peer in await peers.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false))
            if (peer.TrustState == PeerTrustState.Active)
                subjects.Add(PeerSubject(peer.PeerId, peer.DisplayName ?? peer.PeerId, peer.PublicKeyFingerprint));

        foreach (var entity in await peers.ReadObservedEntitiesAsync(cancellationToken).ConfigureAwait(false))
        {
            var claims = await peers.ReadIdentityClaimsAsync(entity.EntityId, cancellationToken).ConfigureAwait(false);
            var strong = claims.Where(c => c.Strength == IdentityStrength.Strong)
                .GroupBy(c => "identity." + c.ClaimType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().NormalizedValue, StringComparer.Ordinal);
            subjects.Add(new MonitoredSubject(MonitoringSubjectKind.ObservedDevice, entity.EntityId, entity.EntityId, KindFor(entity.EntityKind), strong));
        }

        return await EnsureAsync(store, subjects, cancellationToken).ConfigureAwait(false);
    }

    private static MonitoredSubject PeerSubject(string peerId, string name, byte[] fingerprint) =>
        new(MonitoringSubjectKind.Peer, peerId, name, "device.computer",
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
