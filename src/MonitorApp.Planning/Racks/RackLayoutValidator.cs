namespace MonitorApp.Planning.Racks;

/// <summary>
/// Mechanical fit and capacity checks over proposed geometry, never a claim that the real rack is safe.
/// Missing dimensions, rail details or capacity are explicitly unresolved rather than silently compatible.
/// </summary>
public static class RackLayoutValidator
{
    private const decimal Epsilon = 0.02m;

    public static RackLayoutEvaluation Evaluate(RackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var issues = new List<RackFinding>();
        var zones = new Dictionary<string, RackZone>(StringComparer.Ordinal);
        var items = new Dictionary<string, RackEquipment>(StringComparer.Ordinal);
        var placements = new Dictionary<string, RackPlacement>(StringComparer.Ordinal);

        foreach (var zone in layout.Zones)
        {
            if (!zones.TryAdd(zone.Id, zone))
            {
                Add("duplicate-zone", RackFindingSeverity.Error, zone.Id, null, "Zone IDs must be unique.");
                continue;
            }
            if (zone.WidthMm <= 0 || zone.HeightMm <= 0 || zone.UsableDepthMm <= 0 ||
                zone.FrontDoorClearanceMm < 0 || zone.RearReserveMm < 0 ||
                zone.RearReserveMm >= zone.UsableDepthMm)
                Add("invalid-zone-dimensions", RackFindingSeverity.Error, zone.Id, null, "Rack dimensions and clearance reserves must be positive and coherent.");
            if (zone.Format is RackFormat.Eia19 or RackFormat.Eia10 or RackFormat.Eia23 or RackFormat.OpenRack21
                && (zone.RackUnits is null or <= 0))
                Add("unknown-rack-height", RackFindingSeverity.Unresolved, zone.Id, null, "Specify the rack-unit capacity; height in mm alone does not identify the mounting grid.");
            if (zone.Format == RackFormat.Eurorack &&
                (zone.EurorackHpPerRow is null or <= 0 || zone.EurorackRows is null or <= 0 ||
                 zone.EurorackRowPitchMm is null or <= 0))
                Add("unknown-eurorack-grid", RackFindingSeverity.Unresolved, zone.Id, null, "Specify HP per row, rows and measured vertical row pitch.");
            if (zone.Format is RackFormat.Pedalboard or RackFormat.DesktopAudio &&
                zone.UnderboardClearanceMm is < 0)
                Add("invalid-underboard-clearance", RackFindingSeverity.Error, zone.Id, null, "Under-board clearance cannot be negative.");
            if (!Enum.IsDefined(zone.Format))
                Add("unknown-format", RackFindingSeverity.Error, zone.Id, null, "Unsupported rack format; define a custom zone rather than guessing.");
        }

        foreach (var item in layout.Equipment)
        {
            if (!items.TryAdd(item.Id, item))
                Add("duplicate-equipment", RackFindingSeverity.Error, "", null, "Equipment IDs must be unique.");
        }
        foreach (var placement in layout.Placements)
        {
            if (!placements.TryAdd(placement.Id, placement))
                Add("duplicate-placement", RackFindingSeverity.Error, placement.ZoneId, placement.Id, "Placement IDs must be unique.");
        }

        // A nested region is allowed only when an actual shelf, subrack or adapter placement creates it.
        foreach (var zone in zones.Values.Where(z => z.ParentPlacementId is not null))
        {
            if (!placements.TryGetValue(zone.ParentPlacementId!, out var parent) ||
                !items.TryGetValue(parent.EquipmentId, out var mount))
            {
                Add("missing-parent-adapter", RackFindingSeverity.Error, zone.Id, null, "Nested rack zones require a placed parent shelf, frame or adapter.");
                continue;
            }
            if (mount.InternalWidthMm is null || mount.InternalHeightMm is null || mount.InternalDepthMm is null)
            {
                Add("unknown-adapter-capacity", RackFindingSeverity.Unresolved, zone.Id, parent.Id, "Parent adapter internal clearances are not fully measured.");
                continue;
            }
            if (zone.WidthMm > mount.InternalWidthMm || zone.HeightMm > mount.InternalHeightMm ||
                zone.UsableDepthMm > mount.InternalDepthMm)
                Add("adapter-overflow", RackFindingSeverity.Error, zone.Id, parent.Id, "Child mounting zone is larger than its parent adapter's measured cavity.");
        }

        var placedVolumes = new Dictionary<string, Volume>(StringComparer.Ordinal);
        foreach (var placement in placements.Values)
        {
            if (!zones.TryGetValue(placement.ZoneId, out var zone))
            {
                Add("missing-zone", RackFindingSeverity.Error, placement.ZoneId, placement.Id, "Placement references a missing rack zone.");
                continue;
            }
            if (!items.TryGetValue(placement.EquipmentId, out var item))
            {
                Add("missing-equipment", RackFindingSeverity.Error, zone.Id, placement.Id, "Placement references a missing piece of equipment.");
                continue;
            }
            if (item.MountFormat != zone.Format)
                Add("mount-interface", RackFindingSeverity.Error, zone.Id, placement.Id, $"Mount interface {item.MountFormat} cannot be placed directly in {zone.Format}; add a real bracket, shelf or subrack.");
            var isSurface = zone.Format is RackFormat.Pedalboard or RackFormat.DesktopAudio;
            if (isSurface && item.MountMethod is not (
                    MountingMethod.HookAndLoop or MountingMethod.DualLock or
                    MountingMethod.BoardClamp or MountingMethod.BoardScrews or MountingMethod.Freestanding))
                Add("surface-fastening", RackFindingSeverity.Error, zone.Id, placement.Id,
                    "An upright rack mounting interface does not attach a pedal or desktop device to a flat board.");
            if (item.MountMethod is MountingMethod.FourPost or MountingMethod.SlidingRails)
            {
                if (!zone.HasRearRails)
                    Add("rear-rails-missing", RackFindingSeverity.Error, zone.Id, placement.Id, "Four-post hardware requires actual rear rails.");
                if (zone.FrontToRearRailMm is null)
                    Add("unknown-rail-depth", RackFindingSeverity.Unresolved, zone.Id, placement.Id, "Rear rail spacing is not known.");
                else if (item.MinRailSpacingMm is { } min && zone.FrontToRearRailMm < min ||
                         item.MaxRailSpacingMm is { } max && zone.FrontToRearRailMm > max)
                    Add("rail-depth", RackFindingSeverity.Error, zone.Id, placement.Id, "Rail spacing is outside the equipment's mounting range.");
            }
            if (item.RequiredHolePattern is not null)
            {
                if (zone.HolePattern is null)
                    Add("unknown-hole-pattern", RackFindingSeverity.Unresolved, zone.Id, placement.Id, "Rack mounting-hole style is unknown.");
                else if (!string.Equals(item.RequiredHolePattern, zone.HolePattern, StringComparison.OrdinalIgnoreCase))
                    Add("hole-pattern", RackFindingSeverity.Error, zone.Id, placement.Id, "Mounting-hole and hardware styles do not match.");
            }

            if (item.WidthMm is not > 0 || item.HeightMm is not > 0 || item.BodyDepthMm is not > 0)
            {
                Add("unknown-equipment-dimensions", RackFindingSeverity.Unresolved, zone.Id, placement.Id, "Measured width, height and body depth are needed to prove fit.");
                continue;
            }

            Volume volume;
            if (isSurface)
            {
                // Top-down footprint on the board and vertical body height, including connector
                // and foot-operation keep-out zones. Undefined clearance is not assumed to be zero.
                if (item.LeftCableClearanceMm is null || item.RightCableClearanceMm is null ||
                    item.TopCableClearanceMm is null || item.BottomCableClearanceMm is null)
                    Add("unknown-patch-clearance", RackFindingSeverity.Unresolved, zone.Id, placement.Id,
                        "Signal/power patch cable exits are not fully measured on every side.");
                if (item.UnderboardRequiredMm is { } underside)
                {
                    if (zone.UnderboardClearanceMm is null)
                        Add("unknown-underboard", RackFindingSeverity.Unresolved, zone.Id, placement.Id,
                            "Underside power supply or cable cavity has unknown clearance.");
                    else if (underside > zone.UnderboardClearanceMm)
                        Add("underboard-collision", RackFindingSeverity.Error, zone.Id, placement.Id,
                            "Power supply or cabling requires more underside height than the board provides.");
                }

                var leftPad = item.LeftCableClearanceMm ?? 0;
                var rightPad = item.RightCableClearanceMm ?? 0;
                var topPad = item.TopCableClearanceMm ?? 0;
                var bottomPad = item.BottomCableClearanceMm ?? 0;
                var footPad = item.FootAccessClearanceMm ?? 0;
                if (new[] { leftPad, rightPad, topPad, bottomPad, footPad }.Any(v => v < 0))
                    Add("invalid-cable-clearance", RackFindingSeverity.Error, zone.Id, placement.Id,
                        "Cable and foot access clearances must be nonnegative.");
                volume = new Volume(
                    placement.LeftMm - leftPad,
                    placement.LeftMm + item.WidthMm.Value + rightPad,
                    placement.BottomMm - bottomPad - footPad,
                    placement.BottomMm + item.HeightMm.Value + topPad,
                    placement.FrontOffsetMm,
                    placement.FrontOffsetMm + item.BodyDepthMm.Value);
                if (volume.Back > zone.UsableDepthMm + Epsilon)
                    Add("above-board-clearance", RackFindingSeverity.Error, zone.Id, placement.Id,
                        "Pedal height exceeds the available board-to-lid or board-to-case clearance.");
            }
            else
            {
                volume = new Volume(
                    placement.LeftMm,
                    placement.LeftMm + item.WidthMm.Value,
                    placement.BottomMm,
                    placement.BottomMm + item.HeightMm.Value,
                    placement.FrontOffsetMm - item.FrontProtrusionMm,
                    placement.FrontOffsetMm + item.BodyDepthMm.Value + item.RearCableClearanceMm);
            }

            if (volume.Left < -Epsilon || volume.Bottom < -Epsilon ||
                volume.Right > zone.WidthMm + Epsilon || volume.Top > zone.HeightMm + Epsilon)
                Add("outside-zone", RackFindingSeverity.Error, zone.Id, placement.Id, "Equipment extends past the measured mounting region.");
            if (!isSurface && volume.Front < -zone.FrontDoorClearanceMm - Epsilon)
                Add("front-clearance", RackFindingSeverity.Error, zone.Id, placement.Id, "Handles, plugs or front panel exceed front-door clearance.");
            if (!isSurface && volume.Back > zone.UsableDepthMm - zone.RearReserveMm + Epsilon)
                Add("rear-clearance", RackFindingSeverity.Error, zone.Id, placement.Id, "Equipment and rear cable bend allowance exceed available depth.");

            // Do not conflate a 1U mounting increment with a physical 1U panel height.
            if (zone.Format is RackFormat.Eia19 or RackFormat.Eia10 or RackFormat.Eia23 or RackFormat.OpenRack21)
            {
                if (!IsMultiple(placement.BottomMm, RackDimensions.RackUnitMm))
                    Add("rack-grid", RackFindingSeverity.Warning, zone.Id, placement.Id, "Vertical placement is not aligned to the rack's U numbering.");
                if (item.RackUnitsTall is null or <= 0)
                    Add("unknown-u-footprint", RackFindingSeverity.Unresolved, zone.Id, placement.Id, "Required mounting U is not specified.");
                else if (placement.BottomMm + item.RackUnitsTall.Value * RackDimensions.RackUnitMm > zone.HeightMm + Epsilon)
                    Add("rack-u-capacity", RackFindingSeverity.Error, zone.Id, placement.Id, "Required U footprint extends beyond the rack.");
            }
            if (zone.Format == RackFormat.Eurorack)
            {
                if (!IsMultiple(placement.LeftMm, RackDimensions.EurorackHpMm))
                    Add("eurorack-hp-grid", RackFindingSeverity.Warning, zone.Id, placement.Id, "Module is not aligned to the HP grid.");
                if (item.EurorackHpWide is null or <= 0 || item.EurorackRowsTall is null or <= 0)
                    Add("unknown-eurorack-footprint", RackFindingSeverity.Unresolved, zone.Id, placement.Id, "Specify module HP width and number of rows.");
                else
                {
                    if (placement.LeftMm + item.EurorackHpWide.Value * RackDimensions.EurorackHpMm > zone.WidthMm + Epsilon)
                        Add("eurorack-hp-capacity", RackFindingSeverity.Error, zone.Id, placement.Id, "Module HP footprint exceeds row capacity.");
                    if (zone.EurorackRowPitchMm is { } pitch &&
                        (!IsMultiple(placement.BottomMm, pitch) ||
                         placement.BottomMm + item.EurorackRowsTall.Value * pitch > zone.HeightMm + Epsilon))
                        Add("eurorack-row-grid", RackFindingSeverity.Error, zone.Id, placement.Id, "Module height or position exceeds the configured row grid.");
                }
            }
            placedVolumes[placement.Id] = volume;
        }

        // Full 3D envelope collisions: two overlapping faceplates might be okay only if their depth
        // envelopes are separated. Cable bend allowances count as occupied space.
        foreach (var zoneGroup in placements.Values.GroupBy(p => p.ZoneId, StringComparer.Ordinal))
        {
            var active = zoneGroup.Where(p => placedVolumes.ContainsKey(p.Id)).ToArray();
            for (var i = 0; i < active.Length; i++)
                for (var j = i + 1; j < active.Length; j++)
                    if (placedVolumes[active[i].Id].Intersects(placedVolumes[active[j].Id]))
                        Add("volume-collision", RackFindingSeverity.Error, zoneGroup.Key, active[i].Id,
                            $"Placement collides with {active[j].Id} in width, height and depth.");
        }

        foreach (var zone in zones.Values)
        {
            var placed = placements.Values.Where(p => p.ZoneId == zone.Id && items.ContainsKey(p.EquipmentId))
                .Select(p => items[p.EquipmentId]).ToArray();
            CheckBudget("weight", "kg", zone.MaxWeightKg, placed.Select(i => i.WeightKg), zone.Id);
            CheckBudget("power", "W", zone.PowerBudgetWatts, placed.Select(i => i.PowerWatts), zone.Id);
            CheckBudget("heat", "W", zone.HeatRemovalWatts, placed.Select(i => i.HeatWatts), zone.Id);
            // Heat generated by equipment and nameplate power consumption are not equivalent measurements.
        }
        return new RackLayoutEvaluation(issues);

        void Add(string code, RackFindingSeverity level, string zoneId, string? placementId, string details) =>
            issues.Add(new RackFinding(code, level, zoneId, placementId, details));

        void CheckBudget(string kind, string unit, decimal? limit, IEnumerable<decimal?> measurements, string zoneId)
        {
            if (limit is null)
            {
                Add("unknown-" + kind + "-limit", RackFindingSeverity.Unresolved, zoneId, null, $"No {kind} capacity in {unit} is specified.");
                return;
            }
            if (limit < 0)
            {
                Add("invalid-" + kind + "-limit", RackFindingSeverity.Error, zoneId, null, $"Invalid negative {kind} budget.");
                return;
            }
            var values = measurements.ToArray();
            if (values.Any(v => v is null or < 0))
            {
                Add("unknown-" + kind + "-load", RackFindingSeverity.Unresolved, zoneId, null, $"One or more {kind} loads are unknown; do not treat missing values as zero.");
                return;
            }
            if (values.Sum(v => v!.Value) > limit.Value)
                Add(kind + "-over-budget", RackFindingSeverity.Error, zoneId, null, $"Known aggregate {kind} exceeds {limit} {unit}.");
        }
    }

    private static bool IsMultiple(decimal value, decimal pitch) =>
        pitch > 0 && decimal.Abs(value / pitch - decimal.Round(value / pitch, 0)) < 0.001m;

    private readonly record struct Volume(decimal Left, decimal Right, decimal Bottom, decimal Top, decimal Front, decimal Back)
    {
        public bool Intersects(Volume other) =>
            Left < other.Right - Epsilon && Right > other.Left + Epsilon &&
            Bottom < other.Top - Epsilon && Top > other.Bottom + Epsilon &&
            Front < other.Back - Epsilon && Back > other.Front + Epsilon;
    }
}
