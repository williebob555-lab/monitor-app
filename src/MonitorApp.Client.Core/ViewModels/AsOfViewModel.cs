using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Reconstructs the latest known reading per signal as of a chosen past moment.</summary>
public sealed partial class AsOfViewModel : SectionViewModel
{
    [ObservableProperty]
    private string asOfText;

    [ObservableProperty]
    private string lookbackHoursText = "24";

    [ObservableProperty]
    private string summary = "Choose a moment and load.";

    public AsOfViewModel(NodeConnection connection, Func<DateTimeOffset>? clock = null)
        : base(connection, "As of")
    {
        asOfText = (clock ?? (() => DateTimeOffset.UtcNow))().ToLocalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    public SignalPicker Picker { get; } = new();

    public ObservableCollection<ObservationRow> Rows { get; } = [];

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
        if (!TimeInput.TryParse(AsOfText, out var asOf))
        {
            ErrorMessage = $"Enter the as-of moment as a date and time. {TimeInput.Hint}";
            return;
        }

        if (!double.TryParse(LookbackHoursText, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            || !double.IsFinite(hours) || hours <= 0 || hours > 24 * 31)
        {
            ErrorMessage = "Lookback must be a number of hours greater than 0 and at most 744.";
            return;
        }

        await RunAsync(async () =>
        {
            var state = await Client.GetStateAsync(
                asOf,
                (int)Math.Ceiling(hours * 3600),
                Picker.SubjectFilter,
                Picker.SignalFilter,
                cancellationToken);
            Rows.Clear();
            foreach (var point in state.Points)
            {
                Rows.Add(ObservationRow.From(point));
            }

            Summary = StateSummary.Describe(state.Points.Count, state.Truncated, state.AsOf) +
                $" Looking back to {Format.Time(state.LookbackFrom)}; signals with no reading in that window are absent.";
        });
    }
}
