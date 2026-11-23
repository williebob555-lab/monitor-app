using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>One value on a card, with how it is known ("Measured", "Entered", ...) and where it came from.</summary>
/// <param name="IsLive">A live reading from monitoring rather than a stored value.</param>
public sealed record ThingValueRow(string Label, string Value, string Evidence, string SourceText, bool IsLive)
{
    /// <summary>The short badge text: "measured", "spec", "entered", "estimated", "imported" or "unknown".</summary>
    public string Badge => Evidence switch
    {
        "Measured" => IsLive ? "measured · live" : "measured",
        "Specified" => "spec",
        "Entered" => "entered",
        "Estimated" => "estimated",
        "Imported" => "imported",
        _ => "unknown",
    };

    /// <summary>The theme brush for the badge. Colour is never the only cue: the badge always carries its word.</summary>
    public string BadgeBrushKey => Evidence switch
    {
        "Measured" => "Brush.Healthy",
        "Specified" or "Imported" => "Brush.Transfer",
        "Entered" => "Brush.Planned",
        "Estimated" => "Brush.Warning",
        _ => "Brush.Muted",
    };
}

/// <summary>A part shown inside its card ("Network adapter wlan0").</summary>
public sealed record ThingPartRow(string Id, string Name, string KindLabel, string? Detail);

/// <summary>One thing as a card: name, what kind it is, where it is in its life, its values with evidence, and its parts.</summary>
public sealed record ThingCard(
    string Id,
    string Name,
    string KindLabel,
    string LifecycleText,
    bool Monitored,
    string? InsideName,
    IReadOnlyList<ThingValueRow> Values,
    IReadOnlyList<ThingPartRow> Parts)
{
    public bool HasValues => Values.Count > 0;

    public bool HasParts => Parts.Count > 0;

    public string PartsHeading => Parts.Count == 1 ? "1 part" : $"{Parts.Count} parts";

    public string Subtitle => InsideName is null ? $"{KindLabel} · {LifecycleText}" : $"{KindLabel} · {LifecycleText} · inside {InsideName}";

    /// <summary>Text used by the filter box.</summary>
    internal string SearchText => string.Join(' ', [Name, KindLabel, LifecycleText, .. Values.Select(v => v.Value), .. Parts.Select(p => p.Name)]);
}

/// <summary>
/// The Things screen (bill of work items 5 and 6): every object the shared model holds as a card, each value tagged measured,
/// spec, entered, estimated or unknown, with live readings joined from monitoring. "Add" takes a few words; the first line is
/// the name and anything after it is a note. Parts (a computer's network adapters) appear inside their card, not as cards.
/// </summary>
public sealed partial class ThingsViewModel : SectionViewModel
{
    private IReadOnlyList<ThingCard> all = [];

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private string newThingText = string.Empty;

    [ObservableProperty]
    private ThingKindDto? newThingKind;

    [ObservableProperty]
    private string? addError;

    [ObservableProperty]
    private bool available = true;

    public ThingsViewModel(NodeConnection connection)
        : base(connection, "Things")
    {
    }

    private IThingsSource? Source => Client as IThingsSource;

    public ObservableCollection<ThingCard> Cards { get; } = [];

    public ObservableCollection<ThingKindDto> Kinds { get; } = [];

    public bool HasCards => Cards.Count > 0;

    public string CountText => all.Count switch
    {
        0 => "Nothing yet.",
        1 => "1 thing",
        var n when Cards.Count == n => $"{n} things",
        var n => $"{Cards.Count} of {n} things",
    };

    public string EmptyText => Available
        ? (all.Count == 0
            ? "Nothing here yet. Your computers appear by themselves within a minute; add anything else above."
            : "Nothing matches the filter.")
        : "This computer's Monitor App is too old to keep things. Update it from Settings, then Updates.";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnAvailableChanged(bool value) => OnPropertyChanged(nameof(EmptyText));

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            Available = false;
            return;
        }

        await RunAsync(async () =>
        {
            var things = await source.GetThingsAsync(cancellationToken);
            if (things is null)
            {
                Available = false;
                all = [];
                ApplyFilter();
                return;
            }

            Available = true;
            IReadOnlyList<HistoryPointDto> readings = [];
            if (things.Things.Any(t => t.ReadingSubjects.Count > 0))
            {
                try
                {
                    readings = (await Client.GetStateAsync(ct: cancellationToken)).Points;
                }
                catch (LocalApiException)
                {
                    // Cards still show what the model knows; live readings come back on the next refresh.
                }
            }

            all = Build(things.Things, readings);
            SyncKinds(things.Kinds);
            ApplyFilter();
        });
    }

    [RelayCommand]
    private Task Reload() => RefreshAsync();

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Source is not { } source)
        {
            return;
        }

        AddError = null;
        if (string.IsNullOrWhiteSpace(NewThingText))
        {
            AddError = "Type a name first. Details can come later.";
            return;
        }

        var text = NewThingText;
        var kind = NewThingKind?.Id;
        try
        {
            await source.AddThingAsync(text, kind);
        }
        catch (ApiValidationException exception)
        {
            AddError = exception.Message;
            return;
        }
        catch (LocalApiException exception)
        {
            AddError = exception.Message;
            return;
        }

        NewThingText = string.Empty;
        StatusMessage = $"Added {FirstLine(text)}.";
        await RefreshAsync();
    }

    /// <summary>Builds the cards: top-level things (not inside another) with their values, readings and parts.</summary>
    public static IReadOnlyList<ThingCard> Build(IReadOnlyList<ThingDto> things, IReadOnlyList<HistoryPointDto> readings)
    {
        var byId = things.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var bySubject = readings
            .GroupBy(r => r.SubjectEntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return things
            .Where(t => t.InsideId is null || !byId.ContainsKey(t.InsideId))
            .Select(t => new ThingCard(
                t.Id,
                t.Name,
                t.KindLabel,
                LifecycleText(t.Lifecycle),
                t.Monitored,
                null,
                [.. Readings(t, bySubject), .. t.Facts.Where(f => f.Key != "identity.peer-key-fingerprint").Select(ToRow)],
                t.PartIds.Where(byId.ContainsKey).Select(id => byId[id]).Select(ToPart).ToArray()))
            .ToArray();
    }

    private static IEnumerable<ThingValueRow> Readings(ThingDto thing, Dictionary<string, List<HistoryPointDto>> bySubject)
    {
        foreach (var subject in thing.ReadingSubjects)
        {
            if (!bySubject.TryGetValue(subject, out var points))
            {
                continue;
            }

            foreach (var point in points.Where(p => SignalCatalog.Describe(p.SignalId).IsKnown).OrderBy(p => Order(p.SignalId)))
            {
                var reading = SignalCatalog.ToReading(point);
                if (!SignalCatalog.IsAvailable(reading))
                {
                    continue;
                }

                var source = point.Freshness == "Stale" ? "monitoring, not recent" : "monitoring";
                yield return new ThingValueRow(reading.Label, reading.DisplayValue, "Measured", source, IsLive: true);
            }
        }
    }

    /// <summary>The facts a person most wants first: processor, memory, storage, then everything else.</summary>
    private static int Order(string signalId) => signalId switch
    {
        SignalIds.CpuUtilization => 0,
        SignalIds.MemoryTotal => 1,
        SignalIds.MemoryUsed => 2,
        SignalIds.StorageTotal => 3,
        SignalIds.StorageFree => 4,
        SignalIds.BatteryLevel => 5,
        _ => 10,
    };

    private static ThingValueRow ToRow(ThingFactDto fact) =>
        new(fact.Label, fact.Value, fact.Evidence, fact.Others > 0 ? $"{fact.Source} (+{fact.Others} other)" : fact.Source, IsLive: false);

    private static ThingPartRow ToPart(ThingDto part)
    {
        var mac = part.Facts.FirstOrDefault(f => f.Key == "identity.lan.interface.mac");
        return new ThingPartRow(part.Id, part.Name, part.KindLabel, mac is null ? null : $"{mac.Label} {mac.Value}");
    }

    internal static string LifecycleText(string lifecycle) => lifecycle switch
    {
        "Idea" => "idea",
        "Candidate" => "considering",
        "Planned" => "planned",
        "Ordered" => "ordered",
        "Owned" => "owned",
        "Installed" => "in use",
        "Retired" => "retired",
        "Disposed" => "gone",
        _ => lifecycle.ToLowerInvariant(),
    };

    private void SyncKinds(IReadOnlyList<ThingKindDto> kinds)
    {
        if (Kinds.Select(k => k.Id).SequenceEqual(kinds.Select(k => k.Id)))
        {
            return;
        }

        var selected = NewThingKind?.Id;
        Kinds.Clear();
        foreach (var kind in kinds)
        {
            Kinds.Add(kind);
        }

        NewThingKind = Kinds.FirstOrDefault(k => k.Id == selected) ?? Kinds.FirstOrDefault();
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        Cards.Clear();
        foreach (var card in all.Where(c => filter.Length == 0 || c.SearchText.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
        {
            Cards.Add(card);
        }

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }
}
