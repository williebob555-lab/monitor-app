namespace MonitorApp.Planning.Connections;

public enum ConnectionDirection { Source = 1, Sink = 2, Bidirectional = 3, Unknown = 4 }
public enum ConnectionAssessment { PotentiallyCompatible = 1, Incompatible = 2, Unresolved = 3 }
public enum ConnectionFindingLevel { Error = 1, Warning = 2, Unknown = 3, Note = 4 }

public sealed record ElectricalRange(
    decimal? MinimumVoltage,
    decimal? MaximumVoltage,
    decimal? MaximumSourceCurrentAmps,
    decimal? RequiredSinkCurrentAmps,
    string? Polarity,
    bool RequiresNegotiation = false);

/// <summary>
/// A connector ID is only physical morphology. These are declared properties of the device port,
/// and may differ from the connector's potential protocols (e.g., USB-C without DisplayPort).
/// </summary>
public sealed record ConnectionPort(
    string Id,
    string DeviceId,
    string ConnectorTypeId,
    ConnectorGender Gender,
    ConnectionDirection Direction,
    IReadOnlySet<string> ConfirmedProtocols,
    ElectricalRange? Power = null,
    string? Pinout = null,
    string? VerifiedSpecification = null);

public sealed record CableEnd(string ConnectorTypeId, ConnectorGender Gender);

/// <summary>
/// A cable has two *different* endpoints; it is not interchangeable with a protocol converter.
/// USB-C cable speed, e-marker and capabilities must be explicitly declared, not inferred from
/// an oval USB-C connector. Unknown is a first-class state.
/// </summary>
public sealed record ConnectionCable(
    string Id,
    CableEnd EndA,
    CableEnd EndB,
    IReadOnlySet<string> ConfirmedProtocols,
    bool? PinoutVerified,
    decimal? LengthMetres,
    decimal? MaximumCurrentAmps = null,
    string? Shielding = null,
    string? CertificationSource = null);

public sealed record AdapterCapability(
    string Id,
    CableEnd Input,
    CableEnd Output,
    IReadOnlySet<string> InputProtocols,
    IReadOnlySet<string> OutputProtocols,
    bool ActiveConversion,
    bool? Verified,
    string? PowerRequirement = null);

public sealed record ConnectionFinding(
    string Code,
    ConnectionFindingLevel Level,
    string Description,
    string? SubjectId = null,
    string Version = "connector-match/1");

public sealed record ConnectionEvaluation(
    ConnectionAssessment Assessment,
    IReadOnlyList<ConnectionFinding> Findings,
    IReadOnlySet<string> CommonProtocols)
{
    public bool HasErrors => Findings.Any(f => f.Level == ConnectionFindingLevel.Error);
    public bool HasUnknowns => Findings.Any(f => f.Level == ConnectionFindingLevel.Unknown);
}

/// <summary>
/// Conservative physical + protocol + electrical assessment, not an approval to energize wiring.
/// A port shape never establishes protocol version, USB alternate mode, polarity or rated current.
/// </summary>
public sealed class ConnectionCompatibilityEvaluator(ConnectorCatalog? catalog = null)
{
    private readonly ConnectorCatalog catalog = catalog ?? ConnectorCatalog.BuiltIn;

    public ConnectionEvaluation EvaluateDirect(
        ConnectionPort source, ConnectionPort sink, ConnectionCable cable)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(cable);
        var issues = new List<ConnectionFinding>();

        var sourceType = Find(source.ConnectorTypeId, source.Id);
        var sinkType = Find(sink.ConnectorTypeId, sink.Id);
        var firstType = Find(cable.EndA.ConnectorTypeId, cable.Id);
        var secondType = Find(cable.EndB.ConnectorTypeId, cable.Id);

        if (source.Direction is ConnectionDirection.Sink)
            Add("source-direction", ConnectionFindingLevel.Error, "The source port cannot produce a signal or supply power.", source.Id);
        else if (source.Direction == ConnectionDirection.Unknown)
            Add("source-direction-unknown", ConnectionFindingLevel.Unknown, "The source port direction is not specified.", source.Id);
        if (sink.Direction is ConnectionDirection.Source)
            Add("sink-direction", ConnectionFindingLevel.Error, "The destination port does not accept this connection.", sink.Id);
        else if (sink.Direction == ConnectionDirection.Unknown)
            Add("sink-direction-unknown", ConnectionFindingLevel.Unknown, "The destination port direction is not specified.", sink.Id);

        if (sourceType is not null && firstType is not null)
            Mate(sourceType, source.Gender, firstType, cable.EndA.Gender, source.Id);
        if (sinkType is not null && secondType is not null)
            Mate(sinkType, sink.Gender, secondType, cable.EndB.Gender, sink.Id);

        var common = Intersect(source.ConfirmedProtocols, cable.ConfirmedProtocols, sink.ConfirmedProtocols);
        if (source.ConfirmedProtocols.Count == 0 ||
            cable.ConfirmedProtocols.Count == 0 ||
            sink.ConfirmedProtocols.Count == 0)
            Add("protocol-unknown", ConnectionFindingLevel.Unknown,
                "At least one device port or cable has no confirmed protocol capability. Connector appearance is insufficient.");
        else if (common.Count == 0)
            Add("protocol-incompatible", ConnectionFindingLevel.Error,
                "Source, cable and destination share no declared protocol. An explicit converter or different cable may be required.");
        else
        {
            foreach (var type in new[] { sourceType, sinkType, firstType, secondType })
                if (type is not null && !type.PotentialProtocols.Overlaps(common))
                    Add("protocol-connector-exception", ConnectionFindingLevel.Unknown,
                        $"The connector catalog does not establish that {type.Label} supports the declared protocol.");
        }

        if (source.Pinout is null || sink.Pinout is null || cable.PinoutVerified != true)
            Add("pinout-unverified", ConnectionFindingLevel.Unknown,
                "Pin assignments or cable wiring are not fully verified; physical mating alone is not enough.");
        else if (!string.Equals(source.Pinout, sink.Pinout, StringComparison.OrdinalIgnoreCase))
            Add("pinout-difference", ConnectionFindingLevel.Unknown,
                "Port pinouts differ; a specifically documented crossover or adapter may be needed.");

        if (source.Power is not null || sink.Power is not null)
        {
            if (source.Power is null || sink.Power is null)
                Add("power-profile-unknown", ConnectionFindingLevel.Unknown,
                    "Only one endpoint declares its power profile. Check the other endpoint before connecting.");
            else
                EvaluatePower(source.Power, sink.Power, cable.MaximumCurrentAmps);
        }

        if (cable.LengthMetres is null or <= 0)
            Add("cable-length-unknown", ConnectionFindingLevel.Unknown, "Cable length or routing distance is unknown.");
        if (cable.Shielding is null)
            Add("cable-shielding-unknown", ConnectionFindingLevel.Unknown,
                "Shielding and interference rejection are not documented.");
        if (source.VerifiedSpecification is null || sink.VerifiedSpecification is null ||
            cable.CertificationSource is null)
            Add("documentation-unknown", ConnectionFindingLevel.Unknown,
                "Evidence of manufacturer specifications or cable certification is incomplete.");

        var assessment = issues.Any(x => x.Level == ConnectionFindingLevel.Error)
            ? ConnectionAssessment.Incompatible
            : issues.Any(x => x.Level == ConnectionFindingLevel.Unknown)
                ? ConnectionAssessment.Unresolved : ConnectionAssessment.PotentiallyCompatible;
        return new ConnectionEvaluation(assessment, issues, common);

        ConnectorType? Find(string id, string subject)
        {
            if (catalog.TryGet(id, out var found)) return found;
            Add("unknown-connector", ConnectionFindingLevel.Unknown,
                $"Connector type {id} is not registered. Add a versioned definition.", subject);
            return null;
        }

        void Mate(ConnectorType a, ConnectorGender aGender, ConnectorType b, ConnectorGender bGender, string subject)
        {
            if (!string.Equals(a.FitKey, b.FitKey, StringComparison.OrdinalIgnoreCase))
                Add("physical-mismatch", ConnectionFindingLevel.Error,
                    $"{a.Label} and {b.Label} are not defined as directly mating; use an explicitly modeled adapter.", subject);
            if (aGender == ConnectorGender.Unknown || bGender == ConnectorGender.Unknown)
                Add("mating-gender-unknown", ConnectionFindingLevel.Unknown,
                    "Cable/port gender is unknown.", subject);
            else if (aGender == bGender && aGender != ConnectorGender.Genderless)
                Add("mating-gender", ConnectionFindingLevel.Error,
                    "Two plugs or two receptacles do not directly mate.", subject);
            else if (aGender == ConnectorGender.Genderless != (bGender == ConnectorGender.Genderless))
                Add("mating-style", ConnectionFindingLevel.Unknown,
                    "Genderless and keyed mating details need a verified adapter or pinout.", subject);
        }

        void EvaluatePower(ElectricalRange supply, ElectricalRange load, decimal? cableCurrentLimit)
        {
            if (supply.MinimumVoltage is null || supply.MaximumVoltage is null ||
                load.MinimumVoltage is null || load.MaximumVoltage is null)
                Add("voltage-unknown", ConnectionFindingLevel.Unknown, "Compatible voltage ranges have not been established.");
            else if (supply.MaximumVoltage < load.MinimumVoltage ||
                     supply.MinimumVoltage > load.MaximumVoltage)
                Add("voltage-mismatch", ConnectionFindingLevel.Error,
                    "Supply and load declared voltage ranges do not overlap.");
            if (supply.RequiresNegotiation || load.RequiresNegotiation)
                Add("power-negotiation-unknown", ConnectionFindingLevel.Unknown,
                    "Negotiated USB-PD or other power profiles require verified capabilities and a negotiated contract.");
            if (supply.Polarity is null || load.Polarity is null)
                Add("polarity-unknown", ConnectionFindingLevel.Unknown, "Required supply polarity or AC/DC type is unknown.");
            else if (!string.Equals(supply.Polarity, load.Polarity, StringComparison.OrdinalIgnoreCase))
                Add("polarity-mismatch", ConnectionFindingLevel.Error,
                    "AC/DC type or polarity mismatch can damage equipment.");
            if (supply.MaximumSourceCurrentAmps is null || load.RequiredSinkCurrentAmps is null)
                Add("power-current-unknown", ConnectionFindingLevel.Unknown,
                    "Current limit or required current is not known.");
            else if (load.RequiredSinkCurrentAmps > supply.MaximumSourceCurrentAmps)
                Add("supply-overload", ConnectionFindingLevel.Error, "Load requires more current than the source can supply.");
            if (load.RequiredSinkCurrentAmps is { } demand)
            {
                if (cableCurrentLimit is null)
                    Add("cable-current-unknown", ConnectionFindingLevel.Unknown,
                        "The cable's verified current rating is unknown.");
                else if (demand > cableCurrentLimit)
                    Add("cable-overcurrent", ConnectionFindingLevel.Error,
                        "Current demand exceeds cable rating.");
            }
        }

        void Add(string code, ConnectionFindingLevel level, string detail, string? subject = null) =>
            issues.Add(new ConnectionFinding(code, level, detail, subject));
    }

    /// <summary>
    /// Explicit adapter suggestion, *not* proof of an energized compatible route.
    /// A passive adapter cannot be treated as protocol conversion: HDMI to DisplayPort and
    /// USB-C to HDMI often require specific active electronics or alternate-mode sourcing.
    /// </summary>
    public ConnectionEvaluation EvaluateAdapter(
        ConnectionPort source, ConnectionPort sink, AdapterCapability converter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(converter);
        var issues = new List<ConnectionFinding>();
        if (!catalog.TryGet(source.ConnectorTypeId, out var sourceType) ||
            !catalog.TryGet(converter.Input.ConnectorTypeId, out var adapterInput) ||
            !catalog.TryGet(sink.ConnectorTypeId, out var sinkType) ||
            !catalog.TryGet(converter.Output.ConnectorTypeId, out var adapterOutput))
        {
            issues.Add(new("unknown-adapter-end", ConnectionFindingLevel.Unknown,
                "At least one endpoint/adapter connector type is missing from the catalog."));
        }
        else if (!string.Equals(sourceType.FitKey, adapterInput.FitKey, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(sinkType.FitKey, adapterOutput.FitKey, StringComparison.OrdinalIgnoreCase))
            issues.Add(new("adapter-physical-fit", ConnectionFindingLevel.Error,
                "An adapter end does not match its respective device interface."));

        var input = source.ConfirmedProtocols.Intersect(converter.InputProtocols, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = sink.ConfirmedProtocols.Intersect(converter.OutputProtocols, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.Count == 0 || output.Count == 0)
            issues.Add(new("adapter-protocol", ConnectionFindingLevel.Error,
                "Adapter inputs and outputs do not support the specified source/sink protocols."));
        if (!converter.ActiveConversion && !input.Overlaps(output))
            issues.Add(new("passive-conversion-not-supported", ConnectionFindingLevel.Error,
                "A passive connector adapter cannot convert between different signal protocols."));
        if (converter.Verified != true)
            issues.Add(new("adapter-not-verified", ConnectionFindingLevel.Unknown,
                "Adapter pinout, protocol translation, direction and power requirement are not confirmed."));
        else
            issues.Add(new("adapter-pipeline-review", ConnectionFindingLevel.Unknown,
                "Verify adapter power, signal direction, link negotiation, latency and end-to-end capability before use."));

        return new ConnectionEvaluation(
            issues.Any(x => x.Level == ConnectionFindingLevel.Error)
                ? ConnectionAssessment.Incompatible : ConnectionAssessment.Unresolved,
            issues, input.Intersect(output, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> Intersect(IReadOnlySet<string> a, IReadOnlySet<string> b, IReadOnlySet<string> c) =>
        a.Intersect(b, StringComparer.OrdinalIgnoreCase)
         .Intersect(c, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
