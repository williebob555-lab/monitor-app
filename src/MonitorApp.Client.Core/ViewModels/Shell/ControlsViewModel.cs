using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>One control the person can ask a paired device to perform, with what sending it will ask of them.</summary>
public sealed record ControlActionRow(ControlActionKind Kind, string Title, TimeSpan? Duration, string TierText, bool RequiresPresence)
{
    public string AccessibleText => $"{Title}. {TierText}";
}

/// <summary>
/// A sampling pace the person can ask a paired computer for. <see cref="Interval"/> null restores the computer's own default. The computer
/// accepts 10 s to 1 h (ControlPolicy); faster sampling (about 1 s) happens on its own while someone watches live.
/// </summary>
public sealed record SamplingChoice(TimeSpan? Interval, string Title, string TierText)
{
    public string AccessibleText => Interval is null
        ? $"Restore the default sampling interval. {TierText}"
        : $"Sample every {Title.Replace("Every ", string.Empty, StringComparison.Ordinal)}. {TierText}";
}

/// <summary>An alert the selected computer raised and still holds, which this device can acknowledge.</summary>
public sealed record AlertAckRow(string AlertId, string Title, string Detail, string Severity, string TierText, bool IsAcknowledged)
{
    public string StateText => IsAcknowledged ? "Acknowledged from this device" : $"{Severity} · raised, not resolved";

    public bool CanAcknowledge => !IsAcknowledged;

    public string AccessibleText => $"{Severity} alert: {Title}. {Detail}. {StateText}. {TierText}";
}

/// <summary>A pairing request held by the selected computer, awaiting this device's decision (the fingerprint is what gets confirmed).</summary>
public sealed record RemotePairingRow(string PairingId, string Name, string Fingerprint, string TierText)
{
    public string AccessibleText => $"Pairing request from {Name}, fingerprint {Fingerprint}. {TierText}";
}

/// <summary>
/// The inspector's "Controls" section (UI spec 9, 10, 18): operational actions, kept apart from exploration. Offered only
/// when the data source implements <see cref="IControlSource"/> and a paired device is selected, and never while viewing history.
/// </summary>
public sealed partial class ControlsViewModel : ObservableObject
{
    public const string NotPermittedReason = "not_permitted";

    private static readonly TimeSpan PauseFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan MuteFor = TimeSpan.FromHours(1);

    private readonly NodeConnection connection;
    private readonly EnvironmentViewModel environment;
    private SceneNode? node;

    // The alert list carries no acknowledgement state, so acknowledgements this device made are remembered for the session.
    private readonly HashSet<string> acknowledged = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool isAvailable;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? outcomeText;

    [ObservableProperty]
    private bool outcomeIsFailure;

    [ObservableProperty]
    private RemoteUpdateStatusDto? updateStatus;

    [ObservableProperty]
    private string updateTierText = string.Empty;

    public ControlsViewModel(NodeConnection connection, EnvironmentViewModel environment)
    {
        this.connection = connection;
        this.environment = environment;
        environment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentViewModel.AsOf))
            {
                OnPropertyChanged(nameof(IsHistorical));
                OnPropertyChanged(nameof(CanAct));
                OnPropertyChanged(nameof(HistoryNotice));
                Notify();
            }
        };
    }

    /// <summary>Completes when the update status and pending pairings for the current selection have loaded (tests await this).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public ObservableCollection<RemotePairingRow> PendingPairings { get; } = [];

    public bool HasPendingPairings => PendingPairings.Count > 0;

    /// <summary>A staged, verified update exists on a computer that lets this device see it.</summary>
    public bool HasPendingUpdate => UpdateStatus is { Permitted: true, Available: true, StagedVersion: not null, StagedDigestHex: not null };

    public string? UpdateSummary => HasPendingUpdate
        ? $"Update {UpdateStatus!.StagedVersion} is staged and verified (installed: {UpdateStatus.InstalledVersion})."
        : null;

    /// <summary>
    /// Only when the computer reports this device as permitted: otherwise the fingerprint prompt would fire before a refusal.
    /// </summary>
    public bool CanApproveUpdate => HasPendingUpdate && CanAct;

    partial void OnUpdateStatusChanged(RemoteUpdateStatusDto? value) => Notify();

    public ObservableCollection<ControlActionRow> Actions { get; } = [];

    /// <summary>Sampling paces for the selected computer (10 s, 30 s, 1 min, default); empty when controls do not apply.</summary>
    public ObservableCollection<SamplingChoice> SamplingChoices { get; } = [];

    public bool HasSamplingChoices => SamplingChoices.Count > 0;

    /// <summary>What sending a sampling change asks of the person (one tier for every pace).</summary>
    public string SamplingTierText => SamplingChoices.Count > 0 ? SamplingChoices[0].TierText : string.Empty;

    /// <summary>Unresolved alerts the selected computer raised, when the source can list alerts.</summary>
    public ObservableCollection<AlertAckRow> OpenAlerts { get; } = [];

    public bool HasOpenAlerts => OpenAlerts.Count > 0;

    public bool IsHistorical => environment.IsHistorical;

    /// <summary>False while viewing history or while a command is in flight.</summary>
    public bool CanAct => IsAvailable && !IsHistorical && !IsBusy;

    public string? HistoryNotice => IsAvailable && IsHistorical
        ? "Controls are disabled while viewing history. Return to live to act."
        : null;

    partial void OnIsAvailableChanged(bool value) => Notify();

    partial void OnIsBusyChanged(bool value) => Notify();

    /// <summary>Rebuilds the offered actions for the newly selected node and clears the previous outcome.</summary>
    public void Update(SceneNode? selected)
    {
        node = selected;
        UpdateStatus = null;
        PendingPairings.Clear();
        Actions.Clear();
        SamplingChoices.Clear();
        OpenAlerts.Clear();
        OutcomeText = null;
        OutcomeIsFailure = false;
        var source = connection.Source as IControlSource;
        IsAvailable = source is not null && selected is { Kind: SceneNodeKind.Peer };
        if (IsAvailable && source is not null)
        {
            Actions.Add(Row(source, ControlActionKind.PauseCollection, "Pause collection for 1 hour", PauseFor));
            Actions.Add(Row(source, ControlActionKind.ResumeCollection, "Resume collection", null));
            Actions.Add(Row(source, ControlActionKind.MuteAlerts, "Mute alerts for 1 hour", MuteFor));
            var samplingTier = TierText(source.DescribeTier(ControlActionKind.SetSamplingInterval));
            SamplingChoices.Add(new SamplingChoice(TimeSpan.FromSeconds(10), "Every 10 s", samplingTier));
            SamplingChoices.Add(new SamplingChoice(TimeSpan.FromSeconds(30), "Every 30 s", samplingTier));
            SamplingChoices.Add(new SamplingChoice(TimeSpan.FromMinutes(1), "Every 1 min", samplingTier));
            SamplingChoices.Add(new SamplingChoice(null, "Default", samplingTier));
        }

        LoadTask = IsAvailable ? LoadRemoteAsync(selected!.Id) : Task.CompletedTask;
        Notify();
    }

    private async Task LoadRemoteAsync(string peerId)
    {
        if (connection.Source is not IControlSource source)
        {
            return;
        }

        RemoteUpdateStatusDto? status = null;
        IReadOnlyList<PendingPairingDto> pairings = [];
        IReadOnlyList<AlertDto> alerts = [];
        try
        {
            status = await source.ReadUpdateStatusAsync(peerId);
            pairings = await source.ReadPendingPairingsAsync(peerId);
            if (connection.Source is IAlertSource alertSource)
            {
                alerts = await alertSource.GetAlertsAsync(100);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (LocalApiException)
        {
        }

        if (node?.Id != peerId)
        {
            return; // selection moved on while loading
        }

        UpdateStatus = status;
        UpdateTierText = Row(source, ControlActionKind.ApproveUpdate, string.Empty, null).TierText;
        PendingPairings.Clear();
        foreach (var pairing in pairings.Where(p => p.ApprovalState == "PendingApproval"))
        {
            PendingPairings.Add(new RemotePairingRow(
                pairing.PairingId,
                pairing.DisplayName ?? pairing.RemotePeerId,
                pairing.FingerprintSha256,
                Row(source, ControlActionKind.ApprovePairing, string.Empty, null).TierText));
        }

        var ackTier = TierText(source.DescribeTier(ControlActionKind.AcknowledgeAlert));
        OpenAlerts.Clear();
        foreach (var alert in alerts.Where(a => a.OriginPeerId == peerId && !string.Equals(a.State, "Resolved", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.RaisedAt).Take(5))
        {
            OpenAlerts.Add(new AlertAckRow(alert.AlertId, alert.Title, alert.Detail, alert.Severity, ackTier, acknowledged.Contains(alert.AlertId)));
        }

        Notify();
    }

    [RelayCommand(CanExecute = nameof(CanApproveUpdate))]
    public async Task ApproveUpdateAsync()
    {
        if (node is null || UpdateStatus is not { Permitted: true, StagedVersion: { } version, StagedDigestHex: { } digest }
            || connection.Source is not IControlSource source || environment.IsHistorical)
        {
            return;
        }

        var target = node;
        await Send($"Sending: approve update {version}...", target, () => source.ApproveUpdateAsync(target.Id, version, digest));
        LoadTask = LoadRemoteAsync(target.Id);
    }

    /// <summary>Asks the selected computer to sample at the chosen pace (or its default) until changed again.</summary>
    [RelayCommand]
    public async Task SetSamplingIntervalAsync(SamplingChoice? choice)
    {
        if (choice is null || node is null || !CanAct || connection.Source is not IControlSource source)
        {
            return;
        }

        var target = node;
        await Send(
            choice.Interval is null ? "Sending: restore the default sampling interval..." : $"Sending: sample {choice.Title.ToLowerInvariant()}...",
            target,
            () => source.SetSamplingIntervalAsync(target.Id, choice.Interval, null));
    }

    /// <summary>Acknowledges one of the selected computer's alerts; when applied, the row says so.</summary>
    [RelayCommand]
    public async Task AcknowledgeAlertAsync(AlertAckRow? alert)
    {
        if (alert is null || alert.IsAcknowledged || node is null || !CanAct || connection.Source is not IControlSource source)
        {
            return;
        }

        var target = node;
        ControlOutcomeDto? result = null;
        await Send($"Sending: acknowledge {alert.Title}...", target, async () => result = await source.AcknowledgeAlertAsync(target.Id, alert.AlertId));
        if (result is { IsApplied: true } && node?.Id == target.Id)
        {
            acknowledged.Add(alert.AlertId);
            var index = OpenAlerts.IndexOf(alert);
            if (index >= 0)
            {
                OpenAlerts[index] = alert with { IsAcknowledged = true };
            }
        }
    }

    [RelayCommand]
    public async Task ResolvePairingAsync(RemotePairingRow? pairing)
    {
        await ResolveAsync(pairing, approve: true);
    }

    [RelayCommand]
    public async Task DenyPairingAsync(RemotePairingRow? pairing)
    {
        await ResolveAsync(pairing, approve: false);
    }

    private async Task ResolveAsync(RemotePairingRow? pairing, bool approve)
    {
        if (pairing is null || node is null || !CanAct || connection.Source is not IControlSource source)
        {
            return;
        }

        var target = node;
        await Send(
            $"Sending: {(approve ? "approve" : "deny")} pairing with {pairing.Name}...",
            target,
            () => source.ResolvePairingAsync(target.Id, pairing.PairingId, pairing.Fingerprint, approve));
        LoadTask = LoadRemoteAsync(target.Id);
    }

    private async Task Send(string progress, SceneNode target, Func<Task<ControlOutcomeDto>> send)
    {
        IsBusy = true;
        OutcomeText = progress;
        OutcomeIsFailure = false;
        try
        {
            Describe(await send(), target.Label);
        }
        catch (OperationCanceledException)
        {
            OutcomeText = "Cancelled.";
            OutcomeIsFailure = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task RunAsync(ControlActionRow? action)
    {
        if (action is null || node is null || connection.Source is not IControlSource source || environment.IsHistorical)
        {
            return;
        }

        var target = node;
        IsBusy = true;
        OutcomeText = $"Sending: {action.Title}...";
        OutcomeIsFailure = false;
        try
        {
            var outcome = action.Kind switch
            {
                ControlActionKind.PauseCollection => await source.PauseCollectionAsync(target.Id, action.Duration),
                ControlActionKind.ResumeCollection => await source.ResumeCollectionAsync(target.Id),
                _ => await source.MuteAlertsAsync(target.Id, action.Duration ?? MuteFor, null),
            };
            Describe(outcome, target.Label);
        }
        catch (OperationCanceledException)
        {
            OutcomeText = "Cancelled.";
            OutcomeIsFailure = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The words shown for an outcome. A refusal for lack of permission is the expected default and says what to do.</summary>
    public static string OutcomeMessage(ControlOutcomeDto outcome, string computerName)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (string.Equals(outcome.ReasonCode, NotPermittedReason, StringComparison.Ordinal))
        {
            return $"Not allowed yet: grant this device on {computerName}";
        }

        return outcome.Status switch
        {
            "Applied" => $"Done: {outcome.Message}",
            "PendingLocalApproval" => $"Waiting for approval on {computerName}: {outcome.Message}",
            "Refused" => $"Refused: {outcome.Message}",
            _ => $"Failed: {outcome.Message}",
        };
    }

    private void Describe(ControlOutcomeDto outcome, string computerName)
    {
        OutcomeText = OutcomeMessage(outcome, computerName);
        OutcomeIsFailure = !outcome.IsApplied && outcome.Status != "PendingLocalApproval";
    }

    private static ControlActionRow Row(IControlSource source, ControlActionKind kind, string title, TimeSpan? duration)
    {
        var tier = source.DescribeTier(kind, duration);
        return new ControlActionRow(kind, title, duration, TierText(tier), tier.RequiresPresence);
    }

    /// <summary>What sending an action will ask of the person, said before they act ("Elevated tier: asks for your fingerprint").</summary>
    public static string TierText(ControlTierDescription tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return tier.RequiresPresence
            ? tier.PresenceAvailable
                ? $"{tier.Tier} tier: asks for your fingerprint"
                : $"{tier.Tier} tier: asks for your fingerprint, which this device cannot give"
            : $"{tier.Tier} tier: no fingerprint needed";
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(HistoryNotice));
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(UpdateSummary));
        OnPropertyChanged(nameof(CanApproveUpdate));
        OnPropertyChanged(nameof(HasPendingPairings));
        OnPropertyChanged(nameof(HasSamplingChoices));
        OnPropertyChanged(nameof(SamplingTierText));
        OnPropertyChanged(nameof(HasOpenAlerts));
        RunCommand.NotifyCanExecuteChanged();
        ApproveUpdateCommand.NotifyCanExecuteChanged();
    }
}
