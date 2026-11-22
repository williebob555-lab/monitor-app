using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels.Dive;

/// <summary>Home: the grid overview of devices, with the attention and pending-approval tiles.</summary>
public sealed partial class DiveHomePage : ObservableObject
{
    [ObservableProperty]
    private string summary = string.Empty;

    public ObservableCollection<DiveTile> Tiles { get; } = [];
}

/// <summary>
/// Inside a device or service: its header and provenance, the problem it reports (with Investigate), its layers as gauges,
/// its controls, the chambers (child nodes) you can dive into, and its relationships. Updated in place while it stays open.
/// </summary>
public sealed partial class DiveDevicePage(string nodeId, ControlsViewModel controls) : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string kindText = string.Empty;

    [ObservableProperty]
    private DiveBadge badge = new(string.Empty, SceneHealth.Unknown);

    [ObservableProperty]
    private string provenance = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private DiveProblem? problem;

    [ObservableProperty]
    private string problemBadgeText = string.Empty;

    /// <summary>Where Investigate goes: the cause inside this object, or, when the problem is this object itself, a fault on its host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInvestigate))]
    private DiveProblem? investigate;

    /// <summary>"Also on Home server: Home server storage 94% full" when Investigate leads to the host (said as a fact, not as a cause).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLead))]
    private string leadText = string.Empty;

    [ObservableProperty]
    private bool hasChambers;

    [ObservableProperty]
    private bool hasRelations;

    [ObservableProperty]
    private bool hasGauges;

    public string NodeId { get; } = nodeId;

    public ControlsViewModel Controls { get; } = controls;

    public bool HasProblem => Problem is not null;

    public bool HasInvestigate => Investigate is not null;

    public bool HasLead => LeadText.Length > 0;

    public ObservableCollection<DiveGauge> Gauges { get; } = [];

    public ObservableCollection<DiveChamber> Chambers { get; } = [];

    public ObservableCollection<DiveRelation> Relations { get; } = [];
}

/// <summary>
/// A part: one layer of a node (a drive's storage, memory, battery). Shows its fill, the exact readings, and why it matters:
/// the fill stated plainly and the other problems reported on the same device, never an invented cause.
/// </summary>
public sealed partial class DivePartPage(string nodeId, string layerId, ControlsViewModel controls) : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string context = string.Empty;

    [ObservableProperty]
    private string parentTitle = string.Empty;

    [ObservableProperty]
    private DiveBadge badge = new(string.Empty, SceneHealth.Unknown);

    [ObservableProperty]
    private DiveGauge? gauge;

    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    private string statement = string.Empty;

    [ObservableProperty]
    private string relatedHeading = string.Empty;

    [ObservableProperty]
    private bool hasRelated;

    [ObservableProperty]
    private bool isCritical;

    [ObservableProperty]
    private ControlActionRow? muteAction;

    public string NodeId { get; } = nodeId;

    public string LayerId { get; } = layerId;

    public ControlsViewModel Controls { get; } = controls;

    public string BackText => "Back to " + ParentTitle;

    public bool HasMuteAction => MuteAction is not null;

    public ObservableCollection<DiveReadingRow> Readings { get; } = [];

    public ObservableCollection<DiveProblem> Related { get; } = [];

    partial void OnParentTitleChanged(string value) => OnPropertyChanged(nameof(BackText));

    partial void OnMuteActionChanged(ControlActionRow? value) => OnPropertyChanged(nameof(HasMuteAction));
}
