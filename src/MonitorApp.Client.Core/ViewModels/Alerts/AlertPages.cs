using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels.Alerts;

/// <summary>Which state group an alert sits in on the Alerts home, in display order.</summary>
public enum AlertGroupKind
{
    /// <summary>Critical, raised and not acknowledged from this device.</summary>
    NeedsYou,

    /// <summary>Raised, not acknowledged, below critical.</summary>
    Active,

    /// <summary>Still raised, acknowledged from this device during this session.</summary>
    Acknowledged,

    /// <summary>Resolved within <see cref="AlertsViewModel.RecentWindow"/>.</summary>
    Resolved,
}

/// <summary>
/// One alert as a row-card: severity as shape and word, the device (and part) it concerns, the rule, how long, and the current reading
/// and sparkline when the scene has them. Everything comes from the alert itself or the live scene, never invented.
/// </summary>
public sealed record AlertCard(
    string AlertId,
    DiveBadge Severity,
    string Title,
    string DeviceText,
    string AgeText,
    string? ValueText,
    DiveTrend? Trend,
    AlertGroupKind Group)
{
    public bool HasValue => ValueText is not null;

    public bool HasTrend => Trend is not null;

    /// <summary>Critical and still raised: drawn with the fault frame, like the Dive's attention tile.</summary>
    public bool IsAlarm => Group == AlertGroupKind.NeedsYou;

    public bool IsResolved => Group == AlertGroupKind.Resolved;

    public string AccessibleText =>
        $"{Severity.Text} alert: {Title}. {DeviceText}. {AgeText}.{(ValueText is null ? string.Empty : " Now " + ValueText + ".")} Open alert.";
}

/// <summary>A state group on the Alerts home ("Needs you · 1").</summary>
public sealed record AlertGroup(AlertGroupKind Kind, string Title, string Hint, IReadOnlyList<AlertCard> Items)
{
    public string Heading => $"{Title.ToUpperInvariant()} · {Items.Count.ToString(CultureInfo.InvariantCulture)}";

    public bool Equals(AlertGroup? other) =>
        other is not null && Kind == other.Kind && Title == other.Title && Hint == other.Hint && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Kind, Title, Items.Count);
}

/// <summary>A moment in an alert's life (raised, acknowledged here, resolved, or still active now).</summary>
public sealed record AlertMoment(string Time, string Text, DiveBadge Marker)
{
    public string AccessibleText => $"{Time}: {Text}";
}

/// <summary>Alerts home: decisions waiting for approval at the top, then the alerts grouped by state.</summary>
public sealed partial class AlertsHomePage : ObservableObject
{
    [ObservableProperty]
    private string headline = string.Empty;

    [ObservableProperty]
    private string breakdown = string.Empty;

    [ObservableProperty]
    private bool isAllClear = true;

    [ObservableProperty]
    private bool hasApprovals;

    [ObservableProperty]
    private bool hasGroups;

    public ObservableCollection<DiveApprovalTile> Approvals { get; } = [];

    public ObservableCollection<AlertGroup> Groups { get; } = [];
}

/// <summary>
/// One alert, dived into: what fired and why (rule, threshold and observed value when the alert states them), the device and part it
/// concerns with a way to dive there, its timeline, and the actions that apply (acknowledge, mute the rule), with what each asks first.
/// </summary>
public sealed partial class AlertPage(string alertId, ControlsViewModel? controls) : ObservableObject
{
    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private DiveBadge severity = new(string.Empty, SceneHealth.Unknown);

    [ObservableProperty]
    private DiveBadge stateBadge = new(string.Empty, SceneHealth.Unknown);

    [ObservableProperty]
    private string summary = string.Empty;

    [ObservableProperty]
    private string ruleText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeasure))]
    private string? observedText;

    [ObservableProperty]
    private string? thresholdText;

    [ObservableProperty]
    private string detail = string.Empty;

    [ObservableProperty]
    private string deviceTitle = string.Empty;

    [ObservableProperty]
    private string deviceMeta = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGauge))]
    private DiveGauge? gauge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDiveInto))]
    private string? diveNodeId;

    [ObservableProperty]
    private string? diveLayerId;

    [ObservableProperty]
    private string diveIntoText = string.Empty;

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private bool isAcknowledged;

    [ObservableProperty]
    private bool canAcknowledge;

    [ObservableProperty]
    private bool canMute;

    [ObservableProperty]
    private bool hasActions;

    /// <summary>A one-tap fix for what the alert is about (Syncthing: rescan), shown when the alert has one.</summary>
    [ObservableProperty]
    private bool hasFix;

    [ObservableProperty]
    private bool canFix;

    [ObservableProperty]
    private string fixText = string.Empty;

    [ObservableProperty]
    private string fixHint = string.Empty;

    [ObservableProperty]
    private string acknowledgeTier = string.Empty;

    [ObservableProperty]
    private string muteHourTier = string.Empty;

    [ObservableProperty]
    private string muteDayTier = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionNotice))]
    private string actionNotice = string.Empty;

    public string AlertId { get; } = alertId;

    /// <summary>The shared controls: the outcome of an action shows here, as on the Dive's pages.</summary>
    public ControlsViewModel? Controls { get; } = controls;

    public bool HasMeasure => ObservedText is not null;

    public bool HasGauge => Gauge is not null;

    public bool CanDiveInto => DiveNodeId is not null;

    public bool HasActionNotice => ActionNotice.Length > 0;

    public ObservableCollection<AlertMoment> Timeline { get; } = [];
}

/// <summary>Reads what an alert states about itself: the measured value and threshold of a threshold rule, and its times in local words.</summary>
public static partial class AlertText
{
    /// <summary>The peers' threshold detail: "... : 5.0% is below 10.0% since 2026-...". Anything else has no measure.</summary>
    [GeneratedRegex(@"(?<value>-?\d+(?:\.\d+)?)% is (?<cmp>below|above) (?<threshold>-?\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex MeasurePattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    /// <summary>The observed value and threshold ("5.0 %", "below 10.0 %"), or null when the detail does not state them.</summary>
    public static (string Observed, string Threshold)? Measure(string? detail)
    {
        var match = detail is null ? null : MeasurePattern().Match(detail);
        return match is { Success: true }
            ? (match.Groups["value"].Value + " %", $"{match.Groups["cmp"].Value} {match.Groups["threshold"].Value} %")
            : null;
    }

    /// <summary>Replaces round-trip timestamps in a detail with the local clock ("since 21:14"), so the sentence reads as words.</summary>
    public static string Readable(string? detail) =>
        detail is null
            ? string.Empty
            : TimestampPattern().Replace(detail, m =>
                DateTimeOffset.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                    : m.Value);

    /// <summary>"under a minute", "20 min", "2 h 5 min", "3 days".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min";
        }

        if (span < TimeSpan.FromHours(48))
        {
            var minutes = span.Minutes;
            return minutes == 0
                ? $"{((int)span.TotalHours).ToString(CultureInfo.InvariantCulture)} h"
                : $"{((int)span.TotalHours).ToString(CultureInfo.InvariantCulture)} h {minutes.ToString(CultureInfo.InvariantCulture)} min";
        }

        return $"{((int)span.TotalDays).ToString(CultureInfo.InvariantCulture)} days";
    }

    /// <summary>Severity as shape and word: Critical a fault triangle, Warning an amber triangle, anything else a muted ring.</summary>
    public static DiveBadge SeverityBadge(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => new DiveBadge("Critical", SceneHealth.Degraded, IsFault: true),
        "WARNING" => new DiveBadge("Warning", SceneHealth.Degraded),
        _ => new DiveBadge(string.IsNullOrWhiteSpace(severity) ? "Info" : severity, SceneHealth.Unknown),
    };

    /// <summary>Sort key: Critical first.</summary>
    public static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "CRITICAL" => 0,
        "WARNING" => 1,
        _ => 2,
    };

    /// <summary>The layer a rule watches, from its id ("threshold.cpu_high": compute). Null when the rule names none.</summary>
    public static SceneLayerKind? LayerFor(string ruleId)
    {
        var id = ruleId.ToUpperInvariant();
        return id.Contains("CPU", StringComparison.Ordinal) ? SceneLayerKind.Compute
            : id.Contains("DISK", StringComparison.Ordinal) || id.Contains("STORAGE", StringComparison.Ordinal) ? SceneLayerKind.Storage
            : id.Contains("MEMORY", StringComparison.Ordinal) ? SceneLayerKind.Memory
            : id.Contains("BATTERY", StringComparison.Ordinal) ? SceneLayerKind.Power
            : id.StartsWith("SERVICE.", StringComparison.Ordinal) ? SceneLayerKind.Services
            : null;
    }
}
