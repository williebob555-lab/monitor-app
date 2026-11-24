using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonitorApp.Model;

/// <summary>What a name or product link suggests: a kind, a maker, a cleaner name, and the link itself. Every part may be unknown.</summary>
public sealed record GearGuess(string Name, string? KindId, string? Maker, string? Link);

/// <summary>
/// Quick capture of gear from a model name or a product link (bill of work, item D7). Reads only the words the person typed;
/// nothing is looked up on the internet. Which words suggest which kind, and which makers exist, is data
/// (<c>Library/recognize.json</c>), so teaching it something new is adding a row, not code.
/// </summary>
public static partial class GearRecognizer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> ShopWords = ["dp", "gp", "product", "products", "p", "item", "itm", "ip", "shop", "store", "en-us", "en-gb", "en", "us", "site", "sku", "pd", "catalog"];
    private static readonly Data Table = Load();

    /// <summary>
    /// Reads a name ("Boss DS-1 distortion pedal") or a product link. A link becomes a name from its path words
    /// ("…/Boss-DS-1-Distortion-Pedal/dp/…" gives "Boss DS-1 Distortion Pedal") and is kept as the link.
    /// </summary>
    public static GearGuess Recognize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        string? link = null;
        var name = trimmed;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !trimmed.Contains(' ', StringComparison.Ordinal))
        {
            link = uri.GetLeftPart(UriPartial.Path);
            name = NameFromLink(uri) ?? uri.Host;
        }

        var words = Normalize(name);
        string? kind = null;
        var best = (Count: 0, Longest: 0, Depth: 0);
        foreach (var row in Table.Kinds)
        {
            var hits = row.Words.Where(word => ContainsWord(words, word)).ToArray();
            if (hits.Length == 0) continue;
            var score = (hits.Length, hits.Max(w => w.Length), row.Kind.Count(c => c == '.'));
            if (score.CompareTo(best) > 0)
            {
                best = score;
                kind = row.Kind;
            }
        }

        var maker = Table.Makers.FirstOrDefault(m => m.Names.Any(n => ContainsWord(words, n)));
        return new GearGuess(name, kind ?? maker?.Kind, maker?.Maker, link);
    }

    /// <summary>The words of a link's path, without ids, file endings or shop words ("dp", "product", "B0002CZV6E").</summary>
    public static string? NameFromLink(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .Select(s => DotEnding().Replace(s, string.Empty))
            .Where(s => s.Count(char.IsLetter) >= 3 && !ShopWords.Contains(s.ToLowerInvariant()) && !LooksLikeId(s))
            .ToArray();
        var best = segments.OrderByDescending(s => s.Count(c => c is '-' or '_' or '+')).ThenByDescending(s => s.Length).FirstOrDefault();
        if (best is null) return null;
        var parts = Separator().Split(best).Where(p => p.Length > 0).ToArray();
        var joined = string.Join(' ', parts.Select(p => p.Length > 0 && char.IsLower(p[0]) && p.All(char.IsLetter) ? char.ToUpperInvariant(p[0]) + p[1..] : p));
        // Keep model numbers whole: "DS 1" back to "DS-1" when the link wrote it with a hyphen between letters and digits.
        joined = ModelNumber().Replace(joined, "$1-$2");
        return joined.Length > Things.MaxNameLength ? joined[..Things.MaxNameLength] : joined;
    }

    private static bool LooksLikeId(string segment) =>
        segment.Length >= 6 && segment.All(char.IsLetterOrDigit) && segment.Count(char.IsDigit) >= 3 && !segment.Contains('-', StringComparison.Ordinal);

    private static string Normalize(string text) => " " + string.Join(' ', Separator().Split(text.ToLowerInvariant()).Where(p => p.Length > 0)) + " ";

    private static bool ContainsWord(string normalized, string word) => normalized.Contains(Normalize(word), StringComparison.Ordinal);

    [GeneratedRegex(@"[\s\-_+,/()]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    [GeneratedRegex(@"\.(html?|php|aspx?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DotEnding();

    [GeneratedRegex(@"\b([A-Z]{1,4}) (\d{1,4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNumber();

    private sealed record KindRow(string Kind, IReadOnlyList<string> Words);

    private sealed record MakerRow(string Maker, string? Kind, IReadOnlyList<string> Names);

    private sealed record Data(IReadOnlyList<KindRow> Kinds, IReadOnlyList<MakerRow> Makers);

    private sealed record FileKind(string Kind, List<string> Words);

    private sealed record FileMaker(string Maker, string? Kind, List<string>? Also);

    private sealed record FileData(List<FileKind> Kinds, List<FileMaker> Makers);

    private static Data Load()
    {
        using var stream = typeof(GearRecognizer).Assembly.GetManifestResourceStream("MonitorApp.Model.recognize.json")
            ?? throw new InvalidOperationException("The recognize table is missing.");
        var file = JsonSerializer.Deserialize<FileData>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The recognize table is empty.");
        return new Data(
            [.. file.Kinds.Select(k => new KindRow(k.Kind, k.Words))],
            [.. file.Makers.Select(m => new MakerRow(m.Maker, m.Kind, [m.Maker, .. m.Also ?? []]))]);
    }
}
