using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Current state: the newest reading per subject and signal, with freshness and quality shown.</summary>
public sealed partial class StateViewModel : LiveSectionViewModel
{
    [ObservableProperty]
    private string summary = "Not loaded";

    public StateViewModel(NodeConnection connection)
        : base(connection, "Current state")
    {
    }

    public ObservableCollection<ObservationRow> Rows { get; } = [];

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var state = await Client.GetStateAsync(ct: cancellationToken);
            Rows.Clear();
            foreach (var point in state.Points)
            {
                Rows.Add(ObservationRow.From(point));
            }

            Summary = StateSummary.Describe(state.Points.Count, state.Truncated, state.AsOf);
        });
    }
}

public static class StateSummary
{
    public static string Describe(int count, bool truncated, DateTimeOffset asOf)
    {
        var text = count == 0
            ? $"No readings as of {Format.Time(asOf)}."
            : $"{count} reading(s) as of {Format.Time(asOf)}.";
        return truncated ? text + " The node truncated the list; narrow the filter to see the rest." : text;
    }
}
