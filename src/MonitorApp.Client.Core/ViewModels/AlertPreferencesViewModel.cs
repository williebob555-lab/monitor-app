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
/// <see cref="IControlSource"/>. The form loads the stored choice from the first trusted computer that answers (defaults when
/// none answers) and Save replaces what each trusted computer holds for this device.
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
        ("service.tailscale_disconnected", "Tailscale is not connected"),
        ("service.syncthing_errors", "Syncthing folder has errors"),
        ("service.syncthing_behind", "Syncthing folders not in sync"),
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
        "Choose which alerts your trusted computers send to this device. Save replaces the choice on each trusted computer. " +
        "Only this device's choice changes.";

    [ObservableProperty]
    private string? loadedFrom;

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        OnPropertyChanged(nameof(IsSupported));
        if (Client is not IControlSource source)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var computers = (await Client.GetPeersAsync()).Where(p => string.Equals(p.TrustState, "Active", StringComparison.Ordinal));
            foreach (var computer in computers)
            {
                var stored = await source.ReadAlertPreferencesAsync(computer.PeerId, cancellationToken);
                if (stored is null)
                {
                    continue;
                }

                Apply(stored);
                LoadedFrom = $"Loaded from {computer.DisplayName ?? computer.PeerId}";
                return;
            }

            LoadedFrom = "No trusted computer answered; showing the defaults.";
        });
    }

    /// <summary>Shows a stored choice: unknown severities fall back to all, unknown rule ids are kept so Save preserves them.</summary>
    public void Apply(AlertPreferencesDto stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        MinSeverity = SeverityChoices.FirstOrDefault(c => string.Equals(c, stored.MinSeverity, StringComparison.OrdinalIgnoreCase)) ?? AllSeverities;
        foreach (var ruleId in stored.MutedRuleKinds.Where(id => RuleKinds.All(r => r.RuleId != id)))
        {
            RuleKinds.Add(new RuleKindMuteRow(ruleId, ruleId));
        }

        foreach (var row in RuleKinds)
        {
            row.IsMuted = stored.MutedRuleKinds.Contains(row.RuleId, StringComparer.Ordinal);
        }
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
