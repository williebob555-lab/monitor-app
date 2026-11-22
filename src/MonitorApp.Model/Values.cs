using System.Globalization;
using System.Text.Json.Serialization;

namespace MonitorApp.Model;

/// <summary>
/// How a value is known. Ordered from weakest to strongest so the model can pick the
/// strongest available value while keeping the others (docs/ARCHITECTURE-MODEL.md 3.2).
/// </summary>
public enum EvidenceLevel
{
    Unknown = 0,
    Estimated = 1,
    Entered = 2,
    Imported = 3,
    Specified = 4,
    Measured = 5,
}

/// <summary>Where a value came from. <paramref name="Source"/> names the source, for example "user", "datasheet", or a monitoring adapter.</summary>
public sealed record Evidence(EvidenceLevel Level, string Source, string? Reference = null, DateTimeOffset? ObservedAt = null)
{
    public static Evidence UserEntered { get; } = new(EvidenceLevel.Entered, "user");
}

/// <summary>A number with its unit. Units are never assumed.</summary>
public readonly record struct Quantity(double Value, string Unit)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Value} {Unit}");
}

/// <summary>
/// A property value. Exactly one of the members is meaningful, chosen by <see cref="Type"/>.
/// <see cref="PropertyValueType.Unknown"/> records that a property matters but has no value yet.
/// </summary>
public sealed record PropertyValue
{
    public PropertyValueType Type { get; init; }
    public Quantity? Quantity { get; init; }
    public string? Text { get; init; }
    public bool? Flag { get; init; }

    [JsonConstructor]
    public PropertyValue(PropertyValueType type, Quantity? quantity, string? text, bool? flag)
    {
        Type = type;
        Quantity = quantity;
        Text = text;
        Flag = flag;
    }

    public static PropertyValue Number(double value, string unit) => new(PropertyValueType.Quantity, new Quantity(value, unit), null, null);
    public static PropertyValue Of(string text) => new(PropertyValueType.Text, null, text, null);
    public static PropertyValue Of(bool flag) => new(PropertyValueType.Flag, null, null, flag);
    public static PropertyValue Unknown { get; } = new(PropertyValueType.Unknown, null, null, null);

    internal string? ShapeError() => Type switch
    {
        PropertyValueType.Quantity when Quantity is null || string.IsNullOrWhiteSpace(Quantity.Value.Unit) || !double.IsFinite(Quantity.Value.Value)
            => "A quantity needs a finite number and a unit.",
        PropertyValueType.Text when Text is null => "A text value needs text.",
        PropertyValueType.Flag when Flag is null => "A flag value needs true or false.",
        PropertyValueType.Quantity or PropertyValueType.Text or PropertyValueType.Flag or PropertyValueType.Unknown => null,
        _ => "Unknown value type.",
    };
}

public enum PropertyValueType
{
    Unknown = 0,
    Quantity = 1,
    Text = 2,
    Flag = 3,
}

/// <summary>Unit conversion for the dimensions the engines use. Unknown units are kept as entered and are not convertible.</summary>
public static class Units
{
    private sealed record UnitDef(string Dimension, double Factor, double Offset = 0);

    private static readonly Dictionary<string, UnitDef> Table = new(StringComparer.Ordinal)
    {
        ["mm"] = new("length", 0.001),
        ["cm"] = new("length", 0.01),
        ["m"] = new("length", 1),
        ["in"] = new("length", 0.0254),
        ["ft"] = new("length", 0.3048),
        ["g"] = new("mass", 0.001),
        ["kg"] = new("mass", 1),
        ["lb"] = new("mass", 0.45359237),
        ["W"] = new("power", 1),
        ["kW"] = new("power", 1000),
        ["BTU/h"] = new("power", 0.29307107),
        ["V"] = new("voltage", 1),
        ["mV"] = new("voltage", 0.001),
        ["A"] = new("current", 1),
        ["mA"] = new("current", 0.001),
        ["ohm"] = new("impedance", 1),
        ["kohm"] = new("impedance", 1000),
        ["Hz"] = new("frequency", 1),
        ["kHz"] = new("frequency", 1e3),
        ["MHz"] = new("frequency", 1e6),
        ["GHz"] = new("frequency", 1e9),
        ["degC"] = new("temperature", 1),
        ["K"] = new("temperature", 1, -273.15),
        ["degF"] = new("temperature", 5.0 / 9, -32 * 5.0 / 9),
        ["s"] = new("time", 1),
        ["ms"] = new("time", 0.001),
        ["dBu"] = new("level-dbu", 1),
        ["dBV"] = new("level-dbv", 1),
        ["U"] = new("rack-units", 1),
        ["HP"] = new("eurorack-hp", 1),
        ["USD"] = new("currency-usd", 1),
        ["EUR"] = new("currency-eur", 1),
        ["GBP"] = new("currency-gbp", 1),
        ["%"] = new("ratio", 0.01),
        ["ratio"] = new("ratio", 1),
        ["count"] = new("count", 1),
    };

    public static string? DimensionOf(string unit) => Table.TryGetValue(unit, out var def) ? def.Dimension : null;

    /// <summary>Converts between units of the same dimension; returns false when either unit is unknown or the dimensions differ.</summary>
    public static bool TryConvert(Quantity quantity, string toUnit, out Quantity result)
    {
        result = default;
        if (!Table.TryGetValue(quantity.Unit, out var from) || !Table.TryGetValue(toUnit, out var to) || from.Dimension != to.Dimension)
            return false;
        var baseValue = quantity.Value * from.Factor + from.Offset;
        result = new Quantity((baseValue - to.Offset) / to.Factor, toUnit);
        return true;
    }
}
