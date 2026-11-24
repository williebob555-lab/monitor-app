using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Alerts;
using MonitorApp.Client.Core.ViewModels.Dive;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels;

public sealed record AlertRow(
    string AlertId,
    string Severity,
    string State,
    string Title,
    string Detail,
    string Subject,
    string Origin,
    string Raised,
    string? Resolved)
{
    public bool IsActive => !string.Equals(State, "Resolved", StringComparison.OrdinalIgnoreCase);

    public static AlertRow From(AlertDto alert) => new(
        alert.AlertId,
        alert.Severity,
        alert.State,
        alert.Title,
        alert.Detail,
        alert.SubjectId,
        alert.OriginPeerId,
        Format.Time(alert.RaisedAt),
        alert.ResolvedAt is { } resolved ? Format.Time(resolved) : null);
}

/// <summary>
/// Active and recent alerts held by this peer (desktop: its alert store; phone: its received inbox), in the Dive's language: a home of
/// row-cards grouped by state with pending approvals on top, and a page per alert you dive into (what fired and why, the device it concerns
/// with a way to dive there, its timeline, and the actions that apply). Pages are views of real alert data and the live scene only.
/// <para>Standalone it lists and groups alerts; <see cref="Attach"/> (done by the shell) adds the scene, the shared controls and the Dive.</para>
/// </summary>
public sealed partial class AlertsViewModel : LiveSectionViewModel
{
    /// <summary>Resolved alerts younger than this are listed under "Resolved recently".</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    private static readonly TimeSpan MuteHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan MuteDay = TimeSpan.FromHours(24);

    private IReadOnlyList<AlertDto> loaded = [];
    private EnvironmentViewModel? environment;
    private ControlsViewModel? controls;
    private DiveViewModel? dive;
    private TimeProvider timeProvider = TimeProvider.System;

    [ObservableProperty]
    private string summary = "Not loaded";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(CanGoBack))]
    private object page;

    public AlertsViewModel(NodeConnection connection)
        : base(connection, "Alerts")
    {
        page = Home;
    }

    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public AlertsHomePage Home { get; } = new();

    public bool IsHome => Page is AlertsHomePage;

    public bool CanGoBack => !IsHome;

    /// <summary>True while viewing a recorded moment: actions are off.</summary>
    public bool IsHistorical => environment?.IsHistorical ?? false;

    public string RecordedText => environment?.AsOf is { } asOf
        ? "RECORDED · as of " + asOf.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Raised after every move between Home and an alert page, for the zoom animation (same arguments as the Dive's).</summary>
    public event EventHandler<DiveNavigatedEventArgs>? Navigated;

    /// <summary>Asks the shell to switch to the Ecosystem and dive to a node (and part).</summary>
    public event EventHandler<BreadcrumbItem>? DiveRequested;

    /// <summary>Connects the alerts to the scene (device names, readings, sparklines), the shared controls (actions) and the Dive (approvals).</summary>
    public void Attach(EnvironmentViewModel env, ControlsViewModel sharedControls, DiveViewModel sharedDive, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(sharedControls);
        ArgumentNullException.ThrowIfNull(sharedDive);
        environment = env;
        controls = sharedControls;
        dive = sharedDive;
        timeProvider = time ?? TimeProvider.System;
        env.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EnvironmentViewModel.Scene) or nameof(EnvironmentViewModel.AsOf))
            {
                if (args.PropertyName == nameof(EnvironmentViewModel.AsOf))
                {
                    OnPropertyChanged(nameof(IsHistorical));
                    OnPropertyChanged(nameof(RecordedText));
                }

                Rebuild();
            }
        };
        sharedControls.AlertAcknowledged += (_, _) => Rebuild();
        sharedControls.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ControlsViewModel.CanSend) && Page is AlertPage open)
            {
                FillActions(open, loaded.FirstOrDefault(a => a.AlertId == open.AlertId));
            }
        };
        sharedDive.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiveViewModel.Approvals))
            {
                RebuildApprovals();
            }
        };
        RebuildApprovals();
        Rebuild();
    }

    /// <summary>Opens the page of an alert (from a card or by id). Unknown ids are ignored.</summary>
    [RelayCommand]
    public void Open(AlertCard? card)
    {
        if (card is not null)
        {
            OpenAlert(card.AlertId);
        }
    }

    public void OpenAlert(string alertId)
    {
        var alert = loaded.FirstOrDefault(a => a.AlertId == alertId);
        if (alert is null || Page is AlertPage { } current && current.AlertId == alertId)
        {
            return;
        }

        controls?.ClearOutcome();
        var opened = new AlertPage(alertId, controls);
        FillPage(opened, alert);
        Page = opened;
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(null, null, DiveDirection.In));
    }

    /// <summary>Back to the Alerts home; false when already there.</summary>
    [RelayCommand]
    public void Back() => GoHome();

    public bool GoHome()
    {
        if (IsHome)
        {
            return false;
        }

        var from = (Page as AlertPage)?.AlertId;
        Page = Home;
        Navigated?.Invoke(this, new DiveNavigatedEventArgs(from, null, DiveDirection.Out));
        return true;
    }

    /// <summary>Switches to the Ecosystem and dives to the device (and part) the open alert concerns.</summary>
    [RelayCommand]
    public void DiveInto()
    {
        if (Page is AlertPage { DiveNodeId: { } nodeId } open)
        {
            DiveRequested?.Invoke(this, new BreadcrumbItem(open.DeviceTitle, nodeId, false, open.DiveLayerId));
        }
    }

    /// <summary>A pending approval is decided on the computer that holds it, in the Dive.</summary>
    [RelayCommand]
    public void OpenApproval(DiveApprovalTile? approval)
    {
        if (approval is not null)
        {
            DiveRequested?.Invoke(this, new BreadcrumbItem(approval.HostName, approval.HostNodeId));
        }
    }

    [RelayCommand]
    public async Task AcknowledgeAsync()
    {
        if (Page is not AlertPage { CanAcknowledge: true } open || controls is null || loaded.FirstOrDefault(a => a.AlertId == open.AlertId) is not { } alert)
        {
            return;
        }

        await controls.AcknowledgeAlertOnAsync(alert.OriginPeerId, DeviceName(alert.OriginPeerId), alert.AlertId, alert.Title);
        Rebuild();
    }

    /// <summary>
    /// Runs the alert's one-tap fix. Fixes act on the computer this app is connected to, so an alert raised by another computer
    /// says where to press it instead of acting.
    /// </summary>
    [RelayCommand]
    public async Task FixAsync()
    {
        if (Page is not AlertPage { CanFix: true } open || Client is not IServiceFixSource fixes ||
            loaded.FirstOrDefault(a => a.AlertId == open.AlertId) is not { } alert)
        {
            return;
        }

        open.CanFix = false;
        open.ActionNotice = "Asking Syncthing to rescan…";
        try
        {
            var runtime = await Client.GetRuntimeAsync().ConfigureAwait(true);
            if (!string.Equals(runtime.LocalPeer?.PeerId, alert.OriginPeerId, StringComparison.Ordinal))
            {
                open.ActionNotice = $"This alert is about {DeviceName(alert.OriginPeerId)}. Open Monitor App on that computer to rescan its Syncthing.";
                return;
            }

            var result = await fixes.RescanSyncthingAsync().ConfigureAwait(true);
            open.ActionNotice = result.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or LocalApiException or TaskCanceledException or InvalidOperationException)
        {
            open.ActionNotice = "Could not reach Monitor App on this computer: " + exception.Message;
        }
        finally
        {
            open.CanFix = open.HasFix;
        }
    }

    [RelayCommand]
    public Task MuteHourAsync() => MuteAsync(MuteHour);

    [RelayCommand]
    public Task MuteDayAsync() => MuteAsync(MuteDay);

    private async Task MuteAsync(TimeSpan duration)
    {
        if (Page is not AlertPage { CanMute: true } open || controls is null || loaded.FirstOrDefault(a => a.AlertId == open.AlertId) is not { } alert)
        {
            return;
        }

        await controls.MuteAlertRuleAsync(alert.OriginPeerId, DeviceName(alert.OriginPeerId), alert.RuleId, duration, RuleTitle(alert));
        Rebuild();
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Client is not IAlertSource source)
        {
            Summary = "This node does not provide alerts.";
            return;
        }

        await RunAsync(async () =>
        {
            var found = await source.GetAlertsAsync(100, cancellationToken);
            Alerts.Clear();
            foreach (var alert in found.OrderBy(item => string.Equals(item.State, "Resolved", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.RaisedAt))
            {
                Alerts.Add(AlertRow.From(alert));
            }

            loaded = found;
            var active = Alerts.Count(row => row.IsActive);
            Summary = Alerts.Count == 0 ? "No alerts." : $"{active} active, {Alerts.Count - active} resolved.";
            Rebuild();
        });
    }

    /// <summary>The state group an alert belongs in right now, or null when it is resolved and older than <see cref="RecentWindow"/>.</summary>
    public AlertGroupKind? GroupOf(AlertDto alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (IsResolved(alert))
        {
            return now - (alert.ResolvedAt ?? alert.RaisedAt) <= RecentWindow ? AlertGroupKind.Resolved : null;
        }

        if (controls?.IsAcknowledged(alert.AlertId) == true)
        {
            return AlertGroupKind.Acknowledged;
        }

        return AlertText.SeverityRank(alert.Severity) == 0 ? AlertGroupKind.NeedsYou : AlertGroupKind.Active;
    }

    private static bool IsResolved(AlertDto alert) => string.Equals(alert.State, "Resolved", StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        var now = timeProvider.GetUtcNow();
        var cards = loaded
            .Select(a => (Alert: a, Group: GroupOf(a, now)))
            .Where(x => x.Group is not null)
            .Select(x => (x.Alert, Group: x.Group!.Value))
            .ToList();
        var groups = new List<AlertGroup>();
        void Add(AlertGroupKind kind, string title, string hint, IEnumerable<AlertDto> items)
        {
            var list = items.Select(a => Card(a, kind, now)).ToList();
            if (list.Count > 0)
            {
                groups.Add(new AlertGroup(kind, title, hint, list));
            }
        }

        IEnumerable<AlertDto> In(AlertGroupKind kind) => cards.Where(c => c.Group == kind).Select(c => c.Alert);
        Add(AlertGroupKind.NeedsYou, "Needs you", "Critical and not acknowledged yet", In(AlertGroupKind.NeedsYou).OrderByDescending(a => a.RaisedAt));
        Add(AlertGroupKind.Active, "Active", "Raised and still going", In(AlertGroupKind.Active)
            .OrderBy(a => AlertText.SeverityRank(a.Severity)).ThenByDescending(a => a.RaisedAt));
        Add(AlertGroupKind.Acknowledged, "Acknowledged", "Acknowledged here; still raised", In(AlertGroupKind.Acknowledged)
            .OrderBy(a => AlertText.SeverityRank(a.Severity)).ThenByDescending(a => a.RaisedAt));
        Add(AlertGroupKind.Resolved, "Resolved recently", "Resolved in the last 24 hours", In(AlertGroupKind.Resolved)
            .OrderByDescending(a => a.ResolvedAt ?? a.RaisedAt));
        Sync(Home.Groups, groups);
        Home.HasGroups = groups.Count > 0;

        int Count(AlertGroupKind kind) => cards.Count(c => c.Group == kind);
        var needs = Count(AlertGroupKind.NeedsYou);
        var raised = needs + Count(AlertGroupKind.Active) + Count(AlertGroupKind.Acknowledged);
        Home.IsAllClear = raised == 0;
        Home.Headline = needs > 0
            ? needs == 1 ? "1 needs you" : $"{needs.ToString(System.Globalization.CultureInfo.InvariantCulture)} need you"
            : raised > 0 ? $"{raised.ToString(System.Globalization.CultureInfo.InvariantCulture)} active, none critical" : "All clear";
        var parts = new List<string>();
        void Part(int n, string words)
        {
            if (n > 0)
            {
                parts.Add($"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)} {words}");
            }
        }

        Part(needs, "critical");
        Part(Count(AlertGroupKind.Active), "active");
        Part(Count(AlertGroupKind.Acknowledged), "acknowledged");
        Part(Count(AlertGroupKind.Resolved), "resolved in the last 24 h");
        Home.Breakdown = parts.Count == 0 ? "No alerts raised or resolved in the last 24 hours." : string.Join(" · ", parts);

        if (Page is AlertPage open)
        {
            if (loaded.FirstOrDefault(a => a.AlertId == open.AlertId) is { } alert)
            {
                FillPage(open, alert);
            }
        }
    }

    private void RebuildApprovals()
    {
        var pending = dive?.Approvals ?? [];
        Sync(Home.Approvals, pending);
        Home.HasApprovals = pending.Count > 0;
    }

    private AlertCard Card(AlertDto alert, AlertGroupKind group, DateTimeOffset now)
    {
        var (node, layer) = Locate(alert);
        var device = node?.Label ?? DeviceName(alert.OriginPeerId);
        var deviceText = layer is null ? device : $"{device} · {layer.Title}";
        var age = IsResolved(alert)
            ? $"Lasted {AlertText.Duration((alert.ResolvedAt ?? alert.RaisedAt) - alert.RaisedAt)} · resolved {DiveText.Clock(alert.ResolvedAt)}"
            : $"For {AlertText.Duration(now - alert.RaisedAt)} · since {DiveText.Clock(alert.RaisedAt)}";
        var gauge = LiveGauge(node, layer);
        return new AlertCard(
            alert.AlertId, AlertText.SeverityBadge(alert.Severity), alert.Title, deviceText, age,
            gauge is null ? null : $"{gauge.Label} {gauge.Value}{(gauge.Unit.Length > 0 ? " " + gauge.Unit : string.Empty)}", gauge?.Trend, group);
    }

    private void FillPage(AlertPage open, AlertDto alert)
    {
        var now = timeProvider.GetUtcNow();
        var (node, layer) = Locate(alert);
        var device = node?.Label ?? DeviceName(alert.OriginPeerId);
        var resolved = IsResolved(alert);
        var acknowledged = !resolved && controls?.IsAcknowledged(alert.AlertId) == true;
        open.Title = alert.Title;
        open.Severity = AlertText.SeverityBadge(alert.Severity);
        open.StateBadge = resolved ? new DiveBadge("Resolved", SceneHealth.Healthy)
            : acknowledged ? new DiveBadge("Acknowledged · still raised", SceneHealth.Stale)
            : new DiveBadge("Raised · active", SceneHealth.Degraded, IsFault: AlertText.SeverityRank(alert.Severity) == 0);
        open.Summary = resolved
            ? $"Raised {DiveText.Clock(alert.RaisedAt)} on {device}, resolved {DiveText.Clock(alert.ResolvedAt)} after {AlertText.Duration((alert.ResolvedAt ?? alert.RaisedAt) - alert.RaisedAt)}."
            : $"Raised {DiveText.Clock(alert.RaisedAt)} on {device}, active for {AlertText.Duration(now - alert.RaisedAt)}.";
        open.RuleText = RuleTitle(alert);
        var measure = AlertText.Measure(alert.Detail);
        open.ObservedText = measure?.Observed;
        open.ThresholdText = measure?.Threshold;
        open.Detail = AlertText.Readable(alert.Detail);
        open.DeviceTitle = device;
        open.DeviceMeta = layer is null
            ? (node is null ? $"Not in the current view ({alert.OriginPeerId})" : $"{InspectorViewModel.KindText(node.Kind)} · {node.HealthReason}")
            : $"{layer.Title} · {layer.Summary}";
        open.Gauge = LiveGauge(node, layer);
        open.DiveNodeId = node?.Id;
        open.DiveLayerId = layer?.Id;
        open.DiveIntoText = node is null ? string.Empty : $"Dive into {node.Label}";
        open.IsActive = !resolved;
        open.IsAcknowledged = acknowledged;
        var moments = new List<AlertMoment>
        {
            new(DiveText.Clock(alert.RaisedAt), $"Raised · {alert.Severity}", AlertText.SeverityBadge(alert.Severity)),
        };
        if (acknowledged)
        {
            moments.Add(new AlertMoment("this session", "Acknowledged from this device", new DiveBadge("Acknowledged", SceneHealth.Stale)));
        }

        moments.Add(resolved
            ? new AlertMoment(DiveText.Clock(alert.ResolvedAt), "Resolved", new DiveBadge("Resolved", SceneHealth.Healthy))
            : new AlertMoment("now", $"Still raised after {AlertText.Duration(now - alert.RaisedAt)}", new DiveBadge("Active", SceneHealth.Degraded)));
        Sync(open.Timeline, moments);
        FillActions(open, alert);
    }

    private void FillActions(AlertPage open, AlertDto? alert)
    {
        var source = Client as IControlSource;
        var canSend = controls?.CanSend == true && !IsHistorical && alert is not null;
        var resolved = alert is not null && IsResolved(alert);
        var fix = alert is not null && !IsHistorical && !resolved && Client is IServiceFixSource && IsSyncthingRule(alert.RuleId);
        open.HasFix = fix;
        open.CanFix = fix;
        open.FixText = fix ? "Rescan Syncthing" : string.Empty;
        open.FixHint = fix ? "Asks Syncthing on this computer to look at every folder again. Changes no files." : string.Empty;
        open.HasActions = (source is not null && controls is not null) || fix;
        open.CanAcknowledge = canSend && !resolved && !open.IsAcknowledged;
        open.CanMute = canSend;
        open.AcknowledgeTier = controls?.TierTextFor(ControlActionKind.AcknowledgeAlert) ?? string.Empty;
        open.MuteHourTier = controls?.TierTextFor(ControlActionKind.MuteAlerts, MuteHour) ?? string.Empty;
        open.MuteDayTier = controls?.TierTextFor(ControlActionKind.MuteAlerts, MuteDay) ?? string.Empty;
        open.ActionNotice = IsHistorical ? DiveViewModel.RecordedNotice
            : resolved ? "Resolved: nothing to acknowledge. Muting still holds this rule back if it fires again."
            : open.IsAcknowledged ? "Acknowledged from this device. Muting holds back new notifications from this rule."
            : string.Empty;
    }

    /// <summary>The node an alert concerns (its subject when the scene has it, else the device that raised it) and the part its rule watches.</summary>
    private (SceneNode? Node, SceneLayer? Layer) Locate(AlertDto alert)
    {
        var scene = environment?.Scene;
        if (scene is null)
        {
            return (null, null);
        }

        var subject = alert.SubjectId.StartsWith("peer:", StringComparison.Ordinal) ? alert.SubjectId["peer:".Length..] : alert.SubjectId;
        var node = scene.Find(subject) ?? scene.Find(alert.OriginPeerId);
        var kind = AlertText.LayerFor(alert.RuleId);
        var layer = node is null || kind is null ? null : node.Layers.FirstOrDefault(l => l.Kind == kind);
        return (node, layer);
    }

    /// <summary>The part's current reading (with its sparkline), only while live and while the device reports.</summary>
    private DiveGauge? LiveGauge(SceneNode? node, SceneLayer? layer) =>
        node is null || layer is null || IsHistorical || node.Health is SceneHealth.Offline or SceneHealth.Stale
            ? null
            : DiveViewModel.Gauge(node, layer, dive?.Trends);

    private static bool IsSyncthingRule(string ruleId) => ruleId is "service.syncthing_errors" or "service.syncthing_behind";

    private string DeviceName(string peerId) => environment?.Scene.Find(peerId)?.Label ?? peerId;

    private static string RuleTitle(AlertDto alert) =>
        AlertPreferencesViewModel.KnownRuleKinds.FirstOrDefault(k => k.RuleId == alert.RuleId) is { Title: { } known } ? known : alert.Title;

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
