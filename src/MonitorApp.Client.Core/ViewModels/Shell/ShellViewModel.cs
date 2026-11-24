using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Activity;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>One step of the breadcrumb trail; <see cref="NodeId"/> is set when it can be selected.</summary>
/// <remarks>In the Dive, <see cref="LayerId"/> names a part (a layer of <see cref="NodeId"/>) and a crumb without a node is Home.</remarks>
public sealed record BreadcrumbItem(string Text, string? NodeId, bool ShowSeparator = false, string? LayerId = null);

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
    private bool railExpanded = false; // Labels beside the icons until the person collapses it.

    [ObservableProperty]
    private bool inspectorCollapsed;

    [ObservableProperty]
    private bool timelineOpen = true;

    [ObservableProperty]
    private ShellLayoutClass layoutClass = ShellLayoutClass.Wide;

    [ObservableProperty]
    private bool searchDropdownOpen;

    [ObservableProperty]
    private bool diveEnabled = true;

    [ObservableProperty]
    private bool outlineOpen;

    /// <summary>The ecosystem as the map: every device as a card, joined by its typed connections, with pan and zoom.</summary>
    [ObservableProperty]
    private bool mapOpen;

    private ThingsViewModel? thingsLens;

    public ShellViewModel(MainViewModel main, NodeConnection connection, EnvironmentViewModel environment)
    {
        this.main = main;
        Environment = environment;
        Modes = ModesFor(main.Sections.OfType<ThingsViewModel>().Any());
        selectedModeItem = Modes[0];
        Search = new SearchViewModel(environment);
        Inspector = new InspectorViewModel(environment, connection);
        Timeline = new TimelineViewModel(environment);
        Dive = new DiveViewModel(environment, Inspector.Controls, connection);
        Dive.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DiveViewModel.Crumbs) or nameof(DiveViewModel.CanGoUp) or nameof(DiveViewModel.BackText))
            {
                RebuildBreadcrumbs();
                OnPropertyChanged(nameof(ShowDiveBack));
            }
        };

        Alerts = main.Sections.OfType<AlertsViewModel>().Single();
        Alerts.Attach(environment, Inspector.Controls, Dive);
        Alerts.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AlertsViewModel.Page))
            {
                RebuildBreadcrumbs();
                OnPropertyChanged(nameof(ShowAlertBack));
            }
        };
        Alerts.DiveRequested += (_, target) => DiveTo(target.NodeId, target.LayerId);

        Activity = main.Sections.OfType<ActivityViewModel>().Single();
        Activity.Attach(environment);
        Activity.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ActivityViewModel.Page))
            {
                if (Mode == ShellMode.Activity && !syncing)
                {
                    PushSectionToMain();
                }

                RebuildBreadcrumbs();
                OnPropertyChanged(nameof(ShowActivityBack));
            }
        };
        Activity.DiveRequested += (_, target) => DiveTo(target.NodeId, target.LayerId);

        // Things and the Ecosystem are two lenses on the same objects: each can open the other on the thing you picked.
        if (main.Sections.OfType<ThingsViewModel>().SingleOrDefault() is { } things)
        {
            thingsLens = things;
            things.AsOf = environment.AsOf;
            things.DiveRequested += (_, target) => DiveTo(target.NodeId, target.LayerId);
            Dive.CanOpenInThings = true;
            Dive.ThingRequested += (_, nodeId) => OpenInThings(nodeId);
        }

        // A paired device's page leads straight to its pairing card: fingerprint, connection, Remove.
        if (main.Sections.OfType<PairingsViewModel>().SingleOrDefault() is { } pairings)
        {
            Dive.CanOpenPairing = true;
            Dive.PairingRequested += (_, _) => main.SelectedSection = pairings;
        }

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

    /// <summary>The page on screen when it is a section: its action results and errors are shown in the shell's feedback bar.</summary>
    public SectionViewModel? ActiveSection => Content as SectionViewModel;

    public bool ShowsConnection => Mode == ShellMode.Settings;

    public bool IsEcosystem => Mode == ShellMode.Ecosystem;

    /// <summary>The ecosystem as the Dive (the default): home grid, pages per object, no side inspector.</summary>
    public DiveViewModel Dive { get; }

    /// <summary>
    /// True when the ecosystem is shown as the Dive: on by default, off while the outline (the accessible alternative, with the
    /// inspector) is open, and off when a head plugs in its own workspace template (see WorkspaceShell.WorkspaceTemplate).
    /// </summary>
    public bool IsDive => Mode == ShellMode.Ecosystem && DiveEnabled && !OutlineOpen && !MapOpen;

    /// <summary>True when the ecosystem is shown as the map (the node-and-connection graph) with the inspector.</summary>
    public bool IsMap => Mode == ShellMode.Ecosystem && DiveEnabled && MapOpen && !OutlineOpen;

    /// <summary>The map's "Open it up in Things" button: only when a Things lens exists.</summary>
    public bool CanOpenInThings => thingsLens is not null;

    public bool IsNotDive => !IsDive;

    /// <summary>The toolbar's outline switch is offered wherever the Dive could be shown.</summary>
    public bool ShowOutlineToggle => Mode == ShellMode.Ecosystem && DiveEnabled;

    public bool ShowInspectorToggle => InspectorApplies;

    /// <summary>The phone's back chip ("‹ Home server") replaces the breadcrumb trail while inside the Dive.</summary>
    public bool ShowDiveBack => IsDive && IsNarrow && Dive.CanGoUp;

    /// <summary>The Alerts mode: grouped alert cards and a page per alert, in the Dive's language.</summary>
    public AlertsViewModel Alerts { get; }

    /// <summary>The phone's back chip ("‹ Alerts") while an alert page is open.</summary>
    public bool ShowAlertBack => Mode == ShellMode.Alerts && IsNarrow && Alerts.CanGoBack;

    /// <summary>The History mode: the "What happened" story, a page per event, and the older history tools under "Browse history".</summary>
    public ActivityViewModel Activity { get; }

    /// <summary>The phone's back chip ("‹ History") while an event page or a history tool is open.</summary>
    public bool ShowActivityBack => Mode == ShellMode.Activity && IsNarrow && Activity.CanGoBack;

    /// <summary>Switches to the Ecosystem and goes to a node (and part): in the Dive when it is shown, otherwise by selecting it.</summary>
    public void DiveTo(string? nodeId, string? layerId = null)
    {
        // Move first, then switch: the switch refreshes the environment, which keeps the place as long as the node still exists.
        OutlineOpen = false;
        MapOpen = false;
        if (DiveEnabled)
        {
            Dive.GoTo(nodeId, layerId);
        }
        else
        {
            Environment.SelectedNodeId = nodeId;
        }

        Mode = ShellMode.Ecosystem;
    }

    /// <summary>The inspector applies to the ecosystem workspace when it is not the Dive (the Dive has no side panels); other modes are full-page.</summary>
    private bool InspectorApplies => Mode == ShellMode.Ecosystem && !IsDive;

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

    /// <summary>Switches the ecosystem between the Dive and the outline with the inspector (the keyboard and screen-reader path).</summary>
    [RelayCommand]
    public void ToggleOutline()
    {
        MapOpen = false;
        OutlineOpen = !OutlineOpen;
    }

    /// <summary>Switches the ecosystem between the Dive and the map.</summary>
    [RelayCommand]
    public void ToggleMap()
    {
        OutlineOpen = false;
        MapOpen = !MapOpen;
    }

    /// <summary>From the map: dives into the selected device.</summary>
    [RelayCommand]
    public void DiveIntoSelected()
    {
        if (Environment.SelectedNodeId is { } node)
        {
            DiveTo(node);
        }
    }

    /// <summary>From the map: opens the selected device in Things, pulled apart into its parts.</summary>
    [RelayCommand]
    public void OpenSelectedInThings()
    {
        if (Environment.SelectedNodeId is { } node)
        {
            OpenInThings(node);
        }
    }

    private void OpenInThings(string nodeId)
    {
        if (thingsLens is not { } things)
        {
            return;
        }

        Mode = ShellMode.Things;
        _ = things.OpenNodeAsync(nodeId);
    }

    partial void OnOutlineOpenChanged(bool value) => RaiseDive();

    partial void OnMapOpenChanged(bool value) => RaiseDive();

    partial void OnDiveEnabledChanged(bool value) => RaiseDive();

    /// <summary>Escape: closes the search results, then the inspector or sheet (clearing the selection).</summary>
    [RelayCommand]
    public void Dismiss()
    {
        if (SearchDropdownOpen)
        {
            SearchDropdownOpen = false;
        }
        else if (IsDive)
        {
            // The Dive climbs out one level at a time (part, device, host, Home).
            Dive.Up();
        }
        else if (Mode == ShellMode.Alerts)
        {
            Alerts.GoHome();
        }
        else if (Mode == ShellMode.Activity && Activity.CanGoBack)
        {
            Activity.GoHome();
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
        if (IsDive && item is not null)
        {
            Dive.GoTo(item.NodeId, item.LayerId);
        }
        else if (Mode == ShellMode.Alerts && item is not null)
        {
            // "Alerts" goes home; the open alert's own crumb is where you are.
            if (item.NodeId is null)
            {
                Alerts.GoHome();
            }
        }
        else if (Mode == ShellMode.Activity && item is not null)
        {
            if (item.NodeId is null)
            {
                Activity.GoHome();
            }
        }
        else if (item?.NodeId is { } id)
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
        OnPropertyChanged(nameof(ActiveSection));
        OnPropertyChanged(nameof(ShowsConnection));
        OnPropertyChanged(nameof(IsEcosystem));
        OnPropertyChanged(nameof(ShowAlertBack));
        OnPropertyChanged(nameof(ShowActivityBack));
        RaiseDive();
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
        OnPropertyChanged(nameof(ActiveSection));
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
        OnPropertyChanged(nameof(ShowDiveBack));
        OnPropertyChanged(nameof(ShowAlertBack));
        OnPropertyChanged(nameof(ShowActivityBack));
        // The phone starts with the timeline folded away; historical state always keeps it reachable.
        TimelineOpen = value != ShellLayoutClass.Narrow || Environment.IsHistorical;
        RaiseVisibility();
    }

    partial void OnInspectorCollapsedChanged(bool value) => RaiseVisibility();

    partial void OnTimelineOpenChanged(bool value) => OnPropertyChanged(nameof(ShowTimeline));

    private static IReadOnlyList<ShellModeItem> ModesFor(bool hasThings) =>
    [
        new(ShellMode.Ecosystem, "Home", "Home", "ecosystem", "Home: your devices and services"),
        .. hasThings ? [new ShellModeItem(ShellMode.Things, "Things", "Things", "things", "Things: everything you own or plan, with how each value is known")] : Array.Empty<ShellModeItem>(),
        new(ShellMode.Alerts, "Alerts", "Alerts", "alerts", "Alerts"),
        new(ShellMode.Activity, "Activity and history", "History", "history", "Activity and history"),
        new(ShellMode.Search, "Search", "Search", "search", "Search (Ctrl+K)"),
        new(ShellMode.Settings, "Settings", "More", "settings", "Settings: devices, pairing, this device"),
    ];

    private object? ContentFor(ShellMode target) => target switch
    {
        ShellMode.Ecosystem => Environment,
        ShellMode.Things => main.Sections.OfType<ThingsViewModel>().SingleOrDefault(),
        ShellMode.Alerts => main.Sections.OfType<AlertsViewModel>().Single(),
        ShellMode.Activity => Activity,
        ShellMode.Search => Search,
        _ => SelectedSubSection,
    };

    private IEnumerable<SectionViewModel> SubSectionsFor(ShellMode target)
    {
        switch (target)
        {
            case ShellMode.Settings:
                return main.Sections.Where(s => s is DevicesViewModel or UpdatesViewModel or PairingsViewModel or SecurityViewModel or AlertPreferencesViewModel or PairDeviceViewModel or OverviewViewModel)
                    .OrderBy(s => s switch { PairDeviceViewModel => 0, PairingsViewModel => 1, DevicesViewModel => 2, UpdatesViewModel => 3, SecurityViewModel => 4, AlertPreferencesViewModel => 5, _ => 6 });
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
        OnPropertyChanged(nameof(ActiveSection));
    }

    /// <summary>Keeps <see cref="MainViewModel.SelectedSection"/> (and its refresh-on-select) in step with what is on screen.</summary>
    private void PushSectionToMain()
    {
        var section = Mode switch
        {
            ShellMode.Ecosystem => Environment,
            ShellMode.Things => main.Sections.OfType<ThingsViewModel>().SingleOrDefault(),
            ShellMode.Alerts => main.Sections.OfType<AlertsViewModel>().Single(),
            ShellMode.Activity => Activity.BrowsingSection ?? Activity,
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
            ThingsViewModel => ShellMode.Things,
            AlertsViewModel => ShellMode.Alerts,
            ActivityViewModel or StateViewModel or HistoryViewModel or AsOfViewModel => ShellMode.Activity,
            _ => ShellMode.Settings,
        };
        if (target == ShellMode.Activity && section is not ActivityViewModel)
        {
            // A history tool selected from elsewhere opens inside History, under "Browse history".
            Activity.Browse(section);
        }
        else if (section is ActivityViewModel && Activity.BrowsingSection is not null)
        {
            Activity.GoHome();
        }

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

                // One time for every lens: Things follows the timeline too.
                if (thingsLens is { } things && things.AsOf != Environment.AsOf)
                {
                    things.AsOf = Environment.AsOf;
                    if (Mode == ShellMode.Things)
                    {
                        _ = things.RefreshAsync();
                    }
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
        if (IsDive)
        {
            foreach (var crumb in Dive.Crumbs)
            {
                Breadcrumbs.Add(crumb);
            }

            OnPropertyChanged(nameof(ToolbarTitle));
            return;
        }

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
        else if (Mode == ShellMode.Alerts && Alerts.Page is Alerts.AlertPage open)
        {
            Breadcrumbs.Add(new BreadcrumbItem(open.Title, "alert:" + open.AlertId, true));
        }
        else if (Mode == ShellMode.Activity)
        {
            var crumb = Activity.Page switch
            {
                ActivityEventPage moment => moment.Title,
                ActivityBrowsePage tool => tool.Title,
                _ => null,
            };
            if (crumb is not null)
            {
                Breadcrumbs.Add(new BreadcrumbItem(crumb, "activity:" + crumb, true));
            }
        }
        else if (SelectedSubSection is { } sub)
        {
            Breadcrumbs.Add(new BreadcrumbItem(sub.Title, null, true));
        }

        OnPropertyChanged(nameof(ToolbarTitle));
    }

    private void RaiseDive()
    {
        OnPropertyChanged(nameof(IsDive));
        OnPropertyChanged(nameof(IsMap));
        OnPropertyChanged(nameof(IsNotDive));
        OnPropertyChanged(nameof(ShowOutlineToggle));
        OnPropertyChanged(nameof(ShowInspectorToggle));
        OnPropertyChanged(nameof(ShowDiveBack));
        RebuildBreadcrumbs();
        RaiseVisibility();
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowDockedInspector));
        OnPropertyChanged(nameof(ShowOverlayInspector));
        OnPropertyChanged(nameof(ShowInspectorSheet));
        OnPropertyChanged(nameof(ShowInspector));
    }
}
