using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>One step of the breadcrumb trail; <see cref="NodeId"/> is set when it can be selected.</summary>
public sealed record BreadcrumbItem(string Text, string? NodeId, bool ShowSeparator = false);

/// <summary>
/// State of the workspace shell (UI spec section 3): the active mode, panel visibility for the current layout class,
/// breadcrumbs, the LIVE / RECORDED pill, and the search, inspector and timeline view models. The views bind only to this
/// and to the section view models; the shell holds no data of its own.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private bool syncing;

    [ObservableProperty]
    private ShellMode mode = ShellMode.Ecosystem;

    [ObservableProperty]
    private ShellModeItem selectedModeItem;

    [ObservableProperty]
    private SectionViewModel? selectedSubSection;

    [ObservableProperty]
    private bool railExpanded;

    [ObservableProperty]
    private bool inspectorCollapsed;

    [ObservableProperty]
    private bool timelineOpen = true;

    [ObservableProperty]
    private ShellLayoutClass layoutClass = ShellLayoutClass.Wide;

    [ObservableProperty]
    private bool searchDropdownOpen;

    public ShellViewModel(MainViewModel main, NodeConnection connection, EnvironmentViewModel environment)
    {
        this.main = main;
        Environment = environment;
        Modes = ModesFor();
        selectedModeItem = Modes[0];
        Search = new SearchViewModel(environment);
        Inspector = new InspectorViewModel(environment, connection);
        Timeline = new TimelineViewModel(environment);

        environment.PropertyChanged += OnEnvironmentChanged;
        main.PropertyChanged += OnMainChanged;
        Search.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SearchViewModel.Query))
            {
                SearchDropdownOpen = Search.HasQuery && Mode != ShellMode.Search;
            }
        };
        Search.Jumped += (_, result) =>
        {
            SearchDropdownOpen = false;
            Inspector.Focus(result.LayerId, result.SignalId);
            Mode = ShellMode.Ecosystem;
            // A jump to the already selected node raises no selection change; make sure the inspector shows.
            InspectorCollapsed = false;
            RaiseVisibility();
        };
        RebuildSubSections();
        RebuildBreadcrumbs();
    }

    public EnvironmentViewModel Environment { get; }

    public SearchViewModel Search { get; }

    public InspectorViewModel Inspector { get; }

    public TimelineViewModel Timeline { get; }

    /// <summary>For the connection panel (address, status) shown with Settings.</summary>
    public MainViewModel Main => main;

    /// <summary>The modes that exist today, in rail order. See <see cref="ShellMode"/> for adding one.</summary>
    public IReadOnlyList<ShellModeItem> Modes { get; }

    public ObservableCollection<SectionViewModel> SubSections { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public bool HasSubSections => SubSections.Count > 0;

    public bool IsHistorical => Environment.IsHistorical;

    public bool IsLive => !Environment.IsHistorical;

    /// <summary>"LIVE" or "RECORDED hh:mm" (local time of the examined moment).</summary>
    public string LiveText => Environment.AsOf is { } asOf
        ? "RECORDED " + asOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : "LIVE";

    public string LiveAccessibleText => Environment.AsOf is { } asOf
        ? "Showing recorded state from " + asOf.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ". Not live."
        : "Showing live state.";

    public string ToolbarTitle => Breadcrumbs.Count == 0 ? Modes[0].Title : Breadcrumbs[^1].Text;

    public bool IsNarrow => LayoutClass == ShellLayoutClass.Narrow;

    public bool IsMedium => LayoutClass == ShellLayoutClass.Medium;

    public bool IsWide => LayoutClass == ShellLayoutClass.Wide;

    public bool IsNotNarrow => LayoutClass != ShellLayoutClass.Narrow;

    /// <summary>The object (or page) the centre of the workspace shows for the current mode.</summary>
    public object? Content => ContentFor(Mode);

    public bool ShowsConnection => Mode == ShellMode.Settings;

    public bool IsEcosystem => Mode == ShellMode.Ecosystem;

    /// <summary>The inspector applies to the ecosystem workspace only; other modes are full-page.</summary>
    private bool InspectorApplies => Mode == ShellMode.Ecosystem;

    private bool HasSelection => Environment.SelectedNode is not null;

    /// <summary>Wide: docked on the right, also when nothing is selected (shows an empty state).</summary>
    public bool ShowDockedInspector => InspectorApplies && IsWide && !InspectorCollapsed;

    /// <summary>Medium: an overlay panel that opens with a selection.</summary>
    public bool ShowOverlayInspector => InspectorApplies && IsMedium && HasSelection && !InspectorCollapsed;

    /// <summary>Narrow: a bottom sheet that slides up with a selection.</summary>
    public bool ShowInspectorSheet => InspectorApplies && IsNarrow && HasSelection && !InspectorCollapsed;

    /// <summary>Any inspector placement; the view puts the one inspector where <see cref="LayoutClass"/> says.</summary>
    public bool ShowInspector => ShowDockedInspector || ShowOverlayInspector || ShowInspectorSheet;

    public bool ShowTimeline => TimelineOpen;

    /// <summary>Called by the view with the available width; picks the layout class.</summary>
    public void ApplyWidth(double width) => LayoutClass = ShellLayout.Classify(width);

    /// <summary>Navigation preserves context (spec 3): the selection and time stay when the mode changes.</summary>
    [RelayCommand]
    public void GoTo(ShellMode target) => Mode = target;

    [RelayCommand]
    public void ToggleRail() => RailExpanded = !RailExpanded;

    [RelayCommand]
    public void ToggleInspector()
    {
        InspectorCollapsed = !InspectorCollapsed;
        RaiseVisibility();
    }

    [RelayCommand]
    public void ToggleTimeline() => TimelineOpen = !TimelineOpen;

    /// <summary>Escape: closes the search results, then the inspector or sheet (clearing the selection).</summary>
    [RelayCommand]
    public void Dismiss()
    {
        if (SearchDropdownOpen)
        {
            SearchDropdownOpen = false;
        }
        else if (Environment.SelectedNodeId is not null)
        {
            Environment.SelectedNodeId = null;
        }
        else if (Mode == ShellMode.Ecosystem && IsWide)
        {
            InspectorCollapsed = true;
            RaiseVisibility();
        }
    }

    [RelayCommand]
    public void SelectBreadcrumb(BreadcrumbItem? item)
    {
        if (item?.NodeId is { } id)
        {
            Environment.SelectedNodeId = id;
        }
    }

    partial void OnModeChanged(ShellMode value)
    {
        syncing = true;
        try
        {
            SelectedModeItem = Modes.First(m => m.Mode == value);
        }
        finally
        {
            syncing = false;
        }

        SearchDropdownOpen = false;
        RebuildSubSections();
        PushSectionToMain();
        RebuildBreadcrumbs();
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(ShowsConnection));
        OnPropertyChanged(nameof(IsEcosystem));
        RaiseVisibility();
    }

    partial void OnSelectedModeItemChanged(ShellModeItem value)
    {
        if (!syncing)
        {
            Mode = value.Mode;
        }
    }

    partial void OnSelectedSubSectionChanged(SectionViewModel? value)
    {
        OnPropertyChanged(nameof(Content));
        if (!syncing)
        {
            PushSectionToMain();
        }

        RebuildBreadcrumbs();
    }

    partial void OnLayoutClassChanged(ShellLayoutClass value)
    {
        OnPropertyChanged(nameof(IsNarrow));
        OnPropertyChanged(nameof(IsMedium));
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(IsNotNarrow));
        // The phone starts with the timeline folded away; historical state always keeps it reachable.
        TimelineOpen = value != ShellLayoutClass.Narrow || Environment.IsHistorical;
        RaiseVisibility();
    }

    partial void OnInspectorCollapsedChanged(bool value) => RaiseVisibility();

    partial void OnTimelineOpenChanged(bool value) => OnPropertyChanged(nameof(ShowTimeline));

    private static IReadOnlyList<ShellModeItem> ModesFor() =>
        [
            new(ShellMode.Ecosystem, "Ecosystem", "Ecosystem", "ecosystem", "Ecosystem: your devices and services"),
            new(ShellMode.Alerts, "Alerts", "Alerts", "alerts", "Alerts"),
            new(ShellMode.Activity, "Activity and history", "History", "history", "Activity and history"),
            new(ShellMode.Search, "Search", "Search", "search", "Search (Ctrl+K)"),
            new(ShellMode.Settings, "Settings", "More", "settings", "Settings: devices, pairing, this device"),
        ];

    private object? ContentFor(ShellMode target) => target switch
    {
        ShellMode.Ecosystem => Environment,
        ShellMode.Alerts => main.Sections.OfType<AlertsViewModel>().Single(),
        ShellMode.Search => Search,
        _ => SelectedSubSection,
    };

    private IEnumerable<SectionViewModel> SubSectionsFor(ShellMode target)
    {
        switch (target)
        {
            case ShellMode.Activity:
                return main.Sections.Where(s => s is StateViewModel or HistoryViewModel or AsOfViewModel)
                    .OrderBy(s => s is StateViewModel ? 0 : s is HistoryViewModel ? 1 : 2);
            case ShellMode.Settings:
                return main.Sections.Where(s => s is DevicesViewModel or PairingsViewModel or PairDeviceViewModel or OverviewViewModel)
                    .OrderBy(s => s switch { DevicesViewModel => 0, PairingsViewModel => 1, PairDeviceViewModel => 2, _ => 3 });
            default:
                return [];
        }
    }

    private void RebuildSubSections()
    {
        var wanted = SubSectionsFor(Mode).ToList();
        var keep = SelectedSubSection is { } current && wanted.Contains(current) ? current : wanted.FirstOrDefault();
        SubSections.Clear();
        foreach (var section in wanted)
        {
            SubSections.Add(section);
        }

        syncing = true;
        try
        {
            SelectedSubSection = keep;
        }
        finally
        {
            syncing = false;
        }

        OnPropertyChanged(nameof(HasSubSections));
        OnPropertyChanged(nameof(Content));
    }

    /// <summary>Keeps <see cref="MainViewModel.SelectedSection"/> (and its refresh-on-select) in step with what is on screen.</summary>
    private void PushSectionToMain()
    {
        var section = Mode switch
        {
            ShellMode.Ecosystem => Environment,
            ShellMode.Alerts => main.Sections.OfType<AlertsViewModel>().Single(),
            ShellMode.Search => null,
            _ => SelectedSubSection,
        };
        if (section is not null && !ReferenceEquals(main.SelectedSection, section))
        {
            main.SelectedSection = section;
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.SelectedSection))
        {
            return;
        }

        var section = main.SelectedSection;
        var target = section switch
        {
            EnvironmentViewModel => ShellMode.Ecosystem,
            AlertsViewModel => ShellMode.Alerts,
            StateViewModel or HistoryViewModel or AsOfViewModel => ShellMode.Activity,
            _ => ShellMode.Settings,
        };
        if (target != Mode)
        {
            syncing = true;
            try
            {
                SelectedSubSection = SubSectionsFor(target).Contains(section) ? section : null;
            }
            finally
            {
                syncing = false;
            }

            Mode = target;
        }

        if (HasSubSections && SubSections.Contains(section))
        {
            SelectedSubSection = section;
        }
    }

    private void OnEnvironmentChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(EnvironmentViewModel.AsOf):
                OnPropertyChanged(nameof(IsHistorical));
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(LiveText));
                OnPropertyChanged(nameof(LiveAccessibleText));
                if (Environment.IsHistorical)
                {
                    TimelineOpen = true;
                }

                break;
            case nameof(EnvironmentViewModel.SelectedNode):
                if (Environment.SelectedNode is not null)
                {
                    InspectorCollapsed = false;
                }

                RebuildBreadcrumbs();
                RaiseVisibility();
                break;
            case nameof(EnvironmentViewModel.Scene):
                RebuildBreadcrumbs();
                break;
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var title = Modes.First(m => m.Mode == Mode).Title;
        Breadcrumbs.Add(new BreadcrumbItem(title, null));
        if (Mode == ShellMode.Ecosystem)
        {
            var chain = new List<SceneNode>();
            var node = Environment.SelectedNode;
            while (node is not null && chain.Count < 8 && !chain.Contains(node))
            {
                chain.Insert(0, node);
                node = Environment.Scene.Find(node.ParentId);
            }

            foreach (var item in chain)
            {
                Breadcrumbs.Add(new BreadcrumbItem(item.Label, item.Id, true));
            }
        }
        else if (SelectedSubSection is { } sub)
        {
            Breadcrumbs.Add(new BreadcrumbItem(sub.Title, null, true));
        }

        OnPropertyChanged(nameof(ToolbarTitle));
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowDockedInspector));
        OnPropertyChanged(nameof(ShowOverlayInspector));
        OnPropertyChanged(nameof(ShowInspectorSheet));
        OnPropertyChanged(nameof(ShowInspector));
    }
}
