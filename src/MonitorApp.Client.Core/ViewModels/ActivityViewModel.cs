using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Activity;
using MonitorApp.Client.Core.ViewModels.Alerts;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// The History mode in the Dive's language. Home is "What happened": a day-grouped story of the last <see cref="FeedWindow"/> built only
/// from real events (<see cref="TimelineEventSource"/>: alerts raised and resolved, pairings, revocations), then "Browse history" with the
/// older tools (current readings, a range with a chart, the state as of a moment) one tap away. Tapping an entry dives into it: the app moves
/// to that moment (<see cref="EnvironmentViewModel.GoToAsync"/>, recorded mode) and the page shows the device's readings as recorded then,
/// with sparklines of the minutes before from history, the way to see the whole home as of then, and the way back to live.
/// <para>
/// Seam: events come from <see cref="TimelineEventSource.LoadAsync"/>; a source that gains an event history (peer online/offline
/// transitions, updates) adds them there and they appear here with no change to this class.
/// </para>
/// </summary>
public sealed partial class ActivityViewModel : SectionViewModel
{
    /// <summary>How far back the story reaches.</summary>
    public static readonly TimeSpan FeedWindow = TimeSpan.FromDays(7);

    /// <summary>Most entries the story lists (newest first); the home says when more exist.</summary>
    public const int DisplayLimit = 200;

    private static readonly TimeSpan NearbyWindow = TimeSpan.FromHours(1);

    private IReadOnlyList<TimelineEvent> events = [];
    private EnvironmentViewModel? environment;
    private TimeProvider timeProvider = TimeProvider.System;
    private bool restoreOnBack;
    private DateTimeOffset? restoreTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(CanGoBack), nameof(BrowsingSection))]
    private object page;

    public ActivityViewModel(NodeConnection connection, IReadOnlyList<SectionViewModel>? tools = null)
        : base(connection, "What happened")
    {
        page = Home;
        foreach (var tool in tools ?? [])
        {
            Home.Browse.Add(tool switch
            {
                StateViewModel => new ActivityBrowseTile(tool, "Current readings", "Every signal's newest reading, with freshness and quality"),
                HistoryViewModel => new ActivityBrowseTile(tool, "Readings over a time range", "Pick a range, a device and a signal; chart and list"),
                AsOfViewModel => new ActivityBrowseTile(tool, "Readings as of a moment", "The latest reading of each signal at a time you choose"),
                _ => new ActivityBrowseTile(tool, tool.Title, string.Empty),
            });
        }
    }

    public ActivityHomePage Home { get; } = new();

    public bool IsHome => Page is ActivityHomePage;

    public bool CanGoBack => !IsHome;

    /// <summary>The history tool on screen, when browsing one; the shell selects it so it loads.</summary>
    public SectionViewModel? BrowsingSection => (Page as ActivityBrowsePage)?.Section;

    /// <summary>The older history tools this mode keeps reachable.</summary>
    public IEnumerable<SectionViewModel> Tools => Home.Browse.Select(t => t.Section);

    public bool IsHistorical => environment?.IsHistorical ?? false;

    public string RecordedText => environment?.AsOf is { } asOf
        ? "RECORDED · as of " + asOf.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>The task filling the open event page (recorded state and sparklines); tests and screenshots wait on it.</summary>
    public Task OpenTask { get; private set; } = Task.CompletedTask;

    /// <summary>Raised after every move between pages, for the zoom animation (same arguments as the Dive's).</summary>
    public event EventHandler<DiveNavigatedEventArgs>? Navigated;

    /// <summary>Asks the shell to switch to the Ecosystem's Dive (at a node, or Home when null).</summary>
    public event EventHandler<BreadcrumbItem>? DiveRequested;

    /// <summary>Connects the story to the scene (device names) and the time the app shows (done by the shell).</summary>
    public void Attach(EnvironmentViewModel env, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        environment = env;
        timeProvider = time ?? TimeProvider.System;
        env.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentViewModel.AsOf))
            {
                OnPropertyChanged(nameof(IsHistorical));
                OnPropertyChanged(nameof(RecordedText));
                if (Page is ActivityEventPage open && env.AsOf != open.Entry.Time)
                {
                    // The user moved time themselves (timeline, "Return to live"): leave it where they put it when going back.
                    restoreOnBack = false;
                }
            }
        };
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var now = timeProvider.GetUtcNow();
            events = await TimelineEventSource.LoadAsync(Client, now - FeedWindow, now, cancellationToken);
            Rebuild();
        });
    }

    /// <summary>Shows already-derived events (tests, screenshots) exactly as a refresh would.</summary>
    public void ShowEvents(IEnumerable<TimelineEvent> derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        events = derived.ToList();
        Rebuild();
    }

    /// <summary>Dives into an entry: moves the app to that moment and fills the page from the recorded scene.</summary>
    [RelayCommand]
    public void Open(ActivityEntry? entry)
    {
        if (entry is null || Page is ActivityEventPage current && current.Entry == entry)
        {
            return;
        }

        if (Page is not ActivityEventPage)
        {
            restoreTo = environment?.AsOf;
        }

        restoreOnBack = true;
        var opened = new ActivityEventPage(entry);
        Fill(opened);
        Page = opened;
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(null, null, DiveDirection.In));
        OpenTask = LoadMomentAsync(opened);
    }

    /// <summary>Opens one of the older history tools.</summary>
    [RelayCommand]
    public void OpenTool(ActivityBrowseTile? tile)
    {
        if (tile is not null)
        {
            Browse(tile.Section);
        }
    }

    /// <summary>Shows a history tool (from its tile, or when the app selects that section). Unknown sections are ignored.</summary>
    public void Browse(SectionViewModel section)
    {
        if (BrowsingSection == section || Home.Browse.FirstOrDefault(t => t.Section == section) is not { } tile)
        {
            return;
        }

        Page = new ActivityBrowsePage(tile);
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(null, null, DiveDirection.In));
    }

    [RelayCommand]
    public void Back() => GoHome();

    /// <summary>
    /// Back to the story; false when already there. Leaving an event page puts the app back at the time it showed before the entry was
    /// opened, unless the user has moved time since.
    /// </summary>
    public bool GoHome()
    {
        if (IsHome)
        {
            return false;
        }

        var leaving = Page as ActivityEventPage;
        Page = Home;
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(null, null, DiveDirection.Out));
        if (leaving is not null && restoreOnBack && environment is { } env && env.AsOf == leaving.Entry.Time)
        {
            restoreOnBack = false;
            OpenTask = restoreTo is { } back ? env.GoToAsync(back) : env.ReturnToNowAsync();
        }

        return true;
    }

    /// <summary>Switches to the Ecosystem's Dive showing the whole home as recorded at the open entry's moment.</summary>
    [RelayCommand]
    public async Task SeeWholeHomeAsync()
    {
        if (Page is not ActivityEventPage open || environment is null)
        {
            return;
        }

        restoreOnBack = false;
        if (environment.AsOf != open.Entry.Time)
        {
            await environment.GoToAsync(open.Entry.Time);
        }

        DiveRequested?.Invoke(this, new BreadcrumbItem("Home", null));
    }

    [RelayCommand]
    public async Task ReturnToLiveAsync()
    {
        restoreOnBack = false;
        if (environment is not null)
        {
            await environment.ReturnToNowAsync();
        }
    }

    private void Rebuild()
    {
        var now = timeProvider.GetUtcNow();
        var today = now.ToLocalTime().Date;
        var entries = events.OrderByDescending(e => e.Time).Take(DisplayLimit).Select(Entry).ToList();
        var days = entries
            .GroupBy(e => e.Time.ToLocalTime().Date)
            .Select(g => new ActivityDay(ActivityText.Day(g.Key, today), g.ToList()))
            .ToList();
        Sync(Home.Days, days);
        Home.HasDays = days.Count > 0;
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        var span = $"in the last {N((int)FeedWindow.TotalDays)} days";
        Home.Headline = entries.Count == 0 ? "Nothing happened"
            : entries.Count == 1 ? "1 thing happened" : $"{N(events.Count)} things happened";
        var parts = new List<string>();
        void Part(int n, string one, string many)
        {
            if (n > 0)
            {
                parts.Add(n == 1 ? "1 " + one : $"{N(n)} {many}");
            }
        }

        Part(events.Count(e => e.Kind == TimelineEventKind.Alert && e.Severity == TimelineSeverity.Fault), "critical alert", "critical alerts");
        Part(events.Count(e => e.Kind == TimelineEventKind.Alert && e.Severity != TimelineSeverity.Fault), "other alert", "other alerts");
        Part(events.Count(e => e.Kind == TimelineEventKind.AlertResolved), "resolved", "resolved");
        Part(events.Count(e => e.Kind is not (TimelineEventKind.Alert or TimelineEventKind.AlertResolved)), "pairing or trust change", "pairing and trust changes");
        Home.Breakdown = parts.Count == 0
            ? $"No alerts, pairings or trust changes {span}."
            : $"{span[..1].ToUpperInvariant()}{span[1..]}: {string.Join(" · ", parts)}."
                + (events.Count > DisplayLimit ? $" Listing the newest {N(DisplayLimit)}." : string.Empty);
    }

    private ActivityEntry Entry(TimelineEvent e)
    {
        var node = e.SubjectNodeId is { } id ? environment?.Scene.Find(id) : null;
        var device = node?.Label ?? e.SubjectNodeId ?? "This home";
        var title = e.Kind == TimelineEventKind.AlertResolved && e.Title.StartsWith("Resolved: ", StringComparison.Ordinal)
            ? e.Title["Resolved: ".Length..]
            : e.Title;
        return new ActivityEntry(e, DiveText.Clock(e.Time), ActivityText.Badge(e), ActivityText.Kind(e), title, device, e.SubjectNodeId);
    }

    /// <summary>What the page can say before the recorded state arrives: the event itself and what else happened around then.</summary>
    private void Fill(ActivityEventPage open)
    {
        var e = open.Entry.Event;
        var now = timeProvider.GetUtcNow();
        open.WhenText = $"{e.Time.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture)} at {DiveText.Clock(e.Time)} · "
            + (now - e.Time < TimeSpan.FromMinutes(1) ? "just now" : AlertText.Duration(now - e.Time) + " ago");
        open.Detail = AlertText.Readable(e.Detail);
        open.DeviceTitle = open.Entry.DeviceText;
        open.ReadingsNote = $"Reading the recorded state at {open.MomentText}…";
        var nearby = events
            .Where(x => x != e && (x.Time - e.Time).Duration() <= NearbyWindow)
            .OrderBy(x => x.Time)
            .Take(5)
            .Select(Entry)
            .ToList();
        Sync(open.Nearby, nearby);
        open.HasNearby = nearby.Count > 0;
    }

    /// <summary>Moves the app to the entry's moment, then fills the device's readings from the recorded scene and its sparklines from history.</summary>
    private async Task LoadMomentAsync(ActivityEventPage open)
    {
        if (environment is not { } env)
        {
            open.IsLoading = false;
            open.ReadingsNote = "The recorded state is not available here.";
            return;
        }

        var at = open.Entry.Time;
        await env.GoToAsync(at);
        if (!ReferenceEquals(Page, open))
        {
            return;
        }

        if (env.ErrorMessage is { } error)
        {
            open.IsLoading = false;
            open.ReadingsNote = $"Could not read the recorded state at {open.MomentText}: {error}";
            return;
        }

        var node = open.Entry.NodeId is { } id ? env.Scene.Find(id) : null;
        if (node is null)
        {
            open.IsLoading = false;
            open.ReadingsNote = open.HasDevice
                ? $"{open.Entry.DeviceText} is not in the recorded view at {open.MomentText}."
                : $"This concerns the home as a whole; see the whole home as of {open.MomentText}.";
            return;
        }

        var trends = await TrendsBeforeAsync(node, at);
        open.DeviceTitle = node.Label;
        open.DeviceBadge = new DiveBadge(node.Health switch
        {
            SceneHealth.Offline => $"Offline at {open.MomentText}",
            SceneHealth.Stale => $"Stale · last seen {DiveText.Clock(node.LastSeen)}",
            _ => DiveText.HealthWord(node.Health),
        }, node.Health);
        open.DeviceMeta = $"{InspectorViewModel.KindText(node.Kind)} · {node.HealthReason}";
        var gauges = node.Health is SceneHealth.Offline ? [] : node.Layers.Select(l => DiveViewModel.Gauge(node, l, trends)).ToList();
        Sync(open.Gauges, gauges);
        open.GaugeCount = gauges.Count;
        open.IsLoading = false;
        open.ReadingsNote = gauges.Count == 0
            ? $"No readings from {node.Label} were recorded around {open.MomentText}."
            : string.Empty;
    }

    /// <summary>Sparklines of the minutes before the moment, from the node's real history; none when history has fewer than two points.</summary>
    private async Task<DiveTrendBuffer?> TrendsBeforeAsync(SceneNode node, DateTimeOffset at)
    {
        var trends = new DiveTrendBuffer { EndsAt = DiveText.Clock(at) };
        foreach (var signal in DiveTrendBuffer.Signals)
        {
            try
            {
                var history = await Client.GetHistoryAsync(new HistoryQuery(at - DiveTrendBuffer.Window, at, SignalId: signal), maxPages: 2);
                trends.AddHistory(history.Points);
            }
            catch (LocalApiException)
            {
                // Sparklines are optional; the rings still show the recorded values.
            }
        }

        return trends.Count == 0 ? null : trends;
    }

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
