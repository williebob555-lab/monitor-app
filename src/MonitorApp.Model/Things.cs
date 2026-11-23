using System.Globalization;

namespace MonitorApp.Model;

/// <summary>One known value of a thing, with how it is known.</summary>
/// <param name="Evidence">The evidence level name: Measured, Specified, Imported, Entered, Estimated or Unknown.</param>
/// <param name="Others">How many other values from other sources exist for the same property.</param>
public sealed record ThingFact(string Key, string Label, string Value, string Evidence, string Source, DateTimeOffset RecordedAt, int Others);

/// <summary>
/// A model object as the Things screen shows it: plain names, its values with evidence, where it sits and what it holds.
/// <paramref name="ReadingSubjects"/> are the monitoring subject ids whose live readings belong to this thing
/// ("peer:&lt;id&gt;" for a computer or phone), so a screen can join readings without knowing the bridge. For a part,
/// they are its container's subjects and <paramref name="SignalPrefix"/> says which of those readings are the part's
/// ("system.memory." for Memory).
/// </summary>
public sealed record ThingSummary(
    string Id,
    string Name,
    string KindId,
    string KindLabel,
    string Lifecycle,
    IReadOnlyList<string> Tags,
    bool Monitored,
    IReadOnlyList<string> ReadingSubjects,
    IReadOnlyList<ThingFact> Facts,
    string? InsideId,
    IReadOnlyList<string> PartIds,
    DateTimeOffset CreatedAt,
    string? SignalPrefix = null);

/// <summary>A kind a person can pick when adding a thing.</summary>
public sealed record ThingKindChoice(string Id, string Label);

/// <summary>Read and quick-capture operations for the Things screen, shared by every head (desktop node and phone).</summary>
public static class Things
{
    public const string CaptureOrigin = "quick-capture";
    public const int MaxNameLength = 120;
    public const int MaxNoteLength = 4000;

    /// <summary>Kinds offered when adding a thing; anything else can be set later.</summary>
    public static IReadOnlyList<string> CaptureKinds { get; } =
    [
        "thing", "note", "device.computer", "device.phone", "device.network", "device.audio", "device.power", "part", "cable", "adapter", "software", "project",
    ];

    public static IReadOnlyList<ThingKindChoice> KindChoices(ModelLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return CaptureKinds.Select(id => new ThingKindChoice(id, library.TryGetKind(id, out var kind) ? kind!.Label : id)).ToArray();
    }

    /// <summary>Every object in the actual world, computers first, then by name.</summary>
    public static IReadOnlyList<ThingSummary> List(SystemModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Objects
            .Where(o => o.CreatedInLayer is null)
            .Select(o => Summarize(model, o))
            .OrderBy(t => Rank(t.KindId))
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static ThingSummary Summarize(SystemModel model, ModelObject thing)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(thing);
        var facts = thing.Properties
            .Where(p => p.Scope is null && !p.Key.StartsWith("monitoring.", StringComparison.Ordinal))
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var best = thing.Strongest(g.Key)!;
                return new ThingFact(g.Key, LabelFor(g.Key), Format(best.Value), best.Evidence.Level.ToString(), SourceText(best.Evidence), best.RecordedAt, g.Count() - 1);
            })
            .OrderBy(f => f.Key == "note.text" ? 0 : 1)
            .ThenBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var prefix = thing.Strongest(MonitoringBridge.SignalsKey)?.Value.Text;
        var container = model.ContainerOf(thing.Id)?.SourceId is { } holder ? model.Find(holder) : null;
        var subjects = (prefix is not null && container is not null ? container : thing).MonitoringBindings
            .Select(b => b.Kind == MonitoringSubjectKind.Peer ? "peer:" + b.SubjectId : b.SubjectId)
            .ToArray();
        return new ThingSummary(
            thing.Id,
            thing.Name,
            thing.KindId,
            model.Library.TryGetKind(thing.KindId, out var kind) ? kind!.Label : thing.KindId,
            thing.Lifecycle.ToString(),
            thing.Tags.ToArray(),
            thing.MonitoringBindings.Count > 0,
            subjects,
            facts,
            model.ContainerOf(thing.Id)?.SourceId,
            model.ChildrenOf(thing.Id).Select(r => r.TargetId).ToArray(),
            thing.CreatedAt,
            prefix);
    }

    /// <summary>
    /// Adds a thing from a few words. The first line is the name; anything after it is kept as a note. Nothing else is
    /// required: kind defaults to "thing" and every other value stays unknown until someone fills it in.
    /// </summary>
    public static async Task<ThingSummary> CaptureAsync(ModelStore store, string text, string? kindId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var (name, note) = Split(text);
        var kind = string.IsNullOrWhiteSpace(kindId) ? "thing" : kindId.Trim();
        if (!store.Library.IsKnownKind(kind))
        {
            throw new ModelValidationException($"'{kind}' is not a kind this app knows.");
        }

        var id = "thing:" + Guid.NewGuid().ToString("N");
        var lifecycle = kind is "note" or "project" ? Lifecycle.Idea : Lifecycle.Owned;
        var changes = new List<ModelChange> { new ObjectCreated(id, name, kind, lifecycle) };
        if (note is not null)
        {
            changes.Add(new PropertyAsserted(id, "note.text", PropertyValue.Of(note), Evidence.UserEntered));
        }

        var model = await store.AppendAsync(changes, CaptureOrigin, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Summarize(model, model.Get(id));
    }

    internal static (string Name, string? Note) Split(string? text)
    {
        var trimmed = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (trimmed.Length == 0)
        {
            throw new ModelValidationException("Type at least a name.");
        }

        var newline = trimmed.IndexOf('\n', StringComparison.Ordinal);
        var name = (newline < 0 ? trimmed : trimmed[..newline]).Trim();
        var note = newline < 0 ? null : trimmed[(newline + 1)..].Trim();
        if (name.Length > MaxNameLength)
        {
            // A long single line is a note with a short name taken from its start.
            note = note is null ? name : name + "\n" + note;
            name = name[..MaxNameLength].TrimEnd() + "…";
        }

        if (note is { Length: > MaxNoteLength })
        {
            throw new ModelValidationException($"Keep the note under {MaxNoteLength} characters.");
        }

        return (name, string.IsNullOrEmpty(note) ? null : note);
    }

    public static string Format(PropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Type switch
        {
            PropertyValueType.Quantity when value.Quantity is { } q => q.Value.ToString("0.###", CultureInfo.CurrentCulture) + " " + q.Unit,
            PropertyValueType.Text => value.Text ?? string.Empty,
            PropertyValueType.Flag => value.Flag == true ? "Yes" : "No",
            _ => "Unknown",
        };
    }

    /// <summary>"identity.peer-key-fingerprint" gives "Peer key fingerprint"; "note.text" gives "Note".</summary>
    public static string LabelFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (key)
        {
            case "note.text": return "Note";
            case "note.link": return "Link";
            case "identity.lan.interface.mac": return "Hardware address (MAC)";
        }

        var last = key[(key.LastIndexOf('.') + 1)..].Replace('-', ' ').Replace('_', ' ');
        return last.Length == 0 ? key : char.ToUpperInvariant(last[0]) + last[1..];
    }

    private static string SourceText(Evidence evidence) => evidence.Source switch
    {
        "user" => "you",
        MonitoringBridge.Origin => "monitoring",
        var other => other,
    };

    private static int Rank(string kindId) => kindId switch
    {
        _ when kindId.StartsWith("device.computer", StringComparison.Ordinal) || kindId is "device.laptop" or "device.server" => 0,
        "device.phone" or "device.tablet" => 1,
        _ when kindId.StartsWith("device", StringComparison.Ordinal) => 2,
        _ when kindId.StartsWith("part", StringComparison.Ordinal) => 4,
        _ when kindId.StartsWith("network", StringComparison.Ordinal) => 5,
        _ => 3,
    };
}
