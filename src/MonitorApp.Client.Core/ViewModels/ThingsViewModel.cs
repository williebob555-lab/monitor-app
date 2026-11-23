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

/// <summary>A part shown inside its card ("Memory", "Network adapter wlan0") with its own live readings and values.</summary>
/// <param name="Detail">A one-line summary for the card: its first readings, or an identifying value such as the MAC address.</param>
public sealed record ThingPartRow(string Id, string Name, string KindLabel, string KindId, string? Detail, IReadOnlyList<ThingValueRow> Values)
{
    public bool HasValues => Values.Count > 0;

    /// <summary>The kind under the name, left out when it only repeats the name ("Memory", "Memory").</summary>
    public string? KindText => string.Equals(KindLabel, Name, StringComparison.OrdinalIgnoreCase) ? null : KindLabel;
}

/// <summary>One thing as a card: name, what kind it is, where it is in its life, its values with evidence, and its parts.</summary>
public sealed record ThingCard(
    string Id,
    string Name,
    string KindLabel,
    string LifecycleText,
    bool Monitored,
    string? InsideName,
    IReadOnlyList<ThingValueRow> Values,
    IReadOnlyList<ThingPartRow> Parts,
    string KindId = "thing")
{
    public bool HasValues => Values.Count > 0;

    public bool HasParts => Parts.Count > 0;

    public string PartsHeading => Parts.Count == 1 ? "1 part" : $"{Parts.Count} parts";

    /// <summary>Only things with parts can be opened into them.</summary>
    public bool CanOpen => Parts.Count > 0;

    public string OpenLabel => $"Open {Name}";

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

    /// <summary>The thing opened into its parts ("open the machine"), or null on the card grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsGrid), nameof(OpenItems))]
    private ThingCard? openCard;

    public bool IsOpen => OpenCard is not null;

    /// <summary>What the opened view draws: the thing itself first, then each of its parts.</summary>
    public IReadOnlyList<object> OpenItems => OpenCard is { } card ? [card, .. card.Parts] : [];

    public bool IsGrid => OpenCard is null;

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

    /// <summary>Opens a thing into its parts, each with its own live readings.</summary>
    [RelayCommand]
    private void Open(ThingCard? card) => OpenCard = card is { CanOpen: true } ? card : null;

    [RelayCommand]
    private void Close() => OpenCard = null;

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
            .Select(t =>
            {
                var parts = t.PartIds.Where(byId.ContainsKey).Select(id => byId[id]).OrderBy(PartOrder).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                // Readings a part claims (by its signal prefix) are shown on that part, not repeated on the device.
                var claimed = parts.Select(p => p.SignalPrefix).OfType<string>().ToArray();
                return new ThingCard(
                    t.Id,
                    t.Name,
                    t.KindLabel,
                    LifecycleText(t.Lifecycle),
                    t.Monitored,
                    null,
                    [.. Readings(t.ReadingSubjects, bySubject, id => !claimed.Any(c => id.StartsWith(c, StringComparison.Ordinal))), .. Facts(t)],
                    parts.Select(p => ToPart(p, bySubject)).ToArray(),
                    t.KindId);
            })
            .ToArray();
    }

    private static IEnumerable<ThingValueRow> Facts(ThingDto thing) =>
        thing.Facts.Where(f => f.Key != "identity.peer-key-fingerprint").Select(ToRow);

    private static IEnumerable<ThingValueRow> Readings(
        IReadOnlyList<string> subjects, Dictionary<string, List<HistoryPointDto>> bySubject, Func<string, bool> include)
    {
        foreach (var subject in subjects)
        {
            if (!bySubject.TryGetValue(subject, out var points))
            {
                continue;
            }

            foreach (var point in points.Where(p => include(p.SignalId) && SignalCatalog.Describe(p.SignalId).IsKnown).OrderBy(p => Order(p.SignalId)))
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

    /// <summary>Parts in the order a person reads a machine: processor, memory, storage, battery, network, then the rest.</summary>
    private static int PartOrder(ThingDto part) => part.KindId switch
    {
        "part.processor" => 0,
        "part.memory" => 1,
        "part.storage" or "part.drive" => 2,
        "part.battery" => 3,
        _ when part.KindId.StartsWith("part.network", StringComparison.Ordinal) => 4,
        _ => 5,
    };

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

    private static ThingPartRow ToPart(ThingDto part, Dictionary<string, List<HistoryPointDto>> bySubject)
    {
        ThingValueRow[] values = part.SignalPrefix is { } prefix
            ? [.. Readings(part.ReadingSubjects, bySubject, id => id.StartsWith(prefix, StringComparison.Ordinal)), .. Facts(part)]
            : [.. Readings(part.ReadingSubjects, bySubject, _ => true), .. Facts(part)];
        var detail = values.Length == 0 ? null : string.Join(" · ", values.Take(2).Select(v => $"{v.Label} {v.Value}"));
        return new ThingPartRow(part.Id, part.Name, part.KindLabel, part.KindId, detail, values);
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

        // Keep an opened thing open across refreshes, with its new readings.
        if (OpenCard is { } open)
        {
            OpenCard = all.FirstOrDefault(c => c.Id == open.Id);
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
