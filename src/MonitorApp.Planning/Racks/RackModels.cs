namespace MonitorApp.Planning.Racks;

/// <summary>Mechanical interface families; equal nominal widths do not imply interchangeable rails.</summary>
public enum RackFormat
{
    Eia19 = 1,
    Eia10 = 2,
    Eia23 = 3,
    Eurorack = 4,
    OpenRack21 = 5,
    ShelfSpace = 6,
    Custom = 7,
}

public enum MountingMethod
{
    FrontRails = 1,
    FourPost = 2,
    SlidingRails = 3,
    Shelf = 4,
    ChassisInsert = 5,
    Freestanding = 6,
}

public static class RackDimensions
{
    public const decimal RackUnitMm = 44.45m;
    public const decimal EurorackHpMm = 5.08m;
    public const decimal EurorackThreeUPanelHeightMm = 128.5m;

    /// <summary>Nominal panel widths, not a guarantee of hole spacing, inner width or compatibility.</summary>
    public static decimal? NominalPanelWidthMm(RackFormat format) => format switch
    {
        RackFormat.Eia19 => 482.6m,
        RackFormat.Eia10 => 254m,
        RackFormat.Eia23 => 584.2m,
        _ => null,
    };

    /// <summary>1-based units numbered upward from the bottom, as on an equipment rack.</summary>
    public static RackPlacement AtRackUnits(string id, string zoneId, string itemId, int bottomUnit,
        decimal leftMm = 0, decimal frontOffsetMm = 0)
    {
        if (bottomUnit < 1) throw new ArgumentOutOfRangeException(nameof(bottomUnit));
        return new RackPlacement(id, zoneId, itemId, leftMm, (bottomUnit - 1) * RackUnitMm, frontOffsetMm);
    }

    /// <summary>1-based HP and row coordinates, both increasing from lower/left.</summary>
    public static RackPlacement AtEurorack(string id, string zoneId, string itemId,
        int firstHp, int row, decimal rowPitchMm, decimal frontOffsetMm = 0)
    {
        if (firstHp < 1 || row < 1 || rowPitchMm <= 0) throw new ArgumentOutOfRangeException(nameof(firstHp));
        return new RackPlacement(id, zoneId, itemId, (firstHp - 1) * EurorackHpMm,
            (row - 1) * rowPitchMm, frontOffsetMm);
    }
}

/// <summary>
/// A mountable region of a rack, subrack, or shelf. Dimensions are physical, in mm.
/// Nested regions have a parent placement (the adapter/shelf/subrack that creates them).
/// </summary>
public sealed record RackZone
{
    public required string Id { get; init; }
    public required RackFormat Format { get; init; }
    public required decimal WidthMm { get; init; }
    public required decimal HeightMm { get; init; }
    public required decimal UsableDepthMm { get; init; }
    public decimal FrontDoorClearanceMm { get; init; }
    public decimal RearReserveMm { get; init; }
    public decimal? FrontToRearRailMm { get; init; }
    public bool HasRearRails { get; init; }
    public string? HolePattern { get; init; } // square, tapped, round, unknown, etc; never inferred
    public decimal? MaxWeightKg { get; init; }
    public decimal? PowerBudgetWatts { get; init; }
    public decimal? HeatRemovalWatts { get; init; }
    public int? RackUnits { get; init; }
    public int? EurorackHpPerRow { get; init; }
    public int? EurorackRows { get; init; }
    public decimal? EurorackRowPitchMm { get; init; }
    public string? ParentPlacementId { get; init; }
}

public sealed record RackEquipment
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required RackFormat MountFormat { get; init; }
    public required MountingMethod MountMethod { get; init; }
    public decimal? WidthMm { get; init; }
    public decimal? HeightMm { get; init; }
    public decimal? BodyDepthMm { get; init; }
    public decimal FrontProtrusionMm { get; init; }
    public decimal RearCableClearanceMm { get; init; }
    public decimal TopVentilationMm { get; init; }
    public decimal BottomVentilationMm { get; init; }
    public decimal? WeightKg { get; init; }
    public decimal? PowerWatts { get; init; }
    public decimal? HeatWatts { get; init; }
    public decimal? MinRailSpacingMm { get; init; }
    public decimal? MaxRailSpacingMm { get; init; }
    public string? RequiredHolePattern { get; init; }
    public decimal? InternalWidthMm { get; init; } // for a mounted subrack, adapter, shelf, enclosure
    public decimal? InternalHeightMm { get; init; }
    public decimal? InternalDepthMm { get; init; }
    public string? ProductReferenceId { get; init; } // may refer to a proposed product, not an owned instance
    public string? ActualInstanceReferenceId { get; init; } // optional read-only reference, not a promotion
}

/// <summary>Front plane is depth zero; left and bottom are coordinates inside the specified region.</summary>
public sealed record RackPlacement(
    string Id, string ZoneId, string EquipmentId,
    decimal LeftMm, decimal BottomMm, decimal FrontOffsetMm = 0);

public sealed record RackLayout(
    string ScenarioId,
    int Revision,
    IReadOnlyList<RackZone> Zones,
    IReadOnlyList<RackEquipment> Equipment,
    IReadOnlyList<RackPlacement> Placements);

public enum RackFindingSeverity { Error = 1, Warning = 2, Unresolved = 3 }

public sealed record RackFinding(
    string Code,
    RackFindingSeverity Severity,
    string ZoneId,
    string? PlacementId,
    string Explanation,
    string RuleVersion = "rack-layout/1");

public sealed record RackLayoutEvaluation(IReadOnlyList<RackFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == RackFindingSeverity.Error);
    public bool HasUnknowns => Findings.Any(f => f.Severity == RackFindingSeverity.Unresolved);
    public bool IsUnqualifiedFit => !HasErrors && !HasUnknowns;
}
