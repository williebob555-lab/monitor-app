using MonitorApp.Planning.Racks;

namespace MonitorApp.Planning.Constraints;

/// <summary>A planning finding, not a certification of physical safety or electromagnetic compatibility.</summary>
public enum PhysicalFindingLevel { Critical = 1, Warning = 2, Advisory = 3, Unresolved = 4 }
public enum PhysicalEvidence { Measured = 1, ManufacturerSpecification = 2, UserEntered = 3, Estimated = 4 }
public enum AirflowDirection { FrontToRear = 1, RearToFront = 2, SideToSide = 3, BottomToTop = 4, Passive = 5, Unknown = 6 }

public sealed record PhysicalFinding(
    string RuleId,
    string RuleVersion,
    PhysicalFindingLevel Level,
    string? SubjectId,
    string Description,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Assumptions,
    string? SuggestedAction = null);

/// <summary>All clearances in millimetres, heat in watts, air volume in m³/h.</summary>
public sealed record VentilationProfile(
    string EquipmentId,
    AirflowDirection Direction,
    decimal? IntakeClearanceMm,
    decimal? ExhaustClearanceMm,
    decimal? MinimumIntakeMm,
    decimal? MinimumExhaustMm,
    decimal? RatedAirflowCubicMetresPerHour,
    PhysicalEvidence Source);

public sealed record ThermalEnvironment(
    string ZoneId,
    decimal? AmbientCelsius,
    decimal? MaximumPermittedInletCelsius,
    decimal? ExhaustRemovalWatts,
    decimal? MeasuredOrEstimatedAirflowCubicMetresPerHour,
    PhysicalEvidence Source);

/// <summary>Rated W budget; it is not a substitute for circuit current, VA, breaker or wire gauge calculations.</summary>
public sealed record ElectricalFeed(
    string FeedId,
    decimal? RatedContinuousWatts,
    decimal? MeasuredVoltage,
    decimal? RatedCurrentAmps,
    decimal? PowerFactor,
    IReadOnlyList<string> PlacementIds,
    PhysicalEvidence Source);

/// <summary>
/// Screened cable pairs and power/high-current/RF transmitter proximity are evaluated using an explicit
/// local engineering target, not a fabricated universally safe spacing threshold.
/// </summary>
public sealed record InterferenceExposure(
    string SourceId,
    string VictimId,
    string CouplingMechanism,
    decimal? MeasuredSeparationMm,
    decimal? ParallelRunLengthMm,
    decimal? UserTargetSeparationMm,
    bool? Shielded,
    PhysicalEvidence Source);

public sealed record PhysicalCableRun(
    string CableId,
    decimal? AvailableLengthMm,
    decimal? RoutedLengthMm,
    decimal? SlackRequiredMm,
    decimal? ActualMinimumBendRadiusMm,
    decimal? ManufacturerMinimumBendRadiusMm,
    PhysicalEvidence Source);

public sealed record AcousticZone(
    string ZoneId,
    decimal? MaximumDesiredDbA,
    decimal? MeasuredCombinedDbA,
    PhysicalEvidence Source);

public sealed record RackPhysicalContext(
    RackLayout Layout,
    IReadOnlyList<VentilationProfile> Ventilation,
    IReadOnlyList<ThermalEnvironment> Thermal,
    IReadOnlyList<ElectricalFeed> Electrical,
    IReadOnlyList<InterferenceExposure> Interference,
    IReadOnlyList<PhysicalCableRun> Cables,
    IReadOnlyList<AcousticZone> Acoustic);

public interface IPhysicalConstraintRule
{
    string Id { get; }
    string Version { get; }
    IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context);
}

/// <summary>
/// Extensible, deterministic rule runner; a new physical domain can be added without expanding the
/// rack object's nullable field list. Findings retain rule identity, inputs, assumptions and unknowns.
/// </summary>
public sealed class RackPhysicalEvaluator(IEnumerable<IPhysicalConstraintRule>? rules = null)
{
    private readonly IReadOnlyList<IPhysicalConstraintRule> rules =
        (rules ?? [new VentilationRule(), new ThermalRule(), new ElectricalRule(),
            new InterferenceRule(), new CableRoutingRule(), new AcousticRule(), new StabilityRule()]).ToArray();

    public IReadOnlyList<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var all = new List<PhysicalFinding>();
        foreach (var rule in rules)
            all.AddRange(rule.Evaluate(context));
        return all;
    }
}

public sealed class VentilationRule : IPhysicalConstraintRule
{
    public string Id => "airflow-clearance";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        var byId = context.Layout.Equipment.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var profile in context.Ventilation)
        {
            if (!byId.ContainsKey(profile.EquipmentId))
            {
                yield return New(PhysicalFindingLevel.Unresolved, profile.EquipmentId,
                    "Airflow specification references equipment not in the current design.", []);
                continue;
            }
            if (profile.Direction == AirflowDirection.Unknown)
            {
                yield return New(PhysicalFindingLevel.Unresolved, profile.EquipmentId,
                    "Fan intake and exhaust direction is unknown.", []);
                continue;
            }
            if (profile.Direction == AirflowDirection.Passive)
            {
                yield return New(PhysicalFindingLevel.Advisory, profile.EquipmentId,
                    "Passive convection is modeled, but actual heat dissipation still requires validation.",
                    ["passive ventilation"]);
                continue;
            }
            if (profile.IntakeClearanceMm is null || profile.ExhaustClearanceMm is null ||
                profile.MinimumIntakeMm is null || profile.MinimumExhaustMm is null)
            {
                yield return New(PhysicalFindingLevel.Unresolved, profile.EquipmentId,
                    "A complete intake and exhaust clearance specification is not available.",
                    ["inlet gap (mm)", "outlet gap (mm)"]);
                continue;
            }
            if (profile.IntakeClearanceMm < profile.MinimumIntakeMm ||
                profile.ExhaustClearanceMm < profile.MinimumExhaustMm)
                yield return New(PhysicalFindingLevel.Warning, profile.EquipmentId,
                    "Configured intake/exhaust gap is smaller than the stated equipment requirement.",
                    [$"intake {profile.IntakeClearanceMm}/{profile.MinimumIntakeMm} mm",
                     $"exhaust {profile.ExhaustClearanceMm}/{profile.MinimumExhaustMm} mm"]);
        }

        PhysicalFinding New(PhysicalFindingLevel level, string id, string description, IReadOnlyList<string> inputs) =>
            new(Id, Version, level, id, description, inputs,
                ["Airflow direction and clearances may vary with neighboring devices and blanking panels."],
                "Check intake, exhaust, cable obstruction and possible recirculation.");
    }
}

public sealed class ThermalRule : IPhysicalConstraintRule
{
    public string Id => "thermal-envelope";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        foreach (var zone in context.Layout.Zones)
        {
            var environment = context.Thermal.FirstOrDefault(x => x.ZoneId == zone.Id);
            var placedIds = context.Layout.Placements.Where(p => p.ZoneId == zone.Id)
                .Select(p => p.EquipmentId).ToHashSet(StringComparer.Ordinal);
            var devices = context.Layout.Equipment.Where(x => placedIds.Contains(x.Id)).ToArray();
            if (environment is null)
            {
                yield return New(zone.Id, PhysicalFindingLevel.Unresolved,
                    "Ambient temperature, inlet limit and cooling measurements have not been provided.", []);
                continue;
            }
            if (environment.AmbientCelsius is null || environment.MaximumPermittedInletCelsius is null)
                yield return New(zone.Id, PhysicalFindingLevel.Unresolved,
                    "A temperature limit cannot be checked without ambient and equipment-inlet bounds.", []);
            else if (environment.AmbientCelsius > environment.MaximumPermittedInletCelsius)
                yield return New(zone.Id, PhysicalFindingLevel.Critical,
                    "Ambient temperature already exceeds the specified inlet limit.",
                    [$"ambient {environment.AmbientCelsius} °C",
                     $"inlet limit {environment.MaximumPermittedInletCelsius} °C"]);

            if (devices.Any(x => x.HeatWatts is null or < 0))
            {
                yield return New(zone.Id, PhysicalFindingLevel.Unresolved,
                    "At least one device has unknown heat dissipation; no thermal safety claim is possible.", []);
                continue;
            }
            var heat = devices.Sum(x => x.HeatWatts!.Value);
            if (environment.ExhaustRemovalWatts is null)
            {
                yield return New(zone.Id, PhysicalFindingLevel.Unresolved,
                    "Heat-removal capacity is unknown.", [$"device heat {heat} W"]);
            }
            else if (heat > environment.ExhaustRemovalWatts)
                yield return New(zone.Id, PhysicalFindingLevel.Warning,
                    "Expected equipment heat exceeds configured removal capacity.",
                    [$"device heat {heat} W", $"removal {environment.ExhaustRemovalWatts} W"]);
            // A simplified air-temperature rise can indicate a risk, but can never prove safe cooling.
            if (environment.MeasuredOrEstimatedAirflowCubicMetresPerHour is > 0 &&
                environment.AmbientCelsius is { } ambient &&
                environment.MaximumPermittedInletCelsius is { } inletLimit)
            {
                // 1.2 kg/m³, ~1005 J/(kg·K); nominal engineering estimate, not CFD.
                var delta = heat * 3600m / (1.2m * 1005m * environment.MeasuredOrEstimatedAirflowCubicMetresPerHour.Value);
                if (ambient + delta > inletLimit)
                    yield return New(zone.Id, PhysicalFindingLevel.Warning,
                        "First-order air-heating estimate exceeds the configured inlet temperature allowance.",
                        [$"estimated ΔT {delta:0.0} °C", $"ambient {ambient} °C", $"limit {inletLimit} °C"]);
            }
        }

        PhysicalFinding New(string id, PhysicalFindingLevel level, string description, IReadOnlyList<string> inputs) =>
            new(Id, Version, level, id, description, inputs,
                ["No CFD or measurement of recirculation, local hot spots, pressure losses, or intake temperature.",
                 "Power consumption is not silently substituted for equipment heat dissipation."],
                "Measure intake temperatures and fan/vent paths before declaring an installation safe.");
    }
}

public sealed class ElectricalRule : IPhysicalConstraintRule
{
    public string Id => "electrical-feed";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        var items = context.Layout.Equipment.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var placements = context.Layout.Placements.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var feed in context.Electrical)
        {
            if (feed.RatedContinuousWatts is null or <= 0 || feed.RatedCurrentAmps is null or <= 0 ||
                feed.MeasuredVoltage is null or <= 0 || feed.PowerFactor is null or <= 0 or > 1)
            {
                yield return New(PhysicalFindingLevel.Unresolved, feed.FeedId,
                    "Rated continuous W, current A, nominal/measured voltage and power factor are incomplete.", []);
                continue;
            }
            var loads = feed.PlacementIds.Select(p => placements.GetValueOrDefault(p))
                .Select(p => p is null ? null : items.GetValueOrDefault(p.EquipmentId)).ToArray();
            if (loads.Any(x => x is null || x.PowerWatts is null or < 0))
            {
                yield return New(PhysicalFindingLevel.Unresolved, feed.FeedId,
                    "At least one feed assignment or connected equipment power draw is unknown.", []);
                continue;
            }
            var wattage = loads.Sum(x => x!.PowerWatts!.Value);
            var conservativeVa = wattage / feed.PowerFactor.Value;
            var amperage = conservativeVa / feed.MeasuredVoltage.Value;
            if (wattage > feed.RatedContinuousWatts || amperage > feed.RatedCurrentAmps)
                yield return New(PhysicalFindingLevel.Critical, feed.FeedId,
                    "Configured electrical feed limits are exceeded.",
                    [$"draw {wattage} W", $"estimate {amperage:0.00} A",
                     $"continuous W limit {feed.RatedContinuousWatts}",
                     $"current limit {feed.RatedCurrentAmps} A"]);
        }

        PhysicalFinding New(PhysicalFindingLevel level, string id, string description, IReadOnlyList<string> inputs) =>
            new(Id, Version, level, id, description, inputs,
                ["Estimates assume simultaneous nameplate loads and one stated aggregate power factor.",
                 "Does not certify wiring, earthing, overcurrent protection, UPS waveform or electrical-code compliance."],
                "Confirm actual circuit/PDU/UPS ratings and continuous-load requirements.");
    }
}

public sealed class InterferenceRule : IPhysicalConstraintRule
{
    public string Id => "emi-rf-proximity";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        foreach (var exposure in context.Interference)
        {
            var inputs = new[] { exposure.CouplingMechanism, exposure.SourceId, exposure.VictimId };
            if (exposure.MeasuredSeparationMm is null or < 0 || exposure.UserTargetSeparationMm is null or < 0)
            {
                yield return New(PhysicalFindingLevel.Unresolved, exposure.VictimId,
                    "Interference separation or the locally chosen target is unknown; compatibility cannot be inferred.", inputs);
                continue;
            }
            if (exposure.MeasuredSeparationMm < exposure.UserTargetSeparationMm)
                yield return New(PhysicalFindingLevel.Warning, exposure.VictimId,
                    "Separation is below the design target for an interference-sensitive path.", inputs);
            if (exposure.Shielded is null || exposure.ParallelRunLengthMm is null)
                yield return New(PhysicalFindingLevel.Unresolved, exposure.VictimId,
                    "Shield termination and parallel-run geometry are not fully characterized.", inputs);
        }
        PhysicalFinding New(PhysicalFindingLevel level, string id, string description, IReadOnlyList<string> inputs) =>
            new(Id, Version, level, id, description, inputs,
                ["No single universal minimum distance ensures EMC across all equipment.",
                 "Actual coupling depends on field strength, frequency, shielding, grounding and orientation."],
                "Separate power/signal routes, inspect shielding, and test in the actual installation.");
    }
}

public sealed class CableRoutingRule : IPhysicalConstraintRule
{
    public string Id => "cable-routing";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        foreach (var cable in context.Cables)
        {
            if (cable.AvailableLengthMm is null or <= 0 || cable.RoutedLengthMm is null or < 0 ||
                cable.SlackRequiredMm is null or < 0)
            {
                yield return New(PhysicalFindingLevel.Unresolved, cable.CableId,
                    "Cable route, required service slack or available length is unknown.", []);
                continue;
            }
            if (cable.AvailableLengthMm < cable.RoutedLengthMm + cable.SlackRequiredMm)
                yield return New(PhysicalFindingLevel.Warning, cable.CableId,
                    "Cable is too short for the designed route and service slack.",
                    [$"available {cable.AvailableLengthMm} mm", $"needed {cable.RoutedLengthMm + cable.SlackRequiredMm} mm"]);
            if (cable.ManufacturerMinimumBendRadiusMm is null || cable.ActualMinimumBendRadiusMm is null)
                yield return New(PhysicalFindingLevel.Unresolved, cable.CableId,
                    "Minimum bend-radius information is incomplete.", []);
            else if (cable.ActualMinimumBendRadiusMm < cable.ManufacturerMinimumBendRadiusMm)
                yield return New(PhysicalFindingLevel.Warning, cable.CableId,
                    "Cable route violates the manufacturer-stated minimum bend radius.",
                    [$"route {cable.ActualMinimumBendRadiusMm} mm", $"minimum {cable.ManufacturerMinimumBendRadiusMm} mm"]);
        }
        PhysicalFinding New(PhysicalFindingLevel level, string id, string description, IReadOnlyList<string> inputs) =>
            new(Id, Version, level, id, description, inputs, ["Connector geometry and tension require separate checks."],
                "Revise route, slack, ties, or connector orientation.");
    }
}

public sealed class AcousticRule : IPhysicalConstraintRule
{
    public string Id => "acoustic-comfort";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        foreach (var zone in context.Acoustic)
        {
            if (zone.MaximumDesiredDbA is null || zone.MeasuredCombinedDbA is null)
                yield return new PhysicalFinding(Id, Version, PhysicalFindingLevel.Unresolved, zone.ZoneId,
                    "Listening position noise target or actual combined A-weighted level is missing.", [],
                    ["Single-device dBA figures measured at different distances must not be blindly added."],
                    "Measure at the intended listening position.");
            else if (zone.MeasuredCombinedDbA > zone.MaximumDesiredDbA)
                yield return new PhysicalFinding(Id, Version, PhysicalFindingLevel.Warning, zone.ZoneId,
                    "Measured aggregate noise exceeds the chosen comfort target.",
                    [$"measured {zone.MeasuredCombinedDbA} dBA", $"desired {zone.MaximumDesiredDbA} dBA"],
                    ["A comfort target is not a legal occupational noise exposure limit."],
                    "Consider fan controls, location, mounting vibration and airflow tradeoffs.");
        }
    }
}

public sealed class StabilityRule : IPhysicalConstraintRule
{
    public string Id => "rack-stability";
    public string Version => "1.0";
    public IEnumerable<PhysicalFinding> Evaluate(RackPhysicalContext context)
    {
        foreach (var zone in context.Layout.Zones.Where(z => z.ParentPlacementId is null))
        {
            var placed = context.Layout.Placements.Where(p => p.ZoneId == zone.Id).ToArray();
            var equipment = context.Layout.Equipment.ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (placed.Length == 0) continue;
            if (placed.Any(p => !equipment.TryGetValue(p.EquipmentId, out var i) || i.WeightKg is null or < 0 ||
                                i.HeightMm is null))
            {
                yield return new PhysicalFinding(Id, Version, PhysicalFindingLevel.Unresolved, zone.Id,
                    "Overall centre of gravity cannot be estimated without equipment masses and geometry.", [], [],
                    "Verify rack footing, anchoring, rail loads and heavy equipment placement.");
                continue;
            }
            var mass = placed.Sum(p => equipment[p.EquipmentId].WeightKg!.Value);
            if (mass <= 0) continue;
            var centerHeight = placed.Sum(p =>
                equipment[p.EquipmentId].WeightKg!.Value *
                (p.BottomMm + equipment[p.EquipmentId].HeightMm!.Value / 2)) / mass;
            if (centerHeight > zone.HeightMm * 0.6m)
                yield return new PhysicalFinding(Id, Version, PhysicalFindingLevel.Warning, zone.Id,
                    "Equipment's calculated vertical mass centre is high relative to the rack.",
                    [$"centre {centerHeight:0.0} mm", $"height {zone.HeightMm} mm"],
                    ["Does not include rack chassis mass, base footprint, anchors, drawer extension or dynamic loads."],
                    "Place heavy equipment lower; assess tip stability with actual rack and anchors.");
            yield return new PhysicalFinding(Id, Version, PhysicalFindingLevel.Unresolved, zone.Id,
                "Tip stability and static/dynamic anchoring remain unverified.", [],
                ["Rack base footprint, mounting and floor capacity are not represented by a U-space plan."],
                "Confirm real anchoring, seismic and floor-load requirements.");
        }
    }
}
