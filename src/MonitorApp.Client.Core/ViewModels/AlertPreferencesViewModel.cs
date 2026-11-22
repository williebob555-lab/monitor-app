using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>One rule kind this device can opt out of, with a checkbox.</summary>
public sealed partial class RuleKindMuteRow(string ruleId, string title) : ObservableObject
{
    [ObservableProperty]
    private bool isMuted;

    public string RuleId { get; } = ruleId;

    public string Title { get; } = title;
}

/// <summary>
/// "Alerts on this device": which alerts the paired computers send to this phone (T049). Offered only when the source is an
/// <see cref="IControlSource"/>. There is no read method for the stored preferences on that interface, so the form starts at
/// the defaults (all severities, nothing muted) and Save replaces what each trusted computer holds for this device.
/// </summary>
public sealed partial class AlertPreferencesViewModel : SectionViewModel
{
    public const string AllSeverities = "All";

    /// <summary>The severity names the API accepts ("All" means every severity).</summary>
    public static IReadOnlyList<string> SeverityChoices { get; } = [AllSeverities, "Info", "Warning", "Critical"];

    /// <summary>The rule ids the peers raise today (AlertRulesConfig); the API takes any id, these are the known ones.</summary>
    public static IReadOnlyList<(string RuleId, string Title)> KnownRuleKinds { get; } =
    [
        ("threshold.disk_free", "Low free disk space"),
        ("threshold.cpu_high", "Sustained high CPU"),
        ("peer.silent", "A paired device went silent"),
        ("observation.unavailable", "A reading became unavailable"),
    ];

    [ObservableProperty]
    private string minSeverity = AllSeverities;

    [ObservableProperty]
    private bool statusIsFailure;

    public AlertPreferencesViewModel(NodeConnection connection)
        : base(connection, "Alerts on this device")
    {
        foreach (var (ruleId, title) in KnownRuleKinds)
        {
            RuleKinds.Add(new RuleKindMuteRow(ruleId, title));
        }
    }

    public ObservableCollection<RuleKindMuteRow> RuleKinds { get; } = [];

    public bool IsSupported => Client is IControlSource;

    public static string Explanation =>
        "Choose which alerts your trusted computers send to this device. The current stored choice cannot be read back here, " +
        "so this starts from the defaults; Save replaces it on each trusted computer. Only this device's choice changes.";

    public override Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        OnPropertyChanged(nameof(IsSupported));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Client is not IControlSource source)
        {
            return;
        }

        StatusMessage = null;
        StatusIsFailure = false;
        var muted = RuleKinds.Where(r => r.IsMuted).Select(r => r.RuleId).ToArray();
        var severity = MinSeverity == AllSeverities ? null : MinSeverity;
        await RunAsync(async () =>
        {
            var computers = (await Client.GetPeersAsync()).Where(p => string.Equals(p.TrustState, "Active", StringComparison.Ordinal)).ToList();
            if (computers.Count == 0)
            {
                StatusMessage = "No trusted computer to send this to. Pair one first.";
                StatusIsFailure = true;
                return;
            }

            var lines = new List<string>();
            foreach (var computer in computers)
            {
                var name = computer.DisplayName ?? computer.PeerId;
                var outcome = await source.SetAlertPreferencesAsync(computer.PeerId, severity, muted);
                lines.Add(computers.Count == 1 ? ControlsViewModel.OutcomeMessage(outcome, name) : $"{name}: {ControlsViewModel.OutcomeMessage(outcome, name)}");
                StatusIsFailure |= !outcome.IsApplied && outcome.Status != "PendingLocalApproval";
            }

            StatusMessage = string.Join(Environment.NewLine, lines);
        });
    }
}
