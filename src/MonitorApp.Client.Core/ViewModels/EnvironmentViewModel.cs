using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>One entry of the accessible outline: a node, its health in words, and the devices or services under it.</summary>
public sealed record OutlineItem(
    string NodeId,
    string Name,
    string HealthText,
    string AccessibleText,
    IReadOnlyList<OutlineItem> Children,
    SceneHealth Health = SceneHealth.Unknown);

/// <summary>
/// The living schematic's view model: projects peer data into a <see cref="SceneGraph"/> and keeps selection,
/// lens, time travel, pinned positions and a text outline that mirrors the scene for keyboard and screen-reader use.
/// </summary>
public sealed partial class EnvironmentViewModel : SectionViewModel
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(2);

    private readonly StableLayout layout;
    private readonly TimeProvider timeProvider;

    [ObservableProperty]
    private SceneGraph scene = SceneGraph.Empty;

    [ObservableProperty]
    private string? selectedNodeId;

    [ObservableProperty]
    private SceneNode? selectedNode;

    [ObservableProperty]
    private SceneLens lens = SceneLens.Health;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistorical))]
    private DateTimeOffset? asOf;

    [ObservableProperty]
    private string summary = "Not loaded";

    [ObservableProperty]
    private string updateMode = "Manual refresh";

    public EnvironmentViewModel(NodeConnection connection, ILayoutStore? layoutStore = null, TimeProvider? timeProvider = null)
        : base(connection, "Environment")
    {
        layout = new StableLayout(layoutStore);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True while viewing a chosen past time instead of the live state.</summary>
    public bool IsHistorical => AsOf is not null;

    public ObservableCollection<OutlineItem> Outline { get; } = [];

    partial void OnSelectedNodeIdChanged(string? value) => SelectedNode = Scene.Find(value);

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var requestedAsOf = AsOf;
            var now = timeProvider.GetUtcNow();
            var runtime = await Client.GetRuntimeAsync(cancellationToken);
            var peers = await Client.GetPeersAsync(cancellationToken);
            var state = await Client.GetStateAsync(requestedAsOf, ct: cancellationToken);
            var rates = await LoadRatesAsync(requestedAsOf ?? now, cancellationToken);
            var graph = SceneProjector.Project(runtime, peers, state, rates, now, requestedAsOf is not null);
            Apply(graph with { Layout = layout.Place(graph.Nodes) });
        });
    }

    /// <summary>
    /// Shows a ready-made graph (demo mode, tests, screenshots) exactly as a refresh would: summary, outline and
    /// selection are rebuilt from it. Nothing is read from the node.
    /// </summary>
    public void ShowScene(SceneGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Apply(graph);
    }

    /// <summary>Shows the environment as it was at <paramref name="time"/>.</summary>
    public async Task GoToAsync(DateTimeOffset time, CancellationToken cancellationToken = default)
    {
        AsOf = time;
        await RefreshAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task ReturnToNowAsync(CancellationToken cancellationToken = default)
    {
        AsOf = null;
        await RefreshAsync(cancellationToken);
    }

    [RelayCommand]
    public void SelectNext() => Step(1);

    [RelayCommand]
    public void SelectPrevious() => Step(-1);

    /// <summary>Remembers a position the user dragged a node to.</summary>
    [RelayCommand]
    public void PinNode(SceneNodeMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        layout.Pin(moved.NodeId, moved.Position);
        var updated = new Dictionary<string, ScenePoint>(Scene.Layout, StringComparer.Ordinal) { [moved.NodeId] = moved.Position };
        Scene = Scene with { Layout = updated };
    }

    /// <summary>Keeps the scene current until cancelled: re-reads on push when supported, otherwise polls.</summary>
    public async Task RunLiveAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var source = Client;
        try
        {
            if (source.SupportsLiveUpdates)
            {
                var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
                void OnChanged(object? sender, EventArgs args) => signal.Writer.TryWrite(true);
                source.StateChanged += OnChanged;
                try
                {
                    UpdateMode = "Live";
                    await RefreshAsync(cancellationToken);
                    while (await signal.Reader.WaitToReadAsync(cancellationToken))
                    {
                        signal.Reader.TryRead(out _);
                        await RefreshAsync(cancellationToken);
                    }
                }
                finally
                {
                    source.StateChanged -= OnChanged;
                }
            }
            else
            {
                UpdateMode = $"Polling every {pollInterval.TotalSeconds:0.#}s";
                await RefreshAsync(cancellationToken);
                using var timer = new PeriodicTimer(pollInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await RefreshAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop when the view is hidden or the window closes.
        }
        finally
        {
            UpdateMode = "Manual refresh";
        }
    }

    /// <summary>"5 devices · 1 stale · 1 offline"; only non-zero problem counts are listed. Services are not devices.</summary>
    public static string Describe(SceneGraph graph)
    {
        var devices = graph.Nodes.Where(n => n.Kind != SceneNodeKind.Service).ToList();
        if (devices.Count == 0)
        {
            return "No devices";
        }

        var parts = new List<string> { devices.Count == 1 ? "1 device" : $"{devices.Count} devices" };
        foreach (var (health, word) in new[]
        {
            (SceneHealth.Stale, "stale"), (SceneHealth.Offline, "offline"), (SceneHealth.Degraded, "degraded"), (SceneHealth.Unknown, "unknown"),
        })
        {
            var count = graph.Nodes.Count(n => n.Health == health);
            if (count > 0)
            {
                parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {word}");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Builds the outline for a graph: top-level nodes in scene order, children under their parent.</summary>
    public static IReadOnlyList<OutlineItem> BuildOutline(SceneGraph graph)
    {
        var ids = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        OutlineItem Item(SceneNode node) => new(
            node.Id,
            node.Label,
            $"{node.Health}: {node.HealthReason}",
            $"{node.Label}, {KindWord(node.Kind)}. {node.Health}: {node.HealthReason}",
            graph.Nodes.Where(c => c.ParentId == node.Id).Select(Item).ToList(),
            node.Health);
        return graph.Nodes.Where(n => n.ParentId is null || !ids.Contains(n.ParentId)).Select(Item).ToList();
    }

    private static string KindWord(SceneNodeKind kind) => kind switch
    {
        SceneNodeKind.LocalPeer => "this device",
        SceneNodeKind.Peer => "paired device",
        SceneNodeKind.Device => "observed device",
        _ => "service",
    };

    private async Task<NetworkRates> LoadRatesAsync(DateTimeOffset end, CancellationToken cancellationToken)
    {
        try
        {
            var points = new List<HistoryPointDto>();
            foreach (var signal in new[] { SignalIds.NetworkBytesSent, SignalIds.NetworkBytesReceived })
            {
                var history = await Client.GetHistoryAsync(
                    new HistoryQuery(end - RateWindow, end, SignalId: signal), maxPages: 2, ct: cancellationToken);
                points.AddRange(history.Points);
            }

            return NetworkRates.FromHistory(points);
        }
        catch (LocalApiException)
        {
            // Activity is decoration; a failed history read must not hide the environment itself.
            return NetworkRates.None;
        }
    }

    private void Apply(SceneGraph graph)
    {
        Scene = graph;
        Summary = Describe(graph);
        Outline.Clear();
        foreach (var item in BuildOutline(graph))
        {
            Outline.Add(item);
        }

        if (SelectedNodeId is not null && graph.Find(SelectedNodeId) is null)
        {
            SelectedNodeId = null;
        }

        SelectedNode = graph.Find(SelectedNodeId);
    }

    private void Step(int direction)
    {
        var order = new List<string>();
        void Walk(IEnumerable<OutlineItem> items)
        {
            foreach (var item in items)
            {
                order.Add(item.NodeId);
                Walk(item.Children);
            }
        }

        Walk(Outline);
        if (order.Count == 0)
        {
            return;
        }

        var index = SelectedNodeId is null ? -1 : order.IndexOf(SelectedNodeId);
        var next = index < 0
            ? (direction > 0 ? 0 : order.Count - 1)
            : (index + direction + order.Count) % order.Count;
        SelectedNodeId = order[next];
    }
}
