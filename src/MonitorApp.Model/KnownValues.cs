using System.Globalization;
using System.Text.RegularExpressions;

namespace MonitorApp.Model;

/// <summary>
/// Values a person types whose names the engines understand: "Price", "Quantity", "Width", "Depth", "Rack units". They are
/// stored under the shared library keys with a unit, so the parts list, racks and boards can use them; any other name stays
/// the person's own text value. Units are never assumed, except that a count needs none and a price may use a currency sign.
/// </summary>
public static partial class KnownValues
{
    /// <summary>One understood value name: the names a person may type, the library key, and how its text is read.</summary>
    public sealed record Known(string Key, IReadOnlyList<string> Names, string Dimension, string Example)
    {
        public PropertyValue Parse(string text)
        {
            var quantity = Dimension switch
            {
                "currency" => ParseMoney(text),
                "count" => ParseCount(text),
                "rack-units" => ParseNumber(text, "U") ?? ParseUnit(text, Dimension),
                _ => ParseUnit(text, Dimension),
            };

            return quantity is { } q && double.IsFinite(q.Value) && q.Value >= 0 && q.Value <= 1e9
                ? PropertyValue.Number(q.Value, q.Unit)
                : throw new ModelValidationException($"{Names[0]} needs a value like \"{Example}\".");
        }
    }

    public static IReadOnlyList<Known> All { get; } =
    [
        new("commerce.price", ["Price", "Cost", "Unit price"], "currency", "$45 or 39.99 EUR"),
        new("commerce.quantity-needed", ["Quantity", "Quantity needed", "How many", "Qty"], "count", "2"),
        new("mechanical.width", ["Width"], "length", "70 mm"),
        new("mechanical.depth", ["Depth"], "length", "120 mm"),
        new("mechanical.rack-units", ["Rack units", "Rack height", "Units tall"], "rack-units", "2U"),
    ];

    public static Known? Find(string label) =>
        All.FirstOrDefault(k => k.Names.Any(n => string.Equals(n, label.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static readonly (string Sign, string Unit)[] Signs = [("$", "USD"), ("€", "EUR"), ("£", "GBP")];

    /// <summary>"$45" or "45 USD" for a price in a currency the model knows; null for anything else.</summary>
    public static string? Money(Quantity quantity)
    {
        if (Units.DimensionOf(quantity.Unit) is not { } dimension || !dimension.StartsWith("currency-", StringComparison.Ordinal))
        {
            return null;
        }

        var amount = quantity.Value.ToString(quantity.Value % 1 == 0 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        return Signs.FirstOrDefault(s => s.Unit == quantity.Unit) is { Sign: { } sign } ? sign + amount : amount + " " + quantity.Unit;
    }

    private static Quantity? ParseMoney(string text)
    {
        var t = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        foreach (var (sign, unit) in Signs)
        {
            if (t.StartsWith(sign, StringComparison.Ordinal) && Number(t[sign.Length..]) is { } n)
            {
                return new Quantity(n, unit);
            }
        }

        var match = QuantityPattern().Match(t);
        return match.Success && Number(match.Groups[1].Value) is { } amount && Units.DimensionOf(match.Groups[2].Value.ToUpperInvariant()) is { } d && d.StartsWith("currency-", StringComparison.Ordinal)
            ? new Quantity(amount, match.Groups[2].Value.ToUpperInvariant())
            : null;
    }

    private static Quantity? ParseCount(string text) =>
        Number(text) is { } n && n == Math.Floor(n) ? new Quantity(n, "count") : null;

    private static Quantity? ParseNumber(string text, string unit) =>
        QuantityPattern().Match(text.Trim()) is { Success: true } m && string.Equals(m.Groups[2].Value, unit, StringComparison.OrdinalIgnoreCase) && Number(m.Groups[1].Value) is { } n
            ? new Quantity(n, unit)
            : Number(text) is { } plain ? new Quantity(plain, unit) : null;

    private static Quantity? ParseUnit(string text, string dimension) =>
        QuantityPattern().Match(text.Trim()) is { Success: true } m && Units.DimensionOf(m.Groups[2].Value) == dimension && Number(m.Groups[1].Value) is { } n
            ? new Quantity(n, m.Groups[2].Value)
            : null;

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([A-Za-z%]+)\s*$")]
    private static partial Regex QuantityPattern();
}
