using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels.Dive;

/// <summary>
/// The Dive (UI_DESIGN.md, "Decision: Dive with the grid overview"): Home is a grid of device tiles; tapping one dives into the
/// device, which unfolds into its own page (layers as gauges, hosted services as chambers to dive into again, layers as parts).
/// There is no side inspector: details and actions live on the object you are inside. The location is the environment's
/// selection plus an optional part, so live 1 s sampling interest, controls and breadcrumbs follow it, and a selection made
/// anywhere else (search, timeline, outline, breadcrumb) moves the Dive too.
/// <para>Everything shown is derived from the scene; reasons come from <see cref="SceneNode.HealthReason"/>, never invented.</para>
/// </summary>
public sealed partial class DiveViewModel : ObservableObject
{
    /// <summary>From this fill a storage or memory part counts as a fault ("94% full").</summary>
    public const double FullThreshold = 0.9;

    /// <summary>From this fill a gauge is drawn as high.</summary>
    public const double HighThreshold = 0.8;

    /// <summary>The text that says why actions are off while viewing a recorded moment.</summary>
    public const string RecordedNotice = "Recorded view: actions are off. Return to live to act.";

    private static readonly TimeSpan ApprovalRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly EnvironmentViewModel environment;
    private readonly NodeConnection connection;
    private readonly TimeProvider timeProvider;
    private bool navigating;
    private DateTimeOffset lastApprovals = DateTimeOffset.MinValue;
    private IReadOnlyList<DiveApprovalTile> approvals = [];
    private bool trendsSeeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(CanGoUp))]
    private string? nodeId;

    [ObservableProperty]
    private string? layerId;

    [ObservableProperty]
    private object page;

    [ObservableProperty]
    private string backText = string.Empty;

    public DiveViewModel(EnvironmentViewModel environment, ControlsViewModel controls, NodeConnection connection, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(controls);
        this.environment = environment;
        this.connection = connection;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Controls = controls;
        page = Home;
        environment.PropertyChanged += OnEnvironmentChanged;
        controls.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ControlsViewModel.CanAct) or nameof(ControlsViewModel.IsAvailable))
            {
                OnPropertyChanged(nameof(CanAct));
                if (Page is DivePartPage part)
                {
                    part.MuteAction = MuteActionFor();
                }
            }
        };
        controls.Actions.CollectionChanged += (_, _) =>
        {
            if (Page is DivePartPage part)
            {
                part.MuteAction = MuteActionFor();
            }
        };
        Rebuild();
    }

    /// <summary>The selected device's controls (the same instance the inspector uses), so actions reuse its commands.</summary>
    public ControlsViewModel Controls { get; }

    public DiveHomePage Home { get; } = new();

    /// <summary>Home, then each node down to the current one, then the part. The last crumb is where you are.</summary>
    public ObservableCollection<BreadcrumbItem> Crumbs { get; } = [];

    public bool IsHome => NodeId is null;

    public bool CanGoUp => NodeId is not null;

    public bool IsHistorical => environment.IsHistorical;

    /// <summary>"RECORDED · as of 21:14" while viewing history.</summary>
    public string RecordedText => environment.AsOf is { } asOf
        ? "RECORDED · as of " + asOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Actions can be sent: live, a control source, a paired device and nothing in flight.</summary>
    public bool CanAct => !IsHistorical && Controls.CanAct;

    /// <summary>Decisions waiting for this person on paired computers (empty while viewing history). The Alerts home shows them too.</summary>
    public IReadOnlyList<DiveApprovalTile> Approvals => environment.IsHistorical ? [] : approvals;

    /// <summary>Recent CPU and network readings behind the sparklines: seeded from history, appended by each live refresh.</summary>
    public DiveTrendBuffer Trends { get; } = new();

    /// <summary>Completes when the last history read for the sparklines has finished (tests await this).</summary>
    public Task TrendsTask { get; private set; } = Task.CompletedTask;

    /// <summary>Raised after every move, for the zoom animation.</summary>
    public event EventHandler<DiveNavigatedEventArgs>? Navigated;

    /// <summary>Moves to a node (null: Home) and optionally one of its layers as a part. Unknown targets are ignored.</summary>
    public void GoTo(string? targetNodeId, string? targetLayerId = null)
    {
        var scene = environment.Scene;
        SceneNode? node = null;
        if (targetNodeId is not null && (node = scene.Find(targetNodeId)) is null)
        {
            return;
        }

        if (targetLayerId is not null && (node is null || node.Layers.All(l => l.Id != targetLayerId)))
        {
            targetLayerId = null;
        }

        if (targetNodeId == NodeId && targetLayerId == LayerId)
        {
            return;
        }

        var fromNode = NodeId;
        var fromLayer = LayerId;
        var direction = IsWithin(scene, fromNode, fromLayer, targetNodeId, targetLayerId) ? DiveDirection.Out : DiveDirection.In;
        navigating = true;
        try
        {
            NodeId = targetNodeId;
            LayerId = targetLayerId;
            environment.SelectedNodeId = targetNodeId;
        }
        finally
        {
            navigating = false;
        }

        Rebuild();
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(fromNode, fromLayer, direction));
    }

    /// <summary>One level out: part to its device, device to its host, top-level device to Home. False at Home (nothing to climb).</summary>
    public bool Up()
    {
        if (NodeId is null)
        {
            return false;
        }

        if (LayerId is not null)
        {
            GoTo(NodeId);
        }
        else
        {
            var parent = environment.Scene.Find(environment.Scene.Find(NodeId)?.ParentId);
            GoTo(parent?.Id);
        }

        return true;
    }

    [RelayCommand]
    public void Back() => Up();

    [RelayCommand]
    public void GoHome() => GoTo(null);

    [RelayCommand]
    public void Open(DiveTile? tile)
    {
        switch (tile)
        {
            case DiveDeviceTile device:
                GoTo(device.NodeId);
                break;
            case DiveAttentionTile:
                DiveToCause();
                break;
            case DiveApprovalTile approval:
                GoTo(approval.HostNodeId);
                break;
        }
    }

    [RelayCommand]
    public void OpenPart(DiveGauge? gauge)
    {
        if (gauge is not null)
        {
            GoTo(gauge.NodeId, gauge.LayerId);
        }
    }

    [RelayCommand]
    public void OpenChamber(DiveChamber? chamber)
    {
        if (chamber is not null)
        {
            GoTo(chamber.NodeId);
        }
    }

    [RelayCommand]
    public void OpenRelation(DiveRelation? relation)
    {
        if (relation is not null)
        {
            GoTo(relation.NodeId);
        }
    }

    [RelayCommand]
    public void OpenProblem(DiveProblem? problem)
    {
        if (problem is not null)
        {
            GoTo(problem.NodeId, problem.LayerId);
        }
    }

    [RelayCommand]
    public void SelectCrumb(BreadcrumbItem? crumb)
    {
        if (crumb is not null)
        {
            GoTo(crumb.NodeId, crumb.LayerId);
        }
    }

    /// <summary>
    /// Goes to the most likely cause: from Home anywhere, from a device within it. Faults before offline or stale devices, a fault that
    /// does not merely depend on another fault, then the deepest (a part is one deeper than its node).
    /// </summary>
    [RelayCommand]
    public void DiveToCause()
    {
        var cause = FindCause(environment.Scene, LayerId is null ? NodeId : null);
        if (cause is not null)
        {
            GoTo(cause.NodeId, cause.LayerId);
        }
    }

    [RelayCommand]
    public Task ReturnToLiveAsync() => environment.ReturnToNowAsync();

    /// <summary>
    /// Asks every paired computer for decisions waiting for this person (pairing requests, staged updates) for the Home tiles.
    /// Runs on its own every <see cref="ApprovalRefreshInterval"/> while live; never while viewing history.
    /// </summary>
    public async Task RefreshApprovalsAsync(CancellationToken cancellationToken = default)
    {
        lastApprovals = timeProvider.GetUtcNow();
        var found = new List<DiveApprovalTile>();
        if (connection.Source is IControlSource source && !environment.IsHistorical)
        {
            foreach (var peer in environment.Scene.Nodes.Where(n => n.Kind == SceneNodeKind.Peer).ToList())
            {
                try
                {
                    var pairings = await source.ReadPendingPairingsAsync(peer.Id, cancellationToken);
                    foreach (var pairing in pairings.Where(p => p.ApprovalState == "PendingApproval"))
                    {
                        var name = pairing.DisplayName ?? pairing.RemotePeerId;
                        found.Add(new DiveApprovalTile(
                            peer.Id, peer.Label, name, $"asks to join, held by {peer.Label}. Fingerprint {ShortFingerprint(pairing.FingerprintSha256)}",
                            ControlsViewModel.TierText(source.DescribeTier(ControlActionKind.ApprovePairing)), false));
                    }

                    var status = await source.ReadUpdateStatusAsync(peer.Id, cancellationToken);
                    if (status is { Permitted: true, Available: true, StagedVersion: { } version, StagedDigestHex: not null })
                    {
                        found.Add(new DiveApprovalTile(
                            peer.Id, peer.Label, $"Update {version}", $"staged and verified on {peer.Label} (installed {status.InstalledVersion})",
                            ControlsViewModel.TierText(source.DescribeTier(ControlActionKind.ApproveUpdate)), true));
                    }
                }
                catch (LocalApiException)
                {
                    // An unreachable computer has nothing to approve right now.
                }
            }
        }

        approvals = found;
        RebuildHome();
        OnPropertyChanged(nameof(Approvals));
    }

    /// <summary>
    /// Fills the sparkline buffers with the last <see cref="DiveTrendBuffer.Window"/> of real CPU and network-counter history from the node.
    /// Runs once when live data first arrives and again after returning from a recorded view; a failed read leaves the sparklines to grow
    /// from live refreshes alone.
    /// </summary>
    public async Task SeedTrendsAsync(CancellationToken cancellationToken = default)
    {
        trendsSeeded = true;
        if (connection.Source is not { } source || environment.IsHistorical)
        {
            return;
        }

        var end = environment.Scene.AsOf > DateTimeOffset.UnixEpoch ? environment.Scene.AsOf : timeProvider.GetUtcNow();
        foreach (var signal in DiveTrendBuffer.Signals)
        {
            try
            {
                var history = await source.GetHistoryAsync(
                    new HistoryQuery(end - DiveTrendBuffer.Window, end, SignalId: signal), maxPages: 2, ct: cancellationToken);
                Trends.AddHistory(history.Points);
            }
            catch (LocalApiException)
            {
                // Sparklines are optional; the live refreshes still add to them.
            }
        }

        if (!environment.IsHistorical)
        {
            Rebuild();
        }
    }

    /// <summary>Everything that needs attention in the scene, or under <paramref name="withinNodeId"/> (itself included).</summary>
    public static IReadOnlyList<DiveProblem> Problems(SceneGraph scene, string? withinNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var nodes = withinNodeId is null ? scene.Nodes : Subtree(scene, withinNodeId);
        var problems = new List<DiveProblem>();
        foreach (var node in nodes)
        {
            var depth = Depth(scene, node);
            switch (node.Health)
            {
                case SceneHealth.Offline:
                    problems.Add(new DiveProblem(node.Id, null, $"{node.Label} offline since {DiveText.Clock(node.LastSeen)}", SceneHealth.Offline, depth, false));
                    break;
                case SceneHealth.Stale:
                    problems.Add(new DiveProblem(node.Id, null, $"{node.Label} stale, last seen {DiveText.Clock(node.LastSeen)}", SceneHealth.Stale, depth, false));
                    break;
                case SceneHealth.Degraded:
                    problems.Add(new DiveProblem(node.Id, null, $"{node.Label}: {node.HealthReason}", SceneHealth.Degraded, depth, true));
                    break;
            }

            if (node.Health == SceneHealth.Offline)
            {
                continue; // an offline device's last fill is history, not a current fault
            }

            foreach (var layer in node.Layers.Where(IsFull))
            {
                problems.Add(new DiveProblem(
                    node.Id, layer.Id, $"{node.Label} {layer.Title.ToLowerInvariant()} {DiveText.Percent(layer.Fill!.Value)}% full",
                    SceneHealth.Degraded, depth + 1, true));
            }
        }

        return problems;
    }

    /// <summary>The problem to dive to first (see <see cref="DiveToCause"/>); null when nothing needs attention.</summary>
    public static DiveProblem? FindCause(SceneGraph scene, string? withinNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Rank(scene, Problems(scene, withinNodeId)).FirstOrDefault();
    }

    /// <summary>Most likely cause first: faults, then offline, then stale; a fault that depends on another fault later; deeper first; parts before nodes.</summary>
    private static List<DiveProblem> Rank(SceneGraph scene, IReadOnlyList<DiveProblem> problems)
    {
        var faulted = problems.Where(p => p.IsFault).Select(p => p.NodeId).ToHashSet(StringComparer.Ordinal);
        bool DependsOnFault(DiveProblem p) =>
            p.LayerId is null && scene.Links.Any(l =>
                l.Kind == SceneLinkKind.Dependency && l.FromId == p.NodeId && l.ToId != p.NodeId && faulted.Contains(l.ToId));
        return problems
            .OrderBy(p => p.IsFault ? 0 : p.Health == SceneHealth.Offline ? 1 : 2)
            .ThenBy(p => DependsOnFault(p) ? 1 : 0)
            .ThenByDescending(p => p.Depth)
            .ThenBy(p => p.LayerId is null ? 1 : 0)
            .ToList();
    }

    /// <summary>
    /// The Home tiles for a scene: the attention tile (a full row), decisions waiting for approval, then one tile per top-level device by
    /// attention (faults, offline, stale, then the rest in scene order).
    /// </summary>
    /// <param name="trends">Sparkline data; null (as in a recorded view) shows none.</param>
    public static IReadOnlyList<DiveTile> BuildTiles(SceneGraph scene, IReadOnlyList<DiveApprovalTile>? pending = null, DiveTrendBuffer? trends = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var ids = scene.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var devices = scene.Nodes
            .Where(n => n.ParentId is null || !ids.Contains(n.ParentId))
            .Select(n => Tile(scene, n, trends))
            .OrderByDescending(t => t.Attention)
            .ToList();
        var tiles = new List<DiveTile> { Attention(scene) };
        tiles.AddRange(pending ?? []);
        tiles.AddRange(devices);
        return tiles;
    }

    private static DiveAttentionTile Attention(SceneGraph scene)
    {
        var problems = Rank(scene, Problems(scene));
        var faults = problems.Count(p => p.IsFault);
        var offline = problems.Count(p => p.Health == SceneHealth.Offline);
        var stale = problems.Count(p => p.Health == SceneHealth.Stale);
        var parts = new List<string>();
        void Count(int n, string one, string many)
        {
            if (n > 0)
            {
                parts.Add(n == 1 ? "1 " + one : $"{n.ToString(CultureInfo.InvariantCulture)} {many}");
            }
        }

        Count(faults, "fault", "faults");
        Count(offline, "offline", "offline");
        Count(stale, "stale", "stale");
        var headline = problems.Count == 0
            ? "All clear"
            : problems.Count == 1 ? "1 needs attention" : $"{problems.Count.ToString(CultureInfo.InvariantCulture)} need attention";
        return new DiveAttentionTile(
            headline,
            parts.Count == 0 ? "Every device reports in and nothing has failed." : string.Join(" · ", parts),
            problems.Take(3).ToList(),
            problems.FirstOrDefault());
    }

    private static DiveDeviceTile Tile(SceneGraph scene, SceneNode node, DiveTrendBuffer? trends)
    {
        var problems = Problems(scene, node.Id);
        var children = scene.Nodes.Where(c => c.ParentId == node.Id).ToList();
        var untrusted = node.Health is SceneHealth.Offline or SceneHealth.Stale;
        var gauges = untrusted
            ? []
            : node.Layers.Where(l => l.Fill is not null).Take(3).Select(l => Gauge(node, l, null)).ToList();
        var trendList = untrusted || trends is null
            ? []
            : new[] { trends.Cpu(node), trends.Network(node) }.OfType<DiveTrend>().ToList();
        var lines = untrusted
            ? []
            : node.Layers.Where(l => l.Fill is null && l.Kind != SceneLayerKind.Other).Take(2).Select(l => $"{l.Title}: {l.Summary}").ToList();
        string? headline = node.Health switch
        {
            SceneHealth.Offline => "--",
            SceneHealth.Stale => DiveText.Clock(node.LastSeen),
            _ => null,
        };
        var detail = node.Health == SceneHealth.Stale ? node.HealthReason + "; data may be out of date" : node.HealthReason;
        string? childrenText = null;
        if (children.Count > 0)
        {
            var word = children.All(c => c.Kind == SceneNodeKind.Service) ? (children.Count == 1 ? "service" : "services") : "inside";
            var bad = children.Count(c => c.Health is SceneHealth.Degraded or SceneHealth.Offline);
            childrenText = $"{children.Count.ToString(CultureInfo.InvariantCulture)} {word}"
                + (bad > 0 ? $" · {bad.ToString(CultureInfo.InvariantCulture)} need attention" : " · all OK");
        }

        var attention = problems.Any(p => p.IsFault) ? 4
            : problems.Any(p => p.Health == SceneHealth.Offline) ? 3
            : problems.Any(p => p.Health == SceneHealth.Stale) ? 2
            : node.Health == SceneHealth.Unknown ? 1 : 0;
        return new DiveDeviceTile(
            node.Id, node.Label, InspectorViewModel.KindText(node.Kind), BadgeFor(scene, node, problems), gauges, lines, headline, node.Health,
            detail, node.Health == SceneHealth.Healthy ? Math.Clamp(node.Activity, 0, 1) : 0, childrenText, attention, trendList);
    }

    /// <summary>The node's status in words: its own health first, then the worst thing inside it, then transfer activity.</summary>
    private static DiveBadge BadgeFor(SceneGraph scene, SceneNode node, IReadOnlyList<DiveProblem> problems)
    {
        switch (node.Health)
        {
            case SceneHealth.Offline:
                return new DiveBadge($"Offline since {DiveText.Clock(node.LastSeen)}", SceneHealth.Offline);
            case SceneHealth.Stale:
                return new DiveBadge($"Stale · last seen {DiveText.Clock(node.LastSeen)}", SceneHealth.Stale);
            case SceneHealth.Degraded:
                return new DiveBadge("Degraded", SceneHealth.Degraded);
            case SceneHealth.Unknown:
                return new DiveBadge("No data yet", SceneHealth.Unknown);
        }

        var inner = problems.Where(p => p.IsFault).ToList();
        var anyFull = inner.Any(p => p.LayerId is not null);
        if (inner.Count > 1)
        {
            return new DiveBadge($"{inner.Count.ToString(CultureInfo.InvariantCulture)} problems inside", SceneHealth.Degraded, IsFault: anyFull);
        }

        if (inner.Count == 1)
        {
            var p = inner[0];
            var text = p.LayerId is { } layerId && node.Layers.FirstOrDefault(l => l.Id == layerId) is { Fill: { } fill } layer
                ? $"{DiveText.ShortLabel(layer)} {DiveText.Percent(fill)}% full"
                : $"{scene.Find(p.NodeId)?.Label ?? p.NodeId} degraded";
            return new DiveBadge(text, SceneHealth.Degraded, IsFault: anyFull);
        }

        if (problems.Any(p => p.Health is SceneHealth.Offline or SceneHealth.Stale))
        {
            return new DiveBadge("Something inside is not reporting", SceneHealth.Stale);
        }

        return node.Activity >= 0.05
            ? new DiveBadge("Transferring", SceneHealth.Healthy, IsTransfer: true)
            : new DiveBadge("Online", SceneHealth.Healthy);
    }

    /// <summary>A layer as a gauge (ring when it has a proportion), with its sparkline when <paramref name="trends"/> has one. Shared with Alerts.</summary>
    internal static DiveGauge Gauge(SceneNode node, SceneLayer layer, DiveTrendBuffer? trends)
    {
        var trend = trends is null || node.Health is SceneHealth.Offline or SceneHealth.Stale
            ? null
            : layer.Kind switch
            {
                SceneLayerKind.Compute => trends.Cpu(node),
                SceneLayerKind.Network => trends.Network(node),
                _ => null,
            };
        if (layer.Fill is { } fill)
        {
            var level = fill >= FullThreshold ? DiveLevel.Critical : fill >= HighThreshold ? DiveLevel.High : DiveLevel.Normal;
            return new DiveGauge(node.Id, layer.Id, DiveText.ShortLabel(layer), DiveText.Percent(fill), "%", fill, level, layer.Summary, trend);
        }

        var first = layer.Readings.Count > 0 ? layer.Readings[0] : null;
        return new DiveGauge(
            node.Id, layer.Id, DiveText.ShortLabel(layer), first?.DisplayValue ?? "--", string.Empty, null, DiveLevel.None, layer.Summary, trend);
    }

    private static bool IsFull(SceneLayer layer) =>
        layer.Kind is SceneLayerKind.Storage or SceneLayerKind.Memory && layer.Fill is >= FullThreshold;

    private static List<SceneNode> Subtree(SceneGraph scene, string rootId)
    {
        var result = new List<SceneNode>();
        var queue = new Queue<string>([rootId]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || scene.Find(id) is not { } node)
            {
                continue;
            }

            result.Add(node);
            foreach (var child in scene.Nodes.Where(c => c.ParentId == id))
            {
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }

    private static int Depth(SceneGraph scene, SceneNode node)
    {
        var depth = 0;
        var current = scene.Find(node.ParentId);
        while (current is not null && depth < 16)
        {
            depth++;
            current = scene.Find(current.ParentId);
        }

        return depth;
    }

    /// <summary>True when (toNode, toLayer) is the current place or one of its ancestors: moving there climbs out.</summary>
    private static bool IsWithin(SceneGraph scene, string? fromNode, string? fromLayer, string? toNode, string? toLayer)
    {
        if (toNode is null)
        {
            return true;
        }

        if (toLayer is not null)
        {
            return false;
        }

        if (fromLayer is not null && fromNode == toNode)
        {
            return true;
        }

        var current = scene.Find(fromNode);
        var guard = 0;
        while (current is not null && guard++ < 16)
        {
            if (current.ParentId == toNode)
            {
                return true;
            }

            current = scene.Find(current.ParentId);
        }

        return false;
    }

    private static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length <= 16 ? fingerprint : fingerprint[..16] + "...";

    private void OnEnvironmentChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(EnvironmentViewModel.SelectedNodeId):
                if (!navigating && environment.SelectedNodeId != NodeId)
                {
                    GoTo(environment.SelectedNodeId);
                }

                break;
            case nameof(EnvironmentViewModel.Scene):
                if (!environment.IsHistorical && !environment.Scene.IsHistorical)
                {
                    Trends.AddScene(environment.Scene);
                    if (!trendsSeeded && connection.Source is not null && environment.Scene.Nodes.Count > 0)
                    {
                        TrendsTask = SeedTrendsAsync();
                    }
                }

                Rebuild();
                if (!environment.IsHistorical && timeProvider.GetUtcNow() - lastApprovals > ApprovalRefreshInterval
                    && connection.Source is IControlSource)
                {
                    _ = RefreshApprovalsAsync();
                }

                break;
            case nameof(EnvironmentViewModel.AsOf):
                OnPropertyChanged(nameof(IsHistorical));
                OnPropertyChanged(nameof(RecordedText));
                OnPropertyChanged(nameof(CanAct));
                if (environment.IsHistorical)
                {
                    approvals = []; // decisions are made live; a recorded view never offers them
                }
                else
                {
                    trendsSeeded = false; // fill the gap left while viewing history on the next live scene
                }

                Rebuild();
                OnPropertyChanged(nameof(Approvals));
                break;
        }
    }

    private ControlActionRow? MuteActionFor() =>
        Controls.IsAvailable ? Controls.Actions.FirstOrDefault(a => a.Kind == ControlActionKind.MuteAlerts) : null;

    private void Rebuild()
    {
        var scene = environment.Scene;
        RebuildHome();
        var node = scene.Find(NodeId);
        if (node is null)
        {
            Page = Home;
        }
        else if (LayerId is { } layerId && node.Layers.FirstOrDefault(l => l.Id == layerId) is { } layer)
        {
            var part = Page as DivePartPage is { } current && current.NodeId == node.Id && current.LayerId == layerId
                ? current
                : new DivePartPage(node.Id, layerId, Controls);
            FillPart(part, scene, node, layer);
            Page = part;
        }
        else
        {
            var device = Page as DiveDevicePage is { } current && current.NodeId == node.Id ? current : new DiveDevicePage(node.Id, Controls);
            FillDevice(device, scene, node, LiveTrends);
            Page = device;
        }

        RebuildCrumbs(scene, node);
    }

    private void RebuildHome()
    {
        var scene = environment.Scene;
        Home.Summary = EnvironmentViewModel.Describe(scene);
        Sync(Home.Tiles, BuildTiles(scene, environment.IsHistorical ? [] : approvals, LiveTrends));
    }

    private void RebuildCrumbs(SceneGraph scene, SceneNode? node)
    {
        var chain = new List<SceneNode>();
        var current = node;
        while (current is not null && chain.Count < 16 && !chain.Contains(current))
        {
            chain.Insert(0, current);
            current = scene.Find(current.ParentId);
        }

        var crumbs = new List<BreadcrumbItem> { new("Home", null) };
        crumbs.AddRange(chain.Select(n => new BreadcrumbItem(n.Label, n.Id, true)));
        if (node is not null && LayerId is { } layerId && node.Layers.FirstOrDefault(l => l.Id == layerId) is { } layer)
        {
            crumbs.Add(new BreadcrumbItem(layer.Title, node.Id, true, layer.Id));
        }

        Sync(Crumbs, crumbs);
        BackText = crumbs.Count >= 2 ? crumbs[^2].Text : string.Empty;
        OnPropertyChanged(nameof(Crumbs));
    }

    private DiveTrendBuffer? LiveTrends => environment.IsHistorical ? null : Trends;

    private static void FillDevice(DiveDevicePage device, SceneGraph scene, SceneNode node, DiveTrendBuffer? trends)
    {
        var problems = Problems(scene, node.Id);
        device.Title = node.Label;
        device.KindText = InspectorViewModel.KindText(node.Kind);
        device.Badge = BadgeFor(scene, node, problems);
        device.Provenance = InspectorViewModel.DescribeProvenance(node, scene);
        var cause = FindCause(scene, node.Id);
        device.Problem = cause;
        DiveProblem? lead = null;
        if (cause is not null && cause.NodeId == node.Id && cause.LayerId is null && scene.Find(node.ParentId) is { } hostNode)
        {
            var mine = Subtree(scene, node.Id).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            lead = Problems(scene, hostNode.Id)
                .Where(p => p.IsFault && !mine.Contains(p.NodeId)
                    && !scene.Links.Any(l => l.Kind == SceneLinkKind.Dependency && l.FromId == p.NodeId && mine.Contains(l.ToId)))
                .OrderBy(p => p.LayerId is null ? 1 : 0)
                .ThenBy(p => p.NodeId == hostNode.Id ? 0 : 1)
                .FirstOrDefault();
        }

        device.Investigate = cause is null ? null : cause.NodeId == node.Id && cause.LayerId is null ? lead : cause;
        device.LeadText = lead is not null && ReferenceEquals(device.Investigate, lead)
            ? $"Also on {scene.Find(node.ParentId)?.Label}: {lead.Text}"
            : string.Empty;
        device.ProblemBadgeText = cause is null
            ? string.Empty
            : cause.LayerId is not null ? "Part full"
            : cause.Health == SceneHealth.Degraded ? $"{scene.Find(cause.NodeId)?.Label ?? cause.NodeId} degraded"
            : DiveText.HealthWord(cause.Health);
        Sync(device.Gauges, node.Layers.Select(l => Gauge(node, l, trends)).ToList());
        device.HasGauges = device.Gauges.Count > 0;
        var children = scene.Nodes.Where(c => c.ParentId == node.Id).ToList();
        var causeNode = cause?.NodeId;
        Sync(device.Chambers, children.Select(c =>
        {
            var inner = Problems(scene, c.Id);
            var hint = causeNode is not null && Subtree(scene, c.Id).Any(n => n.Id == causeNode) ? "Cause inside" : "Open chamber";
            return new DiveChamber(c.Id, c.Label, InspectorViewModel.KindText(c.Kind), BadgeFor(scene, c, inner), c.HealthReason, hint);
        }).ToList());
        device.HasChambers = device.Chambers.Count > 0;
        var relations = new List<DiveRelation>();
        if (scene.Find(node.ParentId) is { } host)
        {
            relations.Add(new DiveRelation(host.Id, $"Hosted on {host.Label}", host.Health));
        }

        foreach (var link in scene.Links.Where(l => l.Kind != SceneLinkKind.Hosting && (l.FromId == node.Id || l.ToId == node.Id)))
        {
            var outgoing = link.FromId == node.Id;
            if (scene.Find(outgoing ? link.ToId : link.FromId) is not { } other)
            {
                continue;
            }

            var verb = link.Kind == SceneLinkKind.Dependency
                ? (outgoing ? "Depends on" : "Needed by")
                : "Replicates with";
            relations.Add(new DiveRelation(other.Id, $"{verb} {other.Label}", other.Health));
        }

        Sync(device.Relations, relations);
        device.HasRelations = relations.Count > 0;
    }

    private void FillPart(DivePartPage part, SceneGraph scene, SceneNode node, SceneLayer layer)
    {
        var gauge = Gauge(node, layer, LiveTrends);
        part.Title = layer.Title;
        part.ParentTitle = node.Label;
        part.Context = $"{layer.Summary}, inside {node.Label}";
        part.Gauge = gauge;
        part.Summary = layer.Summary;
        part.IsCritical = gauge.Level == DiveLevel.Critical;
        part.Badge = layer.Fill is { } fill
            ? new DiveBadge($"{DiveText.Percent(fill)}% full", gauge.Level == DiveLevel.Critical ? SceneHealth.Degraded : layer.Health,
                IsFault: gauge.Level == DiveLevel.Critical)
            : new DiveBadge(DiveText.HealthWord(layer.Health), layer.Health);
        part.Statement = layer.Fill is { } f
            ? IsFull(layer)
                ? $"{layer.Title} on {node.Label} is {DiveText.Percent(f)}% full ({layer.Summary}). Anything on this device that writes here can fail until space is freed."
                : $"{layer.Title} on {node.Label} is {DiveText.Percent(f)}% full ({layer.Summary})."
            : $"{layer.Title} on {node.Label}: {layer.Summary}.";
        var related = Problems(scene, node.Id).Where(p => !(p.NodeId == node.Id && p.LayerId == layer.Id)).ToList();
        Sync(part.Related, related);
        part.HasRelated = related.Count > 0;
        part.RelatedHeading = related.Count > 0 ? $"Also reported on {node.Label}" : string.Empty;
        Sync(part.Readings, layer.Readings.Select(r => new DiveReadingRow(
            r.Label,
            r.DisplayValue,
            $"observed {r.ObservedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {r.Quality} · {r.Freshness} · {r.Source}"))
            .ToList());
        part.MuteAction = MuteActionFor();
    }

    /// <summary>Updates a bound collection with as few changes as possible, so open pages keep their place while data refreshes.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], items[i]))
                {
                    target[i] = items[i];
                }
            }
            else
            {
                target.Add(items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
