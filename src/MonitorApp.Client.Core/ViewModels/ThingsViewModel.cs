using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>One value on a card, with how it is known ("Measured", "Entered", ...) and where it came from.</summary>
/// <param name="IsLive">A live reading from monitoring rather than a stored value.</param>
/// <param name="Category">The heading the value is listed under: "About" for what is known about the thing, otherwise the kind of reading ("System", "Network", "Power").</param>
public sealed record ThingValueRow(string Label, string Value, string Evidence, string SourceText, bool IsLive, string Category = ThingValueGroup.About)
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

/// <summary>
/// One heading of a thing's values ("System · 4"). A computer can report dozens of readings; grouping them keeps the card short,
/// and big groups start folded.
/// </summary>
public sealed record ThingValueGroup(string Title, IReadOnlyList<ThingValueRow> Values, bool StartsOpen)
{
    public const string About = "About";
    public const string System = "System";
    public const string Network = "Network";
    public const string Power = "Power";
    public const string Monitoring = "Monitoring";
    public const string Other = "Other readings";

    /// <summary>Groups larger than this start folded when the thing has many values.</summary>
    public const int FoldAbove = 4;

    private static readonly string[] Order = [About, System, Network, Power, Other, Monitoring];

    public string Header => $"{Title.ToUpperInvariant()} · {Values.Count}";

    public static IReadOnlyList<ThingValueGroup> Of(IReadOnlyList<ThingValueRow> values)
    {
        var many = values.Count > 6;
        return values
            .GroupBy(v => v.Category, StringComparer.Ordinal)
            .OrderBy(g => Array.IndexOf(Order, g.Key) is var i && i < 0 ? Order.Length : i)
            .Select(g => g.ToArray())
            .Select(g => new ThingValueGroup(g[0].Category, g, g[0].Category == About || !many || g.Length <= FoldAbove))
            .ToArray();
    }

    /// <summary>The heading for a live reading, from what the signal measures.</summary>
    public static string ForReading(SceneLayerKind kind) => kind switch
    {
        SceneLayerKind.Compute or SceneLayerKind.Memory or SceneLayerKind.Storage => System,
        SceneLayerKind.Network or SceneLayerKind.Connectivity => Network,
        SceneLayerKind.Power => Power,
        SceneLayerKind.Collection => Monitoring,
        _ => Other,
    };
}

/// <summary>A part shown inside its card ("Memory", "Network adapter wlan0") with its own live readings and values.</summary>
/// <param name="Detail">A one-line summary for the card: its first readings, or an identifying value such as the MAC address.</param>
/// <param name="LiveNodeId">The device in the Ecosystem this part's readings come from, when it has any.</param>
/// <param name="LiveLayerId">The Ecosystem layer that holds those readings ("memory", "connectivity").</param>
public sealed record ThingPartRow(
    string Id, string Name, string KindLabel, string KindId, string? Detail, IReadOnlyList<ThingValueRow> Values,
    string? LiveNodeId = null, string? LiveLayerId = null)
{
    public bool HasValues => Values.Count > 0;

    public bool CanSeeLive => LiveNodeId is not null;

    /// <summary>The kind under the name, left out when it only repeats the name ("Memory", "Memory").</summary>
    public string? KindText => string.Equals(KindLabel, Name, StringComparison.OrdinalIgnoreCase) ? null : KindLabel;
}

/// <summary>A lifecycle stage offered while editing ("Owned" shown as "owned").</summary>
public sealed record ThingStage(string Id, string Text);

/// <summary>
/// A connection path in a card: the route ("Laptop USB-C → USB-C cable → Monitor USB-C in"), the verdict in words and the
/// reasons, problems first. <see cref="Tone"/> is "problem", "unknown" or "ok" for styling.
/// </summary>
public sealed record ThingWiringRow(string Route, string VerdictText, string Tone, IReadOnlyList<string> Reasons)
{
    public bool IsProblem => Tone == "problem";

    public bool IsUnknown => Tone == "unknown";

    public bool IsOk => Tone == "ok";

    public bool HasReasons => Reasons.Count > 0;
}

/// <summary>
/// Something a setup still needs, as the "What's missing" list shows it. <see cref="Tone"/> is "problem" when values show
/// it is needed, "unknown" when the app only sees a gap. <see cref="About"/> names the things involved.
/// </summary>
public sealed record ThingMissingRow(string Need, string Explanation, string Tone, string About)
{
    public bool IsProblem => Tone == "problem";

    public bool HasAbout => About.Length > 0;
}

/// <summary>A port a connection can start from or go to; <see cref="ThingId"/> is null for this thing's own ports.</summary>
public sealed class ThingPortChoice(string? ThingId, string PortId, string Text)
{
    public string? ThingId { get; } = ThingId;

    public string PortId { get; } = PortId;

    public string Text { get; } = Text;
}

/// <summary>A place a thing can be put: another thing, or nowhere (Id null: on its own).</summary>
public sealed record ThingPlace(string? Id, string Name);

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
    string KindId = "thing",
    string? LiveNodeId = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Links = null,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyList<ThingWiringRow>? Wiring = null)
{
    /// <summary>Its ports in words ("DC out · DC barrel 5.5/2.1 mm plug · output · 9 V → Delay DC in").</summary>
    public IReadOnlyList<string> PortTexts => Ports ?? [];

    public bool HasPorts => Ports is { Count: > 0 };

    /// <summary>Each connection path at this thing with the connection engine's verdict and why.</summary>
    public IReadOnlyList<ThingWiringRow> WiringRows => Wiring ?? [];

    public bool HasWiring => Wiring is { Count: > 0 };

    /// <summary>Its links in words ("Powered by Studio PSU").</summary>
    public IReadOnlyList<string> LinkTexts => Links ?? [];

    public bool HasLinks => Links is { Count: > 0 };

    public string TagsText => Tags is { Count: > 0 } tags ? string.Join("  ", tags.Select(t => "#" + t)) : string.Empty;

    public bool HasTags => Tags is { Count: > 0 };

    /// <summary>Monitored things can be seen live in the Ecosystem.</summary>
    public bool CanSeeLive => LiveNodeId is not null;

    public bool HasValues => Values.Count > 0;

    /// <summary>The values under their headings (About, System, Network, Power, ...).</summary>
    public IReadOnlyList<ThingValueGroup> Groups => ThingValueGroup.Of(Values);

    public bool HasParts => Parts.Count > 0;

    public string PartsHeading => Parts.Count == 1 ? "1 part" : $"{Parts.Count} parts";

    /// <summary>Only things with parts can be opened into them.</summary>
    public bool CanOpen => Parts.Count > 0;

    public string OpenLabel => $"Open {Name}";

    public string Subtitle => InsideName is null ? $"{KindLabel} · {LifecycleText}" : $"{KindLabel} · {LifecycleText} · inside {InsideName}";

    /// <summary>Everything the card shows, so a refresh that changed nothing can leave the screen alone.</summary>
    internal string Signature => string.Join('\u001f', [
        Id, Name, Subtitle, KindId, LiveNodeId ?? string.Empty, TagsText, .. LinkTexts, .. PortTexts,
        .. WiringRows.Select(w => $"{w.Route}/{w.VerdictText}/{string.Join(';', w.Reasons)}"),
        .. Values.Select(v => $"{v.Label}={v.Value}/{v.Badge}/{v.SourceText}"),
        .. Parts.Select(p => $"{p.Id}:{p.Name}:{p.KindId}:{p.LiveNodeId}:{p.LiveLayerId}:" + string.Join(';', p.Values.Select(v => $"{v.Label}={v.Value}/{v.Badge}/{v.SourceText}")))]);

    /// <summary>Text used by the filter box.</summary>
    internal string SearchText => string.Join(' ', [Name, KindLabel, LifecycleText, TagsText, .. Values.Select(v => v.Value), .. Parts.Select(p => p.Name)]);
}

/// <summary>
/// The Things screen (bill of work items 5 and 6): every object the shared model holds as a card, each value tagged measured,
/// spec, entered, estimated or unknown, with live readings joined from monitoring. "Add" takes a few words; the first line is
/// the name and anything after it is a note. Parts (a computer's network adapters) appear inside their card, not as cards.
/// </summary>
public sealed partial class ThingsViewModel : SectionViewModel
{
    private IReadOnlyList<ThingCard> all = [];
    private Dictionary<string, ThingDto> things = new(StringComparer.Ordinal);
    private ThingDto? editing;

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

    /// <summary>
    /// The time the timeline is set to, or null for now. In the past the screen shows what the model held then (which things
    /// existed, what sat inside what, their values) with the readings recorded then; adding and editing are off.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistorical), nameof(CanChange), nameof(RecordedText))]
    private DateTimeOffset? asOf;

    public bool IsHistorical => AsOf is not null;

    /// <summary>History is observational: nothing is added or edited while looking back.</summary>
    public bool CanChange => AsOf is null;

    public string RecordedText => AsOf is { } at
        ? "RECORDED · as of " + at.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.CurrentCulture) + ". This is what you had then; return to live to add or edit."
        : string.Empty;

    /// <summary>The thing opened into its parts ("open the machine"), or null on the card grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsGrid), nameof(OpenItems))]
    private ThingCard? openCard;

    /// <summary>The thing being edited (its name, kind, note and where it sits), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private string? editingName;

    [ObservableProperty]
    private string editName = string.Empty;

    [ObservableProperty]
    private ThingKindDto? editKind;

    [ObservableProperty]
    private string editNote = string.Empty;

    [ObservableProperty]
    private ThingPlace? editInside;

    [ObservableProperty]
    private string? editError;

    [ObservableProperty]
    private ThingStage? editStage;

    /// <summary>Tags, separated by commas or spaces ("studio, spare").</summary>
    [ObservableProperty]
    private string editTags = string.Empty;

    [ObservableProperty]
    private string newValueLabel = string.Empty;

    [ObservableProperty]
    private string newValueText = string.Empty;

    [ObservableProperty]
    private bool newValueFromSpec;

    [ObservableProperty]
    private ThingKindDto? newLinkType;

    [ObservableProperty]
    private ThingPlace? newLinkTarget;

    private readonly List<string> removedLinks = [];

    private readonly List<string> removedPorts = [];

    private readonly List<string> unplugged = [];

    private ILookup<string, ThingWiringDto> wiring = Array.Empty<ThingWiringDto>().ToLookup(w => w.ThingId, StringComparer.Ordinal);

    /// <summary>The sentinel "from" choice for the port being added in the same edit.</summary>
    internal const string NewPortChoice = "@new";

    [ObservableProperty]
    private string newPortName = string.Empty;

    [ObservableProperty]
    private ConnectorChoiceDto? newPortConnector;

    [ObservableProperty]
    private string? newPortGender;

    [ObservableProperty]
    private string? newPortDirection;

    /// <summary>What the port carries, separated by commas ("usb-3, usb-pd" or "analog-audio").</summary>
    [ObservableProperty]
    private string newPortSignals = string.Empty;

    [ObservableProperty]
    private string newPortVoltage = string.Empty;

    [ObservableProperty]
    private string newPortPolarity = string.Empty;

    [ObservableProperty]
    private string newPortLevel = string.Empty;

    /// <summary>Current: what an output can supply, or what an input draws.</summary>
    [ObservableProperty]
    private string newPortCurrent = string.Empty;

    [ObservableProperty]
    private bool newPortFromSpec;

    [ObservableProperty]
    private ThingPortChoice? connectFrom;

    [ObservableProperty]
    private ThingPortChoice? connectTo;

    /// <summary>The ports of the thing being edited; removing or unplugging one here takes effect on Save.</summary>
    public ObservableCollection<ThingPortDto> EditPorts { get; } = [];

    /// <summary>Connectors from the shared connector library, for a new port.</summary>
    public ObservableCollection<ConnectorChoiceDto> Connectors { get; } = [];

    public IReadOnlyList<string> Genders { get; } = ["Plug", "Socket", "Genderless"];

    public IReadOnlyList<string> Directions { get; } = ["Output", "Input", "Both"];

    /// <summary>This thing's ports a connection can start from (plus the one being added).</summary>
    public ObservableCollection<ThingPortChoice> ConnectFromChoices { get; } = [];

    /// <summary>Every other thing's free ports, to plug into.</summary>
    public ObservableCollection<ThingPortChoice> ConnectToChoices { get; } = [];

    /// <summary>The links on the thing being edited; removing one here takes effect on Save.</summary>
    public ObservableCollection<ThingLinkDto> EditLinks { get; } = [];

    /// <summary>Kinds of link the node offers ("Depends on", "Powered by", "Connected to", ...).</summary>
    public ObservableCollection<ThingKindDto> LinkTypes { get; } = [];

    /// <summary>Things the edited one can link to: every other thing.</summary>
    public ObservableCollection<ThingPlace> LinkTargets { get; } = [];

    /// <summary>Every lifecycle stage; the model refuses moves that make no sense (an owned thing back to an idea).</summary>
    public IReadOnlyList<ThingStage> Stages { get; } =
        [.. new[] { "Idea", "Candidate", "Planned", "Ordered", "Owned", "Installed", "Retired", "Disposed" }.Select(id => new ThingStage(id, LifecycleText(id)))];

    public bool IsEditing => EditingName is not null;

    /// <summary>Kinds offered while editing: the usual ones plus the thing's own kind when it is not among them.</summary>
    public ObservableCollection<ThingKindDto> EditKinds { get; } = [];

    /// <summary>Where the thing can go: on its own, or inside any other thing that is not one of its own parts.</summary>
    public ObservableCollection<ThingPlace> EditPlaces { get; } = [];

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

    /// <summary>What the things on record still need (T069): power, cables, adapters, more current. Empty in the past.</summary>
    public ObservableCollection<ThingMissingRow> Missing { get; } = [];

    public bool HasMissing => Missing.Count > 0;

    public string MissingTitle => Missing.Count == 1 ? "What's missing: 1 thing" : $"What's missing: {Missing.Count} things";

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

    partial void OnAsOfChanged(DateTimeOffset? value)
    {
        if (value is not null)
        {
            CancelEdit();
        }
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            Available = false;
            return;
        }

        await RunAsync(async () =>
        {
            var asOf = AsOf;
            var things = await source.GetThingsAsync(asOf, cancellationToken);
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
                    readings = (await Client.GetStateAsync(asOf, ct: cancellationToken)).Points;
                }
                catch (LocalApiException)
                {
                    // Cards still show what the model knows; live readings come back on the next refresh.
                }
            }

            this.things = things.Things.ToDictionary(t => t.Id, StringComparer.Ordinal);
            wiring = (things.Wiring ?? []).ToLookup(w => w.ThingId, StringComparer.Ordinal);
            if (things.Connectors is { } connectors && Connectors.Count != connectors.Count)
            {
                Connectors.Clear();
                foreach (var connector in connectors)
                {
                    Connectors.Add(connector);
                }
            }

            all = Build(things.Things, readings, wiring, Connectors);
            SyncMissing(things.Missing ?? []);
            SyncKinds(things.Kinds);
            if (things.LinkTypes is { } linkTypes && !LinkTypes.Select(k => k.Id).SequenceEqual(linkTypes.Select(k => k.Id)))
            {
                LinkTypes.Clear();
                foreach (var linkType in linkTypes)
                {
                    LinkTypes.Add(linkType);
                }
            }
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

    /// <summary>Asks the shell to show a thing (or one of its parts) live in the Ecosystem: the same object, another lens.</summary>
    public event EventHandler<Shell.BreadcrumbItem>? DiveRequested;

    [RelayCommand]
    private void SeeLive(object? target)
    {
        switch (target)
        {
            case ThingCard { LiveNodeId: { } node } card:
                DiveRequested?.Invoke(this, new Shell.BreadcrumbItem(card.Name, node));
                break;
            case ThingPartRow { LiveNodeId: { } node } part:
                DiveRequested?.Invoke(this, new Shell.BreadcrumbItem(part.Name, node, false, part.LiveLayerId));
                break;
        }
    }

    /// <summary>Opens the thing an Ecosystem node stands for (a computer seen in the Dive), refreshing first.</summary>
    public async Task OpenNodeAsync(string nodeId)
    {
        await RefreshAsync();
        FilterText = string.Empty;
        OpenCard = all.FirstOrDefault(c => c.LiveNodeId == nodeId && c.CanOpen) ?? all.FirstOrDefault(c => c.LiveNodeId == nodeId);
        if (OpenCard is { CanOpen: false })
        {
            // Nothing to pull apart: show its card on the grid instead.
            FilterText = OpenCard.Name;
            OpenCard = null;
        }
    }

    /// <summary>Starts editing a card or a part: fills the form with what the person can change.</summary>
    [RelayCommand]
    private void Edit(object? target)
    {
        var id = target switch
        {
            ThingCard card => card.Id,
            ThingPartRow part => part.Id,
            _ => null,
        };
        if (id is null || !CanChange || !things.TryGetValue(id, out var thing))
        {
            return;
        }

        editing = thing;
        EditError = null;
        EditName = thing.Name;
        EditNote = PersonNote(thing) ?? string.Empty;
        EditKinds.Clear();
        if (!Kinds.Any(k => k.Id == thing.KindId))
        {
            EditKinds.Add(new ThingKindDto(thing.KindId, thing.KindLabel));
        }

        foreach (var kind in Kinds)
        {
            EditKinds.Add(kind);
        }

        EditKind = EditKinds.First(k => k.Id == thing.KindId);
        EditPlaces.Clear();
        var nowhere = new ThingPlace(null, "Nothing (on its own)");
        EditPlaces.Add(nowhere);
        var own = DescendantsOf(thing.Id);
        foreach (var place in things.Values
                     .Where(t => t.Id != thing.Id && !own.Contains(t.Id))
                     .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            EditPlaces.Add(new ThingPlace(place.Id, place.Name));
        }

        EditInside = EditPlaces.FirstOrDefault(p => p.Id == thing.InsideId) ?? nowhere;
        EditStage = Stages.FirstOrDefault(st => st.Id == thing.Lifecycle);
        EditTags = string.Join(", ", thing.Tags);
        NewValueLabel = string.Empty;
        NewValueText = string.Empty;
        NewValueFromSpec = false;
        removedLinks.Clear();
        EditLinks.Clear();
        foreach (var link in thing.Links ?? [])
        {
            EditLinks.Add(link);
        }

        LinkTargets.Clear();
        foreach (var other in things.Values.Where(t => t.Id != thing.Id).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            LinkTargets.Add(new ThingPlace(other.Id, other.Name));
        }

        NewLinkType = LinkTypes.FirstOrDefault();
        NewLinkTarget = null;
        removedPorts.Clear();
        unplugged.Clear();
        EditPorts.Clear();
        foreach (var port in thing.Ports ?? [])
        {
            EditPorts.Add(port);
        }

        NewPortName = string.Empty;
        NewPortConnector = null;
        NewPortGender = null;
        NewPortDirection = null;
        NewPortSignals = string.Empty;
        NewPortVoltage = string.Empty;
        NewPortPolarity = string.Empty;
        NewPortLevel = string.Empty;
        NewPortCurrent = string.Empty;
        NewPortFromSpec = false;
        RebuildConnectChoices();
        ConnectFrom = null;
        ConnectTo = null;
        EditingName = thing.Name;
    }

    [RelayCommand]
    private void RemoveLink(ThingLinkDto? link)
    {
        if (link is not null && EditLinks.Remove(link))
        {
            removedLinks.Add(link.RelationshipId);
        }
    }

    [RelayCommand]
    private void RemovePort(ThingPortDto? port)
    {
        if (port is not null && EditPorts.Remove(port))
        {
            removedPorts.Add(port.PortId);
            RebuildConnectChoices();
        }
    }

    /// <summary>Unplugs a port (the connection goes on Save); the port itself stays.</summary>
    [RelayCommand]
    private void UnplugPort(ThingPortDto? port)
    {
        if (port?.ConnectionId is not { } connection)
        {
            return;
        }

        var index = EditPorts.IndexOf(port);
        if (index < 0)
        {
            return;
        }

        unplugged.Add(connection);
        EditPorts[index] = port with { ConnectionId = null, OtherId = null, OtherName = null, OtherPortName = null };
        RebuildConnectChoices();
    }

    /// <summary>Offers "the new port" as a place to plug in from once it has a name, without rebuilding the lists on each key.</summary>
    partial void OnNewPortNameChanged(string value)
    {
        var existing = ConnectFromChoices.FirstOrDefault(c => c.PortId == NewPortChoice);
        if (value.Trim().Length > 0 && existing is null && editing is not null)
        {
            ConnectFromChoices.Add(newPortChoice);
        }
        else if (value.Trim().Length == 0 && existing is not null)
        {
            ConnectFromChoices.Remove(existing);
        }
    }

    private readonly ThingPortChoice newPortChoice = new(null, NewPortChoice, "The new port");

    /// <summary>Rebuilds both port lists, keeping the same choice objects selected where they still apply.</summary>
    private void RebuildConnectChoices()
    {
        if (editing is not { } thing)
        {
            return;
        }

        var from = ConnectFrom;
        var to = ConnectTo;
        Sync(ConnectFromChoices, [
            .. EditPorts.Where(p => p.ConnectionId is null).Select(p => new ThingPortChoice(null, p.PortId, p.Name)),
            .. NewPortName.Trim().Length > 0 ? [newPortChoice] : Array.Empty<ThingPortChoice>()]);
        Sync(ConnectToChoices, [
            .. things.Values.Where(t => t.Id != thing.Id).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .SelectMany(other => (other.Ports ?? [])
                    // Free ports, and ports this edit is about to unplug.
                    .Where(p => p.ConnectionId is null || unplugged.Contains(p.ConnectionId))
                    .Select(p => new ThingPortChoice(other.Id, p.PortId, $"{other.Name} · {p.Name}")))]);
        ConnectFrom = ConnectFromChoices.FirstOrDefault(c => c == from);
        ConnectTo = ConnectToChoices.FirstOrDefault(c => c == to);
    }

    /// <summary>Adds and removes so that existing choice objects (and any selection of them) survive.</summary>
    private static void Sync(ObservableCollection<ThingPortChoice> target, IReadOnlyList<ThingPortChoice> wanted)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.ThingId == target[i].ThingId && w.PortId == target[i].PortId && w.Text == target[i].Text))
            {
                target.RemoveAt(i);
            }
        }

        foreach (var choice in wanted)
        {
            if (!target.Any(t => t.ThingId == choice.ThingId && t.PortId == choice.PortId && t.Text == choice.Text))
            {
                target.Add(choice);
            }
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        editing = null;
        EditingName = null;
        EditError = null;
        ConnectToChoices.Clear();
        ConnectFromChoices.Clear();
    }

    /// <summary>Sends only what changed; the model records it as entered by this person.</summary>
    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (Source is not { } source || editing is not { } thing || !CanChange)
        {
            return;
        }

        var name = EditName.Trim();
        var note = EditNote.Trim();
        var tags = EditTags.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).ToArray();
        var tagsChanged = !tags.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(thing.Tags);
        ThingValueEditDto[]? values = NewValueLabel.Trim().Length > 0 ? [new ThingValueEditDto(NewValueLabel.Trim(), NewValueText.Trim(), NewValueFromSpec)] : null;
        var edit = new ThingEditDto(
            thing.Id,
            name == thing.Name ? null : name,
            EditKind is { } kind && kind.Id != thing.KindId ? kind.Id : null,
            note == (PersonNote(thing) ?? string.Empty) ? null : note,
            EditInside is { } place && place.Id != thing.InsideId ? place.Id ?? string.Empty : null,
            EditStage is { } stage && stage.Id != thing.Lifecycle ? stage.Id : null,
            tagsChanged ? tags : null,
            values,
            NewLinkType is { } linkType && NewLinkTarget?.Id is { } target ? [new ThingLinkEditDto(linkType.Id, target)] : null,
            removedLinks.Count > 0 ? [.. removedLinks] : null,
            NewPort() is { } port ? [port] : null,
            removedPorts.Count > 0 ? [.. removedPorts] : null,
            PortConnection() is { } connect ? [connect] : null,
            unplugged.Count > 0 ? [.. unplugged] : null);
        if (edit is
            {
                Name: null, KindId: null, Note: null, InsideId: null, Lifecycle: null, Tags: null, Values: null, AddLinks: null, RemoveLinks: null,
                AddPorts: null, RemovePorts: null, Connect: null, Disconnect: null
            })
        {
            CancelEdit();
            return;
        }

        try
        {
            await source.EditThingAsync(edit);
        }
        catch (ApiValidationException exception)
        {
            EditError = exception.Message;
            return;
        }
        catch (LocalApiException exception)
        {
            EditError = exception.Message;
            return;
        }

        CancelEdit();
        StatusMessage = $"Saved {name}.";
        await RefreshAsync();
    }

    private ThingPortEditDto? NewPort()
    {
        var name = NewPortName.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        static string? Text(string value) => value.Trim() is { Length: > 0 } text ? text : null;
        var output = NewPortDirection is "Output";
        return new ThingPortEditDto(
            name,
            NewPortConnector?.Id,
            NewPortGender,
            NewPortDirection,
            NewPortSignals.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Text(NewPortLevel),
            Text(NewPortVoltage),
            Text(NewPortPolarity),
            output ? Text(NewPortCurrent) : null,
            output ? null : Text(NewPortCurrent),
            NewPortFromSpec);
    }

    private ThingConnectEditDto? PortConnection() =>
        ConnectFrom is { } from && ConnectTo is { ThingId: { } otherId } to
            ? new ThingConnectEditDto(from.PortId == NewPortChoice ? NewPortName.Trim() : from.PortId, otherId, to.PortId)
            : null;

    /// <summary>The note the person wrote themselves (other sources' notes are not theirs to edit here).</summary>
    private static string? PersonNote(ThingDto thing) =>
        thing.Facts.FirstOrDefault(f => f.Key == "note.text" && f.Evidence == "Entered")?.Value;

    private HashSet<string> DescendantsOf(string id)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([id]);
        while (queue.TryDequeue(out var next))
        {
            if (!things.TryGetValue(next, out var thing))
            {
                continue;
            }

            foreach (var part in thing.PartIds.Where(found.Add))
            {
                queue.Enqueue(part);
            }
        }

        return found;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Source is not { } source || !CanChange)
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

    /// <summary>Keeps the note for a need: it becomes a requirement on the person's list, and stops being suggested.</summary>
    [RelayCommand]
    private async Task AddNeedAsync(ThingMissingRow? row)
    {
        if (row is null || Source is not { } source || !CanChange)
        {
            return;
        }

        try
        {
            await source.AddThingAsync($"{row.Need}\n{row.Explanation}", "requirement");
        }
        catch (Exception exception) when (exception is ApiValidationException or LocalApiException)
        {
            StatusMessage = exception.Message;
            return;
        }

        StatusMessage = $"Added \"{row.Need}\" to your list.";
        await RefreshAsync();
    }

    private void SyncMissing(IReadOnlyList<MissingItemDto> items)
    {
        var rows = items.Select(m => new ThingMissingRow(
                m.Need,
                m.Explanation,
                m.Certainty is "VerifiedProblem" or "LikelyProblem" ? "problem" : "unknown",
                string.Join(", ", m.Things.Select(id => things.TryGetValue(id, out var t) ? t.Name : null).OfType<string>())))
            .ToArray();
        if (rows.SequenceEqual(Missing))
        {
            return;
        }

        Missing.Clear();
        foreach (var row in rows)
        {
            Missing.Add(row);
        }

        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingTitle));
    }

    /// <summary>Builds the cards: top-level things (not inside another) with their values, readings and parts.</summary>
    public static IReadOnlyList<ThingCard> Build(
        IReadOnlyList<ThingDto> things, IReadOnlyList<HistoryPointDto> readings,
        ILookup<string, ThingWiringDto>? wiring = null, IReadOnlyList<ConnectorChoiceDto>? connectors = null)
    {
        wiring ??= Array.Empty<ThingWiringDto>().ToLookup(w => w.ThingId, StringComparer.Ordinal);
        var labels = (connectors ?? []).ToDictionary(c => c.Id, c => c.Label, StringComparer.OrdinalIgnoreCase);
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
                    t.KindId,
                    NodeIdOf(t.ReadingSubjects),
                    t.Tags,
                    t.Links?.Select(l => $"{l.Phrase} {l.OtherName}").ToArray(),
                    t.Ports?.Select(p => PortText(p, labels)).ToArray(),
                    wiring[t.Id].Select(WiringRow).ToArray());
            })
            .ToArray();
    }

    private static string PortText(ThingPortDto port, Dictionary<string, string> labels)
    {
        var connector = port.ConnectorTypeId is { } id ? labels.GetValueOrDefault(id) ?? id : null;
        var parts = new List<string> { port.Name };
        if (connector is not null || port.Gender != "Unknown")
        {
            parts.Add(string.Join(' ', new[] { connector, port.Gender == "Unknown" ? null : port.Gender.ToLowerInvariant() }.OfType<string>()));
        }

        if (port.Direction != "Unknown")
        {
            parts.Add(port.Direction.ToLowerInvariant());
        }

        parts.AddRange(port.Values.Select(v => v.Value));
        if (port.Signals.Count > 0)
        {
            parts.Add(string.Join(", ", port.Signals));
        }

        var text = string.Join(" · ", parts);
        return port.OtherName is { } other ? $"{text} → {other} {port.OtherPortName}" : text;
    }

    private static ThingWiringRow WiringRow(ThingWiringDto wiring) => new(
        wiring.Route,
        wiring.VerdictText,
        wiring.Verdict switch
        {
            "WillNotWork" or "ProbablyWillNotWork" => "problem",
            "CannotTellYet" => "unknown",
            _ => "ok",
        },
        wiring.Findings.Where(f => f.Certainty != "OkAsDeclared").Select(f => f.Certainty switch
        {
            "VerifiedProblem" => f.Explanation,
            "LikelyProblem" => "Likely: " + f.Explanation,
            _ => "Unknown: " + f.Explanation,
        }).ToArray());

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
                yield return new ThingValueRow(reading.Label, reading.DisplayValue, "Measured", source, IsLive: true,
                    ThingValueGroup.ForReading(SignalCatalog.Describe(point.SignalId).Kind));
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
        Func<string, bool> mine = part.SignalPrefix is { } prefix ? id => id.StartsWith(prefix, StringComparison.Ordinal) : _ => true;
        ThingValueRow[] values = [.. Readings(part.ReadingSubjects, bySubject, mine), .. Facts(part)];
        var detail = values.Length == 0 ? null : string.Join(" · ", values.Take(2).Select(v => $"{v.Label} {v.Value}"));
        // The Ecosystem shows readings by layer; the part's first reading says which layer holds it.
        var first = part.ReadingSubjects.SelectMany(s => bySubject.GetValueOrDefault(s) ?? []).FirstOrDefault(p => mine(p.SignalId));
        var node = first is null ? null : NodeIdOf(part.ReadingSubjects);
        var layer = first is null ? null : SignalCatalog.Describe(first.SignalId).Kind.ToString().ToLowerInvariant();
        return new ThingPartRow(part.Id, part.Name, part.KindLabel, part.KindId, detail, values, node, layer);
    }

    /// <summary>The Ecosystem node of a peer subject ("peer:abc" is node "abc"); other subjects have no node of their own.</summary>
    private static string? NodeIdOf(IReadOnlyList<string> subjects) =>
        subjects.FirstOrDefault(s => s.StartsWith(PeerSubject, StringComparison.Ordinal)) is { } peer ? peer[PeerSubject.Length..] : null;

    private const string PeerSubject = "peer:";

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
        var shown = all.Where(c => filter.Length == 0 || c.SearchText.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        // Rebuilding every card's visuals on each visit made switching to Things stutter; replace only cards that changed.
        if (shown.Count != Cards.Count || shown.Where((card, i) => card.Id != Cards[i].Id).Any())
        {
            Cards.Clear();
            foreach (var card in shown)
            {
                Cards.Add(card);
            }
        }
        else
        {
            for (var i = 0; i < shown.Count; i++)
            {
                if (shown[i].Signature != Cards[i].Signature)
                {
                    Cards[i] = shown[i];
                }
            }
        }

        // Keep an opened thing open across refreshes, with its new readings; unchanged, it is left alone so it does not fly apart again.
        if (OpenCard is { } open)
        {
            var fresh = all.FirstOrDefault(c => c.Id == open.Id);
            if (fresh is null || fresh.Signature != open.Signature)
            {
                OpenCard = fresh;
            }
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
