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

    /// <summary>Subjects of devices found on a home network (net:{mac}) and on the tailnet (ts:{id}).</summary>
    public const string NetworkPrefix = "net:";
    public const string TailnetPrefix = "ts:";
    private const string FoundName = "net.device.name";
    private static readonly (string Id, string Name)[] DrawnServices = [("tailscale", "Tailscale"), ("syncthing", "Syncthing")];

    public static SceneGraph Project(
        RuntimeDto runtime,
        IReadOnlyList<TrustedPeerDto> peers,
        StateDto state,
        NetworkRates? rates,
        DateTimeOffset now,
        bool isHistorical,
        HealthClassifier? classifier = null,
        string? localLabel = null,
        IReadOnlyDictionary<string, string>? thingNames = null)
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

        // Tailscale and Syncthing are drawn as their own nodes beside the computer that runs them, with their own status.
        foreach (var (peerId, points) in peerPoints.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
        {
            if (!labels.ContainsKey(peerId)) continue;
            foreach (var (service, name) in DrawnServices)
            {
                var prefix = $"service.{service}.";
                var mine = points.Where(p => p.SignalId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (mine.Count == 0) continue;
                var serviceId = $"svc:{peerId}:{service}";
                var node = MakeNode(serviceId, name, SceneNodeKind.Device, peerId, mine, PeerSubjectPrefix + peerId);
                nodes.Add(node);
                links.Add(new SceneLink($"hosting:{peerId}:{serviceId}", peerId, serviceId, SceneLinkKind.Hosting,
                    node.Health, node.Activity, $"{labels[peerId]} runs {name}"));
            }
        }

        // Found devices (net:{mac}, ts:{id}) carry their own name. A tailnet device that is already a member of the mesh is the
        // same machine as that member, so it is not drawn twice; devices on a home network hang off their router.
        var memberNames = labels.Values.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? Text(List<HistoryPointDto> points, string signal) =>
            points.Where(p => p.SignalId == signal).OrderByDescending(p => p.ObservedAt).FirstOrDefault()?.Value?.Text;
        bool Flag(List<HistoryPointDto> points, string signal) =>
            points.Where(p => p.SignalId == signal).OrderByDescending(p => p.ObservedAt).FirstOrDefault()?.Value?.Boolean == true;
        foreach (var deviceId in devicePoints.Keys.Where(id => id.StartsWith(TailnetPrefix, StringComparison.Ordinal)).ToList())
        {
            if (Text(devicePoints[deviceId], FoundName) is { } tailnetName && memberNames.Contains(tailnetName))
            {
                devicePoints.Remove(deviceId);
            }
        }

        var routers = devicePoints
            .Where(d => d.Key.StartsWith(NetworkPrefix, StringComparison.Ordinal) && Flag(d.Value, "net.device.gateway"))
            .Select(d => (Id: d.Key, Network: Text(d.Value, "net.device.network")))
            .ToList();
        foreach (var (deviceId, points) in devicePoints)
        {
            if (!deviceId.StartsWith(NetworkPrefix, StringComparison.Ordinal) || routers.Any(r => r.Id == deviceId)) continue;
            var network = Text(points, "net.device.network");
            if (routers.FirstOrDefault(r => r.Network is not null && r.Network == network).Id is { } router)
            {
                deviceParent[deviceId] = router;
            }
        }

        // Parents first, so a router is placed before the devices behind it.
        int Depth(string id) => deviceParent.TryGetValue(id, out var parent) && devicePoints.ContainsKey(parent) ? 1 + Depth(parent) : 0;
        foreach (var (deviceId, points) in devicePoints.OrderBy(d => Depth(d.Key)).ThenBy(d => deviceParent[d.Key], StringComparer.Ordinal).ThenBy(d => d.Key, StringComparer.Ordinal))
        {
            var parentId = deviceParent[deviceId];
            // A name the person gave the device in Things wins over what the network calls it.
            var label = thingNames?.GetValueOrDefault(deviceId) ?? Text(points, FoundName) ?? deviceId;
            var node = MakeNode(deviceId, label, SceneNodeKind.Device, parentId, points, deviceId);
            nodes.Add(node);
            links.Add(new SceneLink(
                $"hosting:{parentId}:{deviceId}", parentId, deviceId, SceneLinkKind.Hosting,
                node.Health, node.Activity,
                deviceId.StartsWith(NetworkPrefix, StringComparison.Ordinal)
                    ? $"{node.Label} is on the network of {labels.GetValueOrDefault(parentId, parentId)}"
                    : deviceId.StartsWith(TailnetPrefix, StringComparison.Ordinal)
                        ? $"{node.Label} is on your tailnet, as seen by {labels.GetValueOrDefault(parentId, parentId)}"
                        : $"{labels.GetValueOrDefault(parentId, parentId)} collects {node.Label}"));
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
