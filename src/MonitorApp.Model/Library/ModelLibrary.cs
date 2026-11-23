using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorApp.Model;

public sealed record KindDefinition(string Id, string? Parent, string Label);

public sealed record PropertyDefinition(string Key, PropertyValueType Type, string? Dimension);

/// <summary>
/// The vocabulary the reasoning layer understands: object kinds and property names.
/// It is data, versioned, and extendable. It never limits what can be recorded:
/// unknown kinds and property keys are stored as the user wrote them (docs/ARCHITECTURE-MODEL.md 3.14, 4).
/// </summary>
public sealed class ModelLibrary
{
    private readonly Dictionary<string, KindDefinition> kinds;
    private readonly Dictionary<string, PropertyDefinition> properties;

    public string Version { get; }
    public IReadOnlyCollection<KindDefinition> Kinds => kinds.Values;
    public IReadOnlyCollection<PropertyDefinition> Properties => properties.Values;

    public ModelLibrary(string version, IEnumerable<KindDefinition> kindDefinitions, IEnumerable<PropertyDefinition> propertyDefinitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        kinds = new Dictionary<string, KindDefinition>(StringComparer.Ordinal);
        foreach (var kind in kindDefinitions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind.Id);
            if (!kinds.TryAdd(kind.Id, kind)) throw new ArgumentException($"Kind '{kind.Id}' is defined twice.", nameof(kindDefinitions));
        }

        foreach (var kind in kinds.Values)
        {
            if (kind.Parent is not null && !kinds.ContainsKey(kind.Parent))
                throw new ArgumentException($"Kind '{kind.Id}' names unknown parent '{kind.Parent}'.", nameof(kindDefinitions));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var k = kind; k is not null; k = k.Parent is null ? null : kinds[k.Parent])
                if (!seen.Add(k.Id)) throw new ArgumentException($"Kind '{kind.Id}' has a parent cycle.", nameof(kindDefinitions));
        }

        properties = new Dictionary<string, PropertyDefinition>(StringComparer.Ordinal);
        foreach (var property in propertyDefinitions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(property.Key);
            if (!properties.TryAdd(property.Key, property)) throw new ArgumentException($"Property '{property.Key}' is defined twice.", nameof(propertyDefinitions));
        }
    }

    public bool IsKnownKind(string kindId) => kinds.ContainsKey(kindId);

    public bool TryGetKind(string kindId, out KindDefinition? kind) => kinds.TryGetValue(kindId, out kind);

    public bool TryGetProperty(string key, out PropertyDefinition? property) => properties.TryGetValue(key, out property);

    /// <summary>True when <paramref name="kindId"/> is <paramref name="familyId"/> or descends from it. Unknown kinds belong to no family but themselves.</summary>
    public bool IsA(string kindId, string familyId)
    {
        if (string.Equals(kindId, familyId, StringComparison.Ordinal)) return true;
        for (var k = kinds.GetValueOrDefault(kindId); k?.Parent is not null; k = kinds.GetValueOrDefault(k.Parent))
            if (string.Equals(k.Parent, familyId, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Returns a new library with extra definitions, for local or imported packs.</summary>
    public ModelLibrary Extend(string version, IEnumerable<KindDefinition>? extraKinds = null, IEnumerable<PropertyDefinition>? extraProperties = null) =>
        new(version, kinds.Values.Concat(extraKinds ?? []), properties.Values.Concat(extraProperties ?? []));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ModelLibrary Starter { get; } = LoadStarter();

    private static ModelLibrary LoadStarter()
    {
        using var stream = typeof(ModelLibrary).Assembly.GetManifestResourceStream("MonitorApp.Model.starter-library.json")
            ?? throw new InvalidOperationException("The starter library resource is missing.");
        var file = JsonSerializer.Deserialize<LibraryFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The starter library is empty.");
        return new ModelLibrary(file.Version,
            file.Kinds.Select(k => new KindDefinition(k.Id, k.Parent, k.Label)),
            file.Properties.Select(p => new PropertyDefinition(p.Key, p.Type, p.Dimension)));
    }

    private sealed record LibraryFile(string Version, List<KindEntry> Kinds, List<PropertyEntry> Properties);
    private sealed record KindEntry(string Id, string? Parent, string Label);
    private sealed record PropertyEntry(string Key, PropertyValueType Type, string? Dimension);
}
