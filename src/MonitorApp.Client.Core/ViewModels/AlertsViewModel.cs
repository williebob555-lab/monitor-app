using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

/// <summary>Active and recent alerts held by this peer (desktop: its alert store; phone: its received inbox).</summary>
public sealed partial class AlertsViewModel : LiveSectionViewModel
{
    [ObservableProperty]
    private string summary = "Not loaded";

    public AlertsViewModel(NodeConnection connection)
        : base(connection, "Alerts")
    {
    }

    public ObservableCollection<AlertRow> Alerts { get; } = [];

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

            var active = Alerts.Count(row => row.IsActive);
            Summary = Alerts.Count == 0 ? "No alerts." : $"{active} active, {Alerts.Count - active} resolved.";
        });
    }
}
