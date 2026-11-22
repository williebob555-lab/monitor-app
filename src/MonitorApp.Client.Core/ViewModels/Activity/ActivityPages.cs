using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels.Activity;

/// <summary>
/// One thing that happened, as a row of the "What happened" story: when, severity as shape and word, what kind of thing, the device it
/// concerns and its title. Built only from a <see cref="TimelineEvent"/> (real alert, pairing and trust data) and the scene's device names.
/// </summary>
public sealed record ActivityEntry(
    TimelineEvent Event,
    string TimeText,
    DiveBadge Severity,
    string KindText,
    string Title,
    string DeviceText,
    string? NodeId)
{
    public DateTimeOffset Time => Event.Time;

    /// <summary>A critical alert being raised: drawn with the fault frame, like the Dive's attention tile.</summary>
    public bool IsAlarm => Event.Kind == TimelineEventKind.Alert && Event.Severity == TimelineSeverity.Fault;

    public string AccessibleText => $"{TimeText}, {Severity.Text}: {KindText}. {Title}. {DeviceText}. Open what happened.";
}

/// <summary>A day of the story ("Today · Sun 22 Nov"), newest entries first.</summary>
public sealed record ActivityDay(string Title, IReadOnlyList<ActivityEntry> Items)
{
    public string Heading => $"{Title.ToUpperInvariant()} · {Items.Count.ToString(CultureInfo.InvariantCulture)}";

    public bool Equals(ActivityDay? other) => other is not null && Title == other.Title && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Title, Items.Count);
}

/// <summary>A way into the older history tools (current readings, a range with a chart, the state as of a moment), kept one tap away.</summary>
public sealed record ActivityBrowseTile(SectionViewModel Section, string Title, string Detail)
{
    public string AccessibleText => $"{Title}. {Detail}. Open.";
}

/// <summary>History home: the story of the last days grouped by day, then the tools to browse readings directly.</summary>
public sealed partial class ActivityHomePage : ObservableObject
{
    [ObservableProperty]
    private string headline = "Nothing loaded yet";

    [ObservableProperty]
    private string breakdown = string.Empty;

    [ObservableProperty]
    private bool hasDays;

    public ObservableCollection<ActivityDay> Days { get; } = [];

    public ObservableCollection<ActivityBrowseTile> Browse { get; } = [];
}

/// <summary>
/// One thing that happened, dived into: what it was and when, the device it concerns with its readings as recorded at that moment (rings,
/// and sparklines of the minutes before when history has them), what else happened around then, and the way to see the whole home then.
/// </summary>
public sealed partial class ActivityEventPage(ActivityEntry entry) : ObservableObject
{
    [ObservableProperty]
    private string whenText = string.Empty;

    [ObservableProperty]
    private string detail = string.Empty;

    [ObservableProperty]
    private string deviceTitle = string.Empty;

    [ObservableProperty]
    private string deviceMeta = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceBadge))]
    private DiveBadge? deviceBadge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadingsNote))]
    private string readingsNote = string.Empty;

    [ObservableProperty]
    private bool isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGauges))]
    private int gaugeCount;

    [ObservableProperty]
    private bool hasNearby;

    public ActivityEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    public DiveBadge Severity => Entry.Severity;

    public string KindText => Entry.KindText;

    public bool IsAlarm => Entry.IsAlarm;

    /// <summary>The moment this page is about, as a local clock time ("21:14").</summary>
    public string MomentText => DiveText.Clock(Entry.Time);

    public string ReadingsHeading => $"{(Entry.NodeId is null ? "THE HOME" : "THE DEVICE")} AT {MomentText}";

    public string WholeHomeText => $"See the whole home as of {MomentText}";

    public bool HasDevice => Entry.NodeId is not null;

    public bool HasDeviceBadge => DeviceBadge is not null;

    public bool HasGauges => GaugeCount > 0;

    public bool HasReadingsNote => ReadingsNote.Length > 0;

    /// <summary>The device's parts as recorded at the moment (from the scene in recorded mode), with sparklines of the minutes before.</summary>
    public ObservableCollection<DiveGauge> Gauges { get; } = [];

    /// <summary>Other entries within an hour either side, for context.</summary>
    public ObservableCollection<ActivityEntry> Nearby { get; } = [];
}

/// <summary>One of the older history tools, opened from the home's "Browse history" (its own view is shown inside).</summary>
public sealed class ActivityBrowsePage(ActivityBrowseTile tile)
{
    public ActivityBrowseTile Tile { get; } = tile;

    public SectionViewModel Section => Tile.Section;

    public string Title => Tile.Title;

    public string Detail => Tile.Detail;
}

/// <summary>Words for the story: kinds, severities and day titles.</summary>
public static class ActivityText
{
    /// <summary>Severity as shape and word, by what the event is: raised alerts by their severity, the rest by what they mean.</summary>
    public static DiveBadge Badge(TimelineEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            TimelineEventKind.Alert => e.Severity switch
            {
                TimelineSeverity.Fault => new DiveBadge("Critical", SceneHealth.Degraded, IsFault: true),
                TimelineSeverity.Warning => new DiveBadge("Warning", SceneHealth.Degraded),
                _ => new DiveBadge("Info", SceneHealth.Unknown),
            },
            TimelineEventKind.AlertResolved => new DiveBadge("Resolved", SceneHealth.Healthy),
            TimelineEventKind.PeerBack => new DiveBadge("Back", SceneHealth.Healthy),
            TimelineEventKind.PeerOffline => new DiveBadge("Offline", SceneHealth.Offline),
            TimelineEventKind.PeerStale => new DiveBadge("Stale", SceneHealth.Stale),
            TimelineEventKind.Pairing when e.Title.StartsWith("Paired", StringComparison.Ordinal) => new DiveBadge("Trusted", SceneHealth.Healthy),
            TimelineEventKind.Pairing => new DiveBadge("Request", SceneHealth.Unknown),
            _ => e.Title.StartsWith("Revoked", StringComparison.Ordinal) ? new DiveBadge("Revoked", SceneHealth.Offline) : new DiveBadge("Info", SceneHealth.Unknown),
        };
    }

    public static string Kind(TimelineEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Kind switch
        {
            TimelineEventKind.Alert => "Alert raised",
            TimelineEventKind.AlertResolved => "Alert resolved",
            TimelineEventKind.PeerOffline => "Went offline",
            TimelineEventKind.PeerStale => "Stopped reporting",
            TimelineEventKind.PeerBack => "Came back",
            TimelineEventKind.Pairing => e.Title.StartsWith("Paired", StringComparison.Ordinal) ? "Device paired" : "Pairing request",
            TimelineEventKind.Update => "Update",
            _ => e.Title.StartsWith("Revoked", StringComparison.Ordinal) ? "Trust revoked" : "Event",
        };
    }

    /// <summary>"Today", "Yesterday", else "Fri 20 Nov" (local dates).</summary>
    public static string Day(DateTime localDate, DateTime today) =>
        localDate == today ? "Today · " + localDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)
        : localDate == today.AddDays(-1) ? "Yesterday · " + localDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)
        : localDate.ToString("ddd d MMM", CultureInfo.InvariantCulture);
}
