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

    [ObservableProperty]
    private bool isAvailable;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? outcomeText;

    [ObservableProperty]
    private bool outcomeIsFailure;

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
                RunCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public ObservableCollection<ControlActionRow> Actions { get; } = [];

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
        Actions.Clear();
        OutcomeText = null;
        OutcomeIsFailure = false;
        var source = connection.Source as IControlSource;
        IsAvailable = source is not null && selected is { Kind: SceneNodeKind.Peer };
        if (IsAvailable && source is not null)
        {
            Actions.Add(Row(source, ControlActionKind.PauseCollection, "Pause collection for 1 hour", PauseFor));
            Actions.Add(Row(source, ControlActionKind.ResumeCollection, "Resume collection", null));
            Actions.Add(Row(source, ControlActionKind.MuteAlerts, "Mute alerts for 1 hour", MuteFor));
        }

        Notify();
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
        var text = tier.RequiresPresence
            ? tier.PresenceAvailable
                ? $"{tier.Tier} tier: asks for your fingerprint"
                : $"{tier.Tier} tier: asks for your fingerprint, which this device cannot give"
            : $"{tier.Tier} tier: no fingerprint needed";
        return new ControlActionRow(kind, title, duration, text, tier.RequiresPresence);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(HistoryNotice));
        RunCommand.NotifyCanExecuteChanged();
    }
}
