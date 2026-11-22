using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>One exact reading: value with unit, when it was observed, and how far to trust it.</summary>
public sealed record InspectorReadingRow(
    string Label,
    string Value,
    string Time,
    string Quality,
    string Freshness,
    string Source,
    bool IsFocused)
{
    public string AccessibleText => $"{Label}: {Value}. Observed {Time}. Quality {Quality}. Freshness {Freshness}.";
}

/// <summary>One internal layer of the selected object, with a fill meter only when a true proportion exists.</summary>
public sealed record InspectorLayerRow(
    string Id,
    string Title,
    string Summary,
    SceneHealth Health,
    string HealthText,
    double Fill,
    bool HasFill,
    string FillText,
    bool IsFocused,
    IReadOnlyList<InspectorReadingRow> Readings)
{
    public string AccessibleText => HasFill
        ? $"{Title}: {Summary}. {HealthText}. {FillText} full."
        : $"{Title}: {Summary}. {HealthText}.";
}

/// <summary>A link to or from the selected object; selecting it moves the selection to the other end.</summary>
public sealed record RelationshipRow(
    string OtherNodeId,
    string OtherName,
    string Direction,
    string KindText,
    string Description,
    SceneHealth Health,
    string HealthText)
{
    public string AccessibleText => $"{Direction} {OtherName}: {KindText}. {HealthText}. {Description}";
}

/// <summary>
/// The contextual inspector (UI spec section 9) for the selected scene node: identity and provenance, one section per
/// layer with exact readings, relationships, and, separated from them, the controls.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly EnvironmentViewModel environment;
    private string? focusLayerId;
    private string? focusSignalId;

    [ObservableProperty]
    private SceneNode? node;

    [ObservableProperty]
    private string title = "Nothing selected";

    [ObservableProperty]
    private string typeText = string.Empty;

    [ObservableProperty]
    private SceneHealth health;

    [ObservableProperty]
    private string healthText = string.Empty;

    [ObservableProperty]
    private string healthReason = string.Empty;

    [ObservableProperty]
    private string provenance = string.Empty;

    public InspectorViewModel(EnvironmentViewModel environment, NodeConnection connection)
    {
        this.environment = environment;
        Controls = new ControlsViewModel(connection, environment);
        environment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EnvironmentViewModel.SelectedNode) or nameof(EnvironmentViewModel.Scene))
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    public ControlsViewModel Controls { get; }

    public ObservableCollection<InspectorLayerRow> Layers { get; } = [];

    public ObservableCollection<RelationshipRow> Relationships { get; } = [];

    public bool HasSelection => Node is not null;

    public bool HasLayers => Layers.Count > 0;

    public bool HasRelationships => Relationships.Count > 0;

    /// <summary>Asks the inspector to emphasise a layer and, optionally, one reading (used when search jumps to a metric).</summary>
    public void Focus(string? layerId, string? signalId)
    {
        focusLayerId = layerId;
        focusSignalId = signalId;
        Rebuild();
    }

    [RelayCommand]
    public void SelectRelated(RelationshipRow? row)
    {
        if (row is not null)
        {
            focusLayerId = null;
            focusSignalId = null;
            environment.SelectedNodeId = row.OtherNodeId;
        }
    }

    [RelayCommand]
    public void Close() => environment.SelectedNodeId = null;

    public static string KindText(SceneNodeKind kind) => kind switch
    {
        SceneNodeKind.LocalPeer => "This device",
        SceneNodeKind.Peer => "Paired device",
        SceneNodeKind.Device => "Observed device",
        _ => "Service",
    };

    /// <summary>"4 s ago", "38 min ago", "2 h ago", "3 d ago".</summary>
    public static string Ago(TimeSpan age)
    {
        var seconds = Math.Max(0, age.TotalSeconds);
        return seconds switch
        {
            < 90 => $"{seconds.ToString("0", CultureInfo.InvariantCulture)} s ago",
            < 5400 => $"{(seconds / 60).ToString("0", CultureInfo.InvariantCulture)} min ago",
            < 129600 => $"{(seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture)} h ago",
            _ => $"{(seconds / 86400).ToString("0.#", CultureInfo.InvariantCulture)} d ago",
        };
    }

    private void Rebuild()
    {
        var selected = environment.SelectedNode;
        var scene = environment.Scene;
        Layers.Clear();
        Relationships.Clear();
        Node = selected;
        if (selected is null)
        {
            Title = "Nothing selected";
            TypeText = string.Empty;
            Health = SceneHealth.Unknown;
            HealthText = string.Empty;
            HealthReason = string.Empty;
            Provenance = string.Empty;
        }
        else
        {
            Title = selected.Label;
            TypeText = KindText(selected.Kind);
            Health = selected.Health;
            HealthText = selected.Health.ToString();
            HealthReason = selected.HealthReason;
            Provenance = DescribeProvenance(selected, scene);
            foreach (var layer in selected.Layers)
            {
                Layers.Add(BuildLayer(layer));
            }

            foreach (var link in scene.Links)
            {
                var outgoing = link.FromId == selected.Id;
                if (!outgoing && link.ToId != selected.Id)
                {
                    continue;
                }

                var other = scene.Find(outgoing ? link.ToId : link.FromId);
                if (other is null)
                {
                    continue;
                }

                Relationships.Add(new RelationshipRow(
                    other.Id, other.Label, outgoing ? "To" : "From", LinkKindText(link.Kind),
                    link.Description, link.Health, link.Health.ToString()));
            }
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(HasRelationships));
        Controls.Update(selected);
    }

    private InspectorLayerRow BuildLayer(SceneLayer layer)
    {
        var focusedLayer = layer.Id == focusLayerId;
        var readings = layer.Readings.Select(r => new InspectorReadingRow(
            r.Label,
            r.DisplayValue,
            r.ObservedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            r.Quality,
            r.Freshness,
            r.Source,
            focusedLayer && r.SignalId == focusSignalId)).ToList();
        var fill = layer.Fill;
        return new InspectorLayerRow(
            layer.Id, layer.Title, layer.Summary, layer.Health, layer.Health.ToString(),
            fill is { } f ? Math.Clamp(f, 0, 1) : 0,
            fill is not null,
            fill is { } p ? (Math.Clamp(p, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : string.Empty,
            focusedLayer,
            readings);
    }

    private static string LinkKindText(SceneLinkKind kind) => kind switch
    {
        SceneLinkKind.Replication => "Replication",
        SceneLinkKind.Hosting => "Hosting",
        _ => "Dependency",
    };

    /// <summary>Spec 20: say who observed it and how old it is; never imply the reading is newer than it is.</summary>
    private static string DescribeProvenance(SceneNode node, SceneGraph scene)
    {
        var origins = node.Layers.SelectMany(l => l.Readings).Select(r => r.OriginPeerId)
            .GroupBy(id => id, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        string who;
        if (origins is null)
        {
            who = "No readings";
        }
        else if (origins == node.Id)
        {
            who = node.Kind == SceneNodeKind.LocalPeer ? "Observed on this device" : "Reported by the device itself";
        }
        else
        {
            who = $"Observed by {scene.Find(origins)?.Label ?? origins}";
        }

        if (node.LastSeen is not { } seen)
        {
            return $"{who}; never seen";
        }

        var age = Ago(scene.AsOf - seen);
        return scene.IsHistorical ? $"{who}, {age} (recorded)" : $"{who}, {age}";
    }
}
