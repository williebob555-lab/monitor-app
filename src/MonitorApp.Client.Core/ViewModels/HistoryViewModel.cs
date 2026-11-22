using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

public enum HistoryPreset
{
    LastHour,
    Last24Hours,
    Last7Days,
    Custom,
}

public sealed record PresetOption(string Label, HistoryPreset Preset)
{
    public override string ToString() => Label;
}

public static class TimeInput
{
    public const string Hint = "ISO 8601, for example 2026-11-21T10:00:00Z. Without a zone, local time is assumed.";

    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.RoundtripKind, out value);
}

public sealed partial class HistoryViewModel : SectionViewModel
{
    private readonly Func<DateTimeOffset> clock;

    [ObservableProperty]
    private PresetOption selectedPreset;

    [ObservableProperty]
    private string customFrom = string.Empty;

    [ObservableProperty]
    private string customTo = string.Empty;

    [ObservableProperty]
    private ChartSeries chart = ChartSeries.Empty;

    [ObservableProperty]
    private string chartNote = "Load history to see a chart.";

    [ObservableProperty]
    private string summary = "Not loaded";

    public HistoryViewModel(NodeConnection connection, Func<DateTimeOffset>? clock = null)
        : base(connection, "History")
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        Presets =
        [
            new PresetOption("Last 1 hour", HistoryPreset.LastHour),
            new PresetOption("Last 24 hours", HistoryPreset.Last24Hours),
            new PresetOption("Last 7 days", HistoryPreset.Last7Days),
            new PresetOption("Custom range", HistoryPreset.Custom),
        ];
        selectedPreset = Presets[1];
    }

    public IReadOnlyList<PresetOption> Presets { get; }

    public SignalPicker Picker { get; } = new();

    public ObservableCollection<ObservationRow> Rows { get; } = [];

    public bool IsCustom => SelectedPreset.Preset == HistoryPreset.Custom;

    public int MaxPages { get; set; } = LocalApiClient.DefaultMaxPages;

    public int PageSize { get; set; } = LocalApiClient.PageSize;

    partial void OnSelectedPresetChanged(PresetOption value) => OnPropertyChanged(nameof(IsCustom));

    /// <summary>Loads the catalog (for the pickers) and then the history for the current selection.</summary>
    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await RunAsync(async () => Picker.Load(await Client.GetCatalogAsync(cancellationToken)));
        if (loaded)
        {
            await LoadAsync(cancellationToken);
        }
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!TryResolveWindow(out var from, out var to, out var problem))
        {
            ErrorMessage = problem;
            return;
        }

        await RunAsync(async () =>
        {
            var result = await Client.GetHistoryAsync(
                new HistoryQuery(from, to, Picker.SubjectFilter, Picker.SignalFilter),
                MaxPages,
                PageSize,
                cancellationToken);
            Rows.Clear();
            foreach (var point in result.Points)
            {
                Rows.Add(ObservationRow.From(point));
            }

            Summary = $"{result.Points.Count} reading(s) from {Format.Time(from)} to {Format.Time(to)} " +
                $"({result.Resolution} resolution, {result.PagesFetched} page(s))." +
                (result.MoreAvailable
                    ? $" Stopped after {MaxPages} pages; more readings exist. Narrow the time range or filters."
                    : string.Empty);
            UpdateChart(result.Points);
        });
    }

    private void UpdateChart(IReadOnlyList<HistoryPointDto> points)
    {
        if (Picker.SubjectFilter is null || Picker.SignalFilter is null)
        {
            Chart = ChartSeries.Empty;
            ChartNote = "Select one subject and one signal to see a chart.";
            return;
        }

        Chart = ChartSeries.Build(points);
        var unit = Picker.SelectedUnit;
        ChartNote = !Chart.HasData
            ? "No numeric readings in this range to chart."
            : $"{Chart.PlottedCount} numeric reading(s){(string.IsNullOrEmpty(unit) ? string.Empty : $" in {unit}")}; " +
                (Chart.GapCount > 0
                    ? $"{Chart.GapCount} unavailable or non-numeric reading(s) shown as gaps."
                    : "no gaps.");
    }

    private bool TryResolveWindow(out DateTimeOffset from, out DateTimeOffset to, out string? problem)
    {
        problem = null;
        to = clock();
        from = to;
        switch (SelectedPreset.Preset)
        {
            case HistoryPreset.LastHour:
                from = to.AddHours(-1);
                return true;
            case HistoryPreset.Last24Hours:
                from = to.AddHours(-24);
                return true;
            case HistoryPreset.Last7Days:
                from = to.AddDays(-7);
                return true;
            default:
                if (!TimeInput.TryParse(CustomFrom, out from) || !TimeInput.TryParse(CustomTo, out to))
                {
                    problem = $"Enter both From and To as dates and times. {TimeInput.Hint}";
                    return false;
                }

                // Range order and the maximum window are validated by the node; its message is shown as-is.
                return true;
        }
    }
}
