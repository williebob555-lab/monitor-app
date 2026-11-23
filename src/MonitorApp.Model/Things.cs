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
    string? SignalPrefix = null,
    IReadOnlyList<ThingLink>? Links = null,
    IReadOnlyList<ThingPort>? Ports = null);

/// <summary>
/// A link a person can see and change on a thing, phrased from this thing's side: "Powered by" Studio PSU, or "Powers" the amp
/// when this thing is the other end. Containment ("inside") is not a link here; it has its own field.
/// </summary>
public sealed record ThingLink(string RelationshipId, string TypeId, string Phrase, string OtherId, string OtherName, bool Outgoing);

/// <summary>A link type offered in the edit form ("depends-on" shown as "Depends on").</summary>
public sealed record ThingLinkType(string Id, string Label);

/// <summary>Adds a link from the edited thing to another: "this thing &lt;type&gt; other".</summary>
public sealed record ThingLinkEdit(string TypeId, string OtherId);

/// <summary>What a person changed on a thing; null fields are left alone (see <see cref="Things.EditAsync"/>).</summary>
/// <param name="Lifecycle">A lifecycle stage name ("Idea", "Owned", "Retired", ...); moves follow <see cref="LifecycleRules"/>.</param>
/// <param name="Tags">The complete set of tags the thing should have (null: leave them).</param>
/// <param name="Values">Values the person adds or replaces; an empty value removes theirs.</param>
public sealed record ThingEdit(
    string? Name = null,
    string? KindId = null,
    string? Note = null,
    string? InsideId = null,
    string? Lifecycle = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<ThingValueEdit>? Values = null,
    IReadOnlyList<ThingLinkEdit>? AddLinks = null,
    IReadOnlyList<string>? RemoveLinks = null,
    IReadOnlyList<ThingPortEdit>? AddPorts = null,
    IReadOnlyList<string>? RemovePorts = null,
    IReadOnlyList<ThingConnectEdit>? Connect = null,
    IReadOnlyList<string>? Disconnect = null);

/// <summary>A value the person types, such as "Max power" = "5 W". <paramref name="FromSpec"/> marks it as read from a spec sheet or datasheet.</summary>
public sealed record ThingValueEdit(string Label, string? Value, bool FromSpec = false);

/// <summary>A kind a person can pick when adding a thing.</summary>
public sealed record ThingKindChoice(string Id, string Label);

/// <summary>Read and quick-capture operations for the Things screen, shared by every head (desktop node and phone).</summary>
public static class Things
{
    public const string CaptureOrigin = "quick-capture";
    public const string EditOrigin = "things-edit";
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
            prefix,
            Links(model, thing.Id),
            ThingPorts.Describe(model, thing));
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

    /// <summary>
    /// Changes what a person can change on a thing: its name, its kind, their note, and what it sits inside. Every field is
    /// optional (null leaves it alone). An empty note removes the person's note; an empty <see cref="ThingEdit.InsideId"/>
    /// takes the thing out of whatever holds it. Readings and other sources' values are never touched. All of it is one
    /// change set, so a bad move (into itself, into one of its own parts) leaves the thing as it was.
    /// </summary>
    public static Task<ThingSummary> EditAsync(ModelStore store, string thingId, ThingEdit edit, CancellationToken cancellationToken = default) =>
        EditAsync(store, thingId, edit, null, cancellationToken);

    /// <param name="knownConnector">Whether a connector id is in the shared connector library; the model itself does not
    /// hold the library, so callers that have it pass it. Without it any short id is accepted.</param>
    public static async Task<ThingSummary> EditAsync(ModelStore store, string thingId, ThingEdit edit, Func<string, bool>? knownConnector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(edit);
        var model = await store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(thingId) || model.Find(thingId) is not { } thing || thing.CreatedInLayer is not null)
        {
            throw new ModelValidationException("That thing is not in the model.");
        }

        var changes = new List<ModelChange>();
        if (edit.Name is not null)
        {
            var name = edit.Name.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (name.Length == 0)
            {
                throw new ModelValidationException("A thing needs a name.");
            }

            if (name.Length > MaxNameLength)
            {
                throw new ModelValidationException($"Keep the name under {MaxNameLength} characters.");
            }

            if (name != thing.Name)
            {
                changes.Add(new ObjectRenamed(thing.Id, name));
            }
        }

        if (edit.KindId is { } kindId && kindId.Trim() != thing.KindId)
        {
            if (!store.Library.IsKnownKind(kindId.Trim()))
            {
                throw new ModelValidationException($"'{kindId}' is not a kind this app knows.");
            }

            changes.Add(new ObjectKindChanged(thing.Id, kindId.Trim()));
        }

        if (edit.Note is not null)
        {
            var note = edit.Note.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (note.Length > MaxNoteLength)
            {
                throw new ModelValidationException($"Keep the note under {MaxNoteLength} characters.");
            }

            var current = thing.Properties.FirstOrDefault(p => p.Key == "note.text" && p.Scope is null && p.Evidence.Source == Evidence.UserEntered.Source);
            if (note.Length == 0 && current is not null)
            {
                changes.Add(new PropertyRetracted(thing.Id, "note.text", Evidence.UserEntered.Source));
            }
            else if (note.Length > 0 && current?.Value.Text != note)
            {
                changes.Add(new PropertyAsserted(thing.Id, "note.text", PropertyValue.Of(note), Evidence.UserEntered));
            }
        }

        if (edit.InsideId is { } insideId)
        {
            var holder = model.ContainerOf(thing.Id);
            var target = insideId.Trim();
            if (target.Length > 0 && model.Find(target) is null)
            {
                throw new ModelValidationException("The thing to put it in is not in the model.");
            }

            if (holder?.SourceId != (target.Length == 0 ? null : target))
            {
                if (holder is not null)
                {
                    changes.Add(new RelationshipRemoved(holder.RelationshipId));
                }

                if (target.Length > 0)
                {
                    changes.Add(new RelationshipAdded($"user:{Guid.NewGuid():N}", RelationshipTypes.Contains, target, thing.Id));
                }
            }
        }

        if (edit.Lifecycle is { } stageText && stageText.Trim().Length > 0)
        {
            if (!Enum.TryParse<Lifecycle>(stageText.Trim(), ignoreCase: true, out var stage) || !Enum.IsDefined(stage))
            {
                throw new ModelValidationException($"'{stageText}' is not a lifecycle stage.");
            }

            if (stage != thing.Lifecycle)
            {
                changes.Add(new LifecycleMoved(thing.Id, stage));
            }
        }

        if (edit.Tags is { } tags)
        {
            var wanted = tags.Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (wanted.Any(t => t.Length > MaxTagLength))
            {
                throw new ModelValidationException($"Keep each tag under {MaxTagLength} characters.");
            }

            changes.AddRange(thing.Tags.Where(t => !wanted.Contains(t, StringComparer.OrdinalIgnoreCase)).Select(t => (ModelChange)new ObjectUntagged(thing.Id, t)));
            changes.AddRange(wanted.Where(t => !thing.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).Select(t => (ModelChange)new ObjectTagged(thing.Id, t)));
        }

        foreach (var value in edit.Values ?? [])
        {
            changes.AddRange(ValueChanges(thing, value));
        }

        foreach (var removed in edit.RemoveLinks ?? [])
        {
            // Only links this thing takes part in, and never containment (that is "inside").
            if (model.RelationshipsOf(thing.Id).FirstOrDefault(r => r.RelationshipId == removed) is not { } link || link.TypeId == RelationshipTypes.Contains)
            {
                throw new ModelValidationException("That link is not on this thing.");
            }

            changes.Add(new RelationshipRemoved(link.RelationshipId));
        }

        foreach (var added in edit.AddLinks ?? [])
        {
            if (!LinkTypes.Any(t => t.Id == added.TypeId))
            {
                throw new ModelValidationException($"'{added.TypeId}' is not a kind of link this screen can add.");
            }

            if (model.Find(added.OtherId) is null)
            {
                throw new ModelValidationException("The thing to link to is not in the model.");
            }

            if (model.RelationshipsOf(thing.Id).Any(r => r.TypeId == added.TypeId && r.SourceId == thing.Id && r.TargetId == added.OtherId))
            {
                continue;
            }

            changes.Add(new RelationshipAdded($"user:{Guid.NewGuid():N}", added.TypeId, thing.Id, added.OtherId));
        }

        changes.AddRange(ThingPorts.Changes(model, thing, edit, knownConnector ?? (id => id.Length <= 40)));
        if (changes.Count == 0)
        {
            return Summarize(model, thing);
        }

        model = await store.AppendAsync(changes, EditOrigin, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Summarize(model, model.Get(thing.Id));
    }

    /// <summary>Links a person can draw from the Things screen, with how each reads from either end.</summary>
    public static IReadOnlyList<ThingLinkType> LinkTypes { get; } =
    [
        new(RelationshipTypes.DependsOn, "Depends on"),
        new(RelationshipTypes.PoweredBy, "Powered by"),
        new(RelationshipTypes.HostedOn, "Runs on"),
        new(RelationshipTypes.PartOfProject, "Part of"),
        new(RelationshipTypes.Replaces, "Replaces"),
        new(RelationshipTypes.About, "About"),
        new(ConnectsTo, "Connected to"),
    ];

    /// <summary>A loose "these are connected" link, before anyone records ports and cables.</summary>
    public const string ConnectsTo = "connects-to";

    private static string Phrase(string typeId, bool outgoing) => (typeId, outgoing) switch
    {
        (RelationshipTypes.DependsOn, false) => "Needed by",
        (RelationshipTypes.PoweredBy, false) => "Powers",
        (RelationshipTypes.HostedOn, false) => "Runs",
        (RelationshipTypes.PartOfProject, false) => "Includes",
        (RelationshipTypes.Replaces, false) => "Replaced by",
        (RelationshipTypes.About, false) => "Noted in",
        (ConnectsTo, _) => "Connected to",
        _ when LinkTypes.FirstOrDefault(t => t.Id == typeId) is { } known => known.Label,
        _ => LabelFor(typeId.Replace('-', '.')),
    };

    private static ThingLink[] Links(SystemModel model, string thingId) =>
        model.RelationshipsOf(thingId)
            .Where(r => r.TypeId != RelationshipTypes.Contains && r.CreatedInLayer is null)
            .Select(r =>
            {
                var outgoing = r.SourceId == thingId;
                var other = outgoing ? r.TargetId : r.SourceId;
                return new ThingLink(r.RelationshipId, r.TypeId, Phrase(r.TypeId, outgoing), other, model.Find(other)?.Name ?? other, outgoing);
            })
            .OrderBy(l => l.Phrase, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(l => l.OtherName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>Where a person's own values live: "user.Max-power" shows as "Max power".</summary>
    public const string PersonValuePrefix = "user.";

    /// <summary>The source of values a person read from a spec sheet.</summary>
    public const string SpecSource = "user-spec";

    public const int MaxTagLength = 40;

    private static IEnumerable<ModelChange> ValueChanges(ModelObject thing, ThingValueEdit value)
    {
        var label = (value.Label ?? string.Empty).Trim();
        if (label.Length == 0 || label.Length > MaxNameLength)
        {
            throw new ModelValidationException("Give the value a short name, like \"Max power\".");
        }

        var key = PersonValuePrefix + string.Join('-', label.Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var text = (value.Value ?? string.Empty).Trim();
        if (text.Length > MaxNoteLength)
        {
            throw new ModelValidationException($"Keep the value under {MaxNoteLength} characters.");
        }

        if (text.Length == 0)
        {
            // Removes only the person's own values for this name; values from other sources stay.
            foreach (var source in new[] { Evidence.UserEntered.Source, SpecSource })
            {
                if (thing.Properties.Any(p => p.Key == key && p.Scope is null && p.Evidence.Source == source))
                {
                    yield return new PropertyRetracted(thing.Id, key, source);
                }
            }

            yield break;
        }

        var evidence = value.FromSpec ? new Evidence(EvidenceLevel.Specified, SpecSource) : Evidence.UserEntered;
        yield return new PropertyAsserted(thing.Id, key, PropertyValue.Of(text), evidence);
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

    internal static string SourceText(Evidence evidence) => evidence.Source switch
    {
        "user" => "you",
        SpecSource => "you, from a spec",
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
