namespace MonitorApp.Client.Core.Scene;

/// <summary>
/// Pure projection of node data into a <see cref="SceneGraph"/>. Nothing here touches the network or a clock;
/// the caller supplies <c>now</c>. Layout is not decided here (the returned graph has an empty layout); see
/// <see cref="StableLayout"/>.
/// <para>
/// Ownership rules: a subject <c>peer:{id}</c> (or the bare id) belongs to that peer's node. Any other subject
/// becomes a Device child of the peer that originated its data. Data from a revoked peer is not shown. Data whose
/// origin is not a known peer at all is attached to the local peer, which holds it.
/// </para>
/// </summary>
public static class SceneProjector
{
    public const string PeerSubjectPrefix = "peer:";
    public const string LocalLabel = "This device";

    public static SceneGraph Project(
        RuntimeDto runtime,
        IReadOnlyList<TrustedPeerDto> peers,
        StateDto state,
        NetworkRates? rates,
        DateTimeOffset now,
        bool isHistorical,
        HealthClassifier? classifier = null,
        string? localLabel = null)
    {
        classifier ??= new HealthClassifier();
        rates ??= NetworkRates.None;
        var localId = runtime.LocalPeer?.PeerId;
        var active = peers
            .Where(p => string.Equals(p.TrustState, "Active", StringComparison.OrdinalIgnoreCase) && p.PeerId != localId)
            .OrderBy(p => p.PeerId, StringComparer.Ordinal)
            .ToList();
        var revoked = peers
            .Where(p => !string.Equals(p.TrustState, "Active", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PeerId)
            .ToHashSet(StringComparer.Ordinal);
        var activeIds = active.Select(p => p.PeerId).ToHashSet(StringComparer.Ordinal);

        // Assign each point to a node id.
        var peerPoints = new Dictionary<string, List<HistoryPointDto>>(StringComparer.Ordinal);
        var devicePoints = new Dictionary<string, List<HistoryPointDto>>(StringComparer.Ordinal);
        var deviceParent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var point in state.Points
            .OrderBy(p => p.OriginPeerId, StringComparer.Ordinal)
            .ThenBy(p => p.SubjectEntityId, StringComparer.Ordinal)
            .ThenBy(p => p.SignalId, StringComparer.Ordinal))
        {
            var subject = point.SubjectEntityId;
            var owner = subject.StartsWith(PeerSubjectPrefix, StringComparison.Ordinal) ? subject[PeerSubjectPrefix.Length..] : subject;
            if (owner == localId || activeIds.Contains(owner))
            {
                Add(peerPoints, owner, point);
                continue;
            }

            if (revoked.Contains(owner) || revoked.Contains(point.OriginPeerId))
            {
                continue;
            }

            string? parent = point.OriginPeerId == localId || activeIds.Contains(point.OriginPeerId)
                ? point.OriginPeerId
                : localId;
            if (parent is null)
            {
                continue;
            }

            Add(devicePoints, subject, point);
            deviceParent.TryAdd(subject, parent);
        }

        var nodes = new List<SceneNode>();
        var links = new List<SceneLink>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        SceneNode MakeNode(string id, string label, SceneNodeKind kind, string? parentId, List<HistoryPointDto>? points, string rateSubject)
        {
            var readings = (points ?? []).Select(SignalCatalog.ToReading).ToList();
            var node = classifier.Node(readings);
            labels[id] = label;
            return new SceneNode(
                id, label, kind, node.Health, node.Reason, parentId,
                LayerBuilder.Build(readings, classifier), rates.ActivityFor(rateSubject), node.LastSeen);
        }

        if (localId is not null)
        {
            nodes.Add(MakeNode(localId, localLabel ?? LocalLabel, SceneNodeKind.LocalPeer, null, peerPoints.GetValueOrDefault(localId), PeerSubjectPrefix + localId));
        }

        foreach (var peer in active)
        {
            var node = MakeNode(
                peer.PeerId, Name(peer), SceneNodeKind.Peer, null, peerPoints.GetValueOrDefault(peer.PeerId), PeerSubjectPrefix + peer.PeerId);
            nodes.Add(node);
            if (localId is not null)
            {
                links.Add(new SceneLink(
                    $"replication:{localId}:{peer.PeerId}", localId, peer.PeerId, SceneLinkKind.Replication,
                    node.Health, node.Activity, $"Paired with {node.Label}; origin history replicates between the two"));
            }
        }

        foreach (var (deviceId, points) in devicePoints.OrderBy(d => deviceParent[d.Key], StringComparer.Ordinal).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            var parentId = deviceParent[deviceId];
            var node = MakeNode(deviceId, deviceId, SceneNodeKind.Device, parentId, points, deviceId);
            nodes.Add(node);
            links.Add(new SceneLink(
                $"hosting:{parentId}:{deviceId}", parentId, deviceId, SceneLinkKind.Hosting,
                node.Health, node.Activity, $"{labels.GetValueOrDefault(parentId, parentId)} collects {deviceId}"));
        }

        return new SceneGraph(isHistorical ? state.AsOf : now, isHistorical, nodes, links, new Dictionary<string, ScenePoint>());
    }

    /// <summary>Display name for a peer: its display name, else a shortened id.</summary>
    public static string Name(TrustedPeerDto peer) =>
        string.IsNullOrWhiteSpace(peer.DisplayName) ? ShortId(peer.PeerId) : peer.DisplayName.Trim();

    public static string ShortId(string peerId) => peerId.Length <= 12 ? peerId : peerId[..8] + "...";

    private static void Add(Dictionary<string, List<HistoryPointDto>> map, string key, HistoryPointDto point)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(point);
    }
}
