using System.Globalization;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Dive;

/// <summary>
/// A status in words. <see cref="Health"/> picks the shape and colour drawn beside the words; <see cref="IsTransfer"/> marks active
/// information transfer (blue) instead of a health state; <see cref="IsFault"/> marks a fault the scene states as a number (a part over
/// <see cref="DiveViewModel.FullThreshold"/> full), drawn in the fault colour. Severity is never shown by colour alone.
/// </summary>
public sealed record DiveBadge(string Text, SceneHealth Health, bool IsTransfer = false, bool IsFault = false);

/// <summary>How full a gauge is: Normal below 80 %, High from 80 %, Critical from 90 %; None when the layer has no true proportion.</summary>
public enum DiveLevel
{
    None,
    Normal,
    High,
    Critical,
}

/// <summary>
/// One layer of a node as a gauge: a ring when the layer has a real proportion (<see cref="Fill"/>), otherwise a stat showing the
/// first reading in words. Tapping it dives into the layer as a part (<see cref="NodeId"/>, <see cref="LayerId"/>).
/// </summary>
public sealed record DiveGauge(
    string NodeId,
    string LayerId,
    string Label,
    string Value,
    string Unit,
    double? Fill,
    DiveLevel Level,
    string Caption,
    DiveTrend? Trend = null)
{
    public bool HasFill => Fill is not null;

    /// <summary>A sparkline of the recent window is shown only when real readings make one (never in a recorded view).</summary>
    public bool HasTrend => Trend is not null;

    public double FillValue => Fill ?? 0;

    public string AccessibleText => HasFill
        ? $"{Label}: {Value}{Unit}. {Caption}.{TrendSentence} Open {Label}."
        : $"{Label}: {Value}. {Caption}.{TrendSentence} Open {Label}.";

    private string TrendSentence => Trend is null ? string.Empty : " " + Trend.Text + ".";
}

/// <summary>Something that needs attention, located as precisely as the data allows (a node, or a layer of it).</summary>
/// <param name="Depth">Ancestors of the node, plus one when the problem is a layer: deeper means closer to the cause.</param>
/// <param name="IsFault">A failure or an over-full part, as opposed to a device that is merely offline or stale.</param>
public sealed record DiveProblem(string NodeId, string? LayerId, string Text, SceneHealth Health, int Depth, bool IsFault);

/// <summary>A tile on the home grid. <see cref="SpansRow"/> tiles take a whole row.</summary>
public abstract record DiveTile(string Key)
{
    public virtual bool SpansRow => false;

    public abstract string AccessibleText { get; }
}

/// <summary>A top-level device: name, status in words and shape, a few key gauges, and what is inside it.</summary>
/// <param name="Headline">Large text shown instead of gauges when the readings cannot be trusted as current ("--" offline, last-seen time when stale).</param>
/// <param name="Lines">Short facts for layers without a proportion ("Connectivity: Connected · VPN on").</param>
/// <param name="Attention">Sort key: 4 fault, 3 offline, 2 stale, 1 unknown, 0 fine (the node or anything inside it).</param>
public sealed record DiveDeviceTile(
    string NodeId,
    string Title,
    string KindText,
    DiveBadge Badge,
    IReadOnlyList<DiveGauge> Gauges,
    IReadOnlyList<string> Lines,
    string? Headline,
    SceneHealth HeadlineHealth,
    string Detail,
    double Activity,
    string? ChildrenText,
    int Attention,
    IReadOnlyList<DiveTrend>? Trends = null) : DiveTile(NodeId)
{
    public bool HasGauges => Gauges.Count > 0;

    /// <summary>Sparklines of the recent window (CPU, network) built from real readings; empty when there are none.</summary>
    public IReadOnlyList<DiveTrend> TrendList => Trends ?? [];

    public bool HasTrends => TrendList.Count > 0;

    /// <summary>The one reading a compact (phone) tile shows: the fullest gauge, the first on a tie.</summary>
    public DiveGauge? KeyGauge => Gauges.Count == 0 ? null : Gauges.OrderByDescending(g => g.Level).ThenByDescending(g => g.FillValue).First();

    public bool HasKeyGauge => KeyGauge is not null;

    /// <summary>The compact tile's one line when it has no gauge: the first fact, else the detail.</summary>
    public string CompactLine => Lines.Count > 0 ? Lines[0] : Detail;

    public bool HasLines => Lines.Count > 0;

    public bool HasHeadline => Headline is not null;

    public bool HasActivity => Activity > 0;

    public bool HasChildren => ChildrenText is not null;

    public string ActivityText => "Transfer activity " + SignalCatalog.Percent(Activity);

    public override string AccessibleText =>
        $"{Title}, {KindText}. {Badge.Text}. {string.Join(". ", Gauges.Select(g => $"{g.Label} {g.Value}{g.Unit}"))} {string.Concat(TrendList.Select(t => t.Text + ". "))}{Detail}. Dive in.";
}

/// <summary>The summary of everything that needs attention, with a way to dive straight to the most likely cause.</summary>
public sealed record DiveAttentionTile(string Headline, string Breakdown, IReadOnlyList<DiveProblem> Items, DiveProblem? Cause) : DiveTile("attention")
{
    public override bool SpansRow => true;

    public bool HasCause => Cause is not null;

    public bool IsAllClear => Items.Count == 0;

    public string CauseText => Cause is null ? string.Empty : "Dive to the cause";

    public override string AccessibleText => Cause is null
        ? $"Needs attention: {Headline}."
        : $"Needs attention: {Headline}. {Breakdown}. Dive to the cause: {Cause.Text}.";
}

/// <summary>A decision waiting for this person on a paired computer (a pairing request or a staged update).</summary>
public sealed record DiveApprovalTile(string HostNodeId, string HostName, string Title, string Detail, string TierText, bool IsUpdate)
    : DiveTile("approval:" + HostNodeId + ":" + Title)
{
    public string KindText => IsUpdate ? "Update waiting for approval" : "Pairing request";

    public override string AccessibleText => $"{KindText}: {Title}. {Detail}. {TierText}. Open {HostName} to decide.";
}

/// <summary>A child node inside the current one (a hosted service or observed device) you can dive into again.</summary>
public sealed record DiveChamber(string NodeId, string Title, string KindText, DiveBadge Badge, string Detail, string Hint)
{
    public string AccessibleText => $"{Title}, {KindText}. {Badge.Text}. {Detail}. {Hint}.";
}

/// <summary>A relationship to another node, as a way to go there.</summary>
public sealed record DiveRelation(string NodeId, string Text, SceneHealth Health)
{
    public string AccessibleText => Text;
}

/// <summary>One exact reading with its provenance.</summary>
public sealed record DiveReadingRow(string Label, string Value, string Meta)
{
    public string AccessibleText => $"{Label}: {Value}. {Meta}.";
}

/// <summary>Which way a navigation went, so the view can zoom in from the tapped tile or back out.</summary>
public enum DiveDirection
{
    In,
    Out,
}

/// <summary>Raised after the Dive moved to a new place.</summary>
public sealed class DiveNavigatedEventArgs(string? fromNodeId, string? fromLayerId, DiveDirection direction) : EventArgs
{
    public string? FromNodeId { get; } = fromNodeId;

    public string? FromLayerId { get; } = fromLayerId;

    public DiveDirection Direction { get; } = direction;
}

/// <summary>Small text helpers shared by the Dive pages.</summary>
public static class DiveText
{
    /// <summary>Local wall-clock time ("21:14"), or "an unknown time".</summary>
    public static string Clock(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "an unknown time";

    public static string Percent(double fill) =>
        Math.Round(Math.Clamp(fill, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture);

    public static string ShortLabel(SceneLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return layer.Kind switch
        {
            SceneLayerKind.Compute => "CPU",
            SceneLayerKind.Memory => "Memory",
            SceneLayerKind.Storage => "Storage",
            SceneLayerKind.Power => "Battery",
            SceneLayerKind.Network => "Network",
            SceneLayerKind.Connectivity => "Connectivity",
            SceneLayerKind.Services => "Services",
            SceneLayerKind.Collection => "Monitoring",
            _ => "Other",
        };
    }

    public static string HealthWord(SceneHealth health) => health switch
    {
        SceneHealth.Healthy => "OK",
        SceneHealth.Stale => "Stale",
        SceneHealth.Degraded => "Degraded",
        SceneHealth.Offline => "Offline",
        _ => "No data yet",
    };
}
