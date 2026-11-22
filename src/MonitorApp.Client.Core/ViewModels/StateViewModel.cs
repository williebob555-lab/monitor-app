using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Current state: the newest reading per subject and signal, with freshness and quality shown.</summary>
public sealed partial class StateViewModel : SectionViewModel
{
    [ObservableProperty]
    private string summary = "Not loaded";

    public StateViewModel(NodeConnection connection)
        : base(connection, "Current state")
    {
    }

    [ObservableProperty]
    private string updateMode = "Manual refresh";

    public ObservableCollection<ObservationRow> Rows { get; } = [];

    /// <summary>
    /// Keeps the table current until cancelled: re-reads on each push from a source that supports live updates,
    /// otherwise polls every <paramref name="pollInterval"/>. Callers run it while the view is visible.
    /// </summary>
    public async Task RunLiveAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var source = Client;
        try
        {
            if (source.SupportsLiveUpdates)
            {
                var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
                void OnChanged(object? sender, EventArgs args) => signal.Writer.TryWrite(true);
                source.StateChanged += OnChanged;
                try
                {
                    UpdateMode = "Live";
                    await RefreshAsync(cancellationToken);
                    while (await signal.Reader.WaitToReadAsync(cancellationToken))
                    {
                        signal.Reader.TryRead(out _);
                        await RefreshAsync(cancellationToken);
                    }
                }
                finally
                {
                    source.StateChanged -= OnChanged;
                }
            }
            else
            {
                UpdateMode = $"Polling every {pollInterval.TotalSeconds:0.#}s";
                await RefreshAsync(cancellationToken);
                using var timer = new PeriodicTimer(pollInterval);
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await RefreshAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop when the view is hidden or the window closes.
        }
        finally
        {
            UpdateMode = "Manual refresh";
        }
    }

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
