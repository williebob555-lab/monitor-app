namespace MonitorApp.Planning.Racks;

/// <summary>
/// Framework-independent data for a rack-organization panel: front view, side-depth view,
/// snapping guides and findings. This is a projection of a design revision, not live inventory.
/// </summary>
public static class RackPanelProjection
{
    public static RackPanelScene Build(RackLayout layout, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        var zone = layout.Zones.Single(z => z.Id == zoneId);
        var evaluation = RackLayoutValidator.Evaluate(layout);
        var equipment = layout.Equipment.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var items = new List<RackPanelItem>();
        var unplaced = new List<string>();
        foreach (var placement in layout.Placements.Where(p => p.ZoneId == zoneId))
        {
            if (!equipment.TryGetValue(placement.EquipmentId, out var item) ||
                item.WidthMm is not > 0 || item.HeightMm is not > 0 || item.BodyDepthMm is not > 0)
            {
                unplaced.Add(placement.Id);
                continue;
            }

            var problems = evaluation.Findings.Where(f => f.PlacementId == placement.Id)
                .Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            items.Add(new RackPanelItem(
                placement.Id, item.Id, item.Name, placement.LeftMm, placement.BottomMm,
                item.WidthMm.Value, item.HeightMm.Value,
                placement.FrontOffsetMm - item.FrontProtrusionMm,
                placement.FrontOffsetMm + item.BodyDepthMm.Value + item.RearCableClearanceMm,
                problems));
        }
        var xPitch = zone.Format == RackFormat.Eurorack ? RackDimensions.EurorackHpMm : (decimal?)null;
        decimal? yPitch = zone.Format switch
        {
            RackFormat.Eurorack => zone.EurorackRowPitchMm,
            RackFormat.Eia19 or RackFormat.Eia10 or RackFormat.Eia23 or RackFormat.OpenRack21 => RackDimensions.RackUnitMm,
            _ => null,
        };
        return new RackPanelScene(
            layout.ScenarioId, layout.Revision, zone, xPitch, yPitch,
            items.OrderByDescending(x => x.BottomMm).ThenBy(x => x.LeftMm).ToArray(),
            unplaced, evaluation.Findings.Where(f => f.ZoneId == zoneId).ToArray(),
            IsDesign: true);
    }
}

/// <summary>
/// Scene coordinates use millimetres, left/up in the front view and front/back in the side view.
/// The UI chooses orientation, zoom, outlines, and how to label U/HP coordinates.
/// </summary>
public sealed record RackPanelScene(
    string ScenarioId,
    int Revision,
    RackZone Zone,
    decimal? HorizontalGridPitchMm,
    decimal? VerticalGridPitchMm,
    IReadOnlyList<RackPanelItem> Items,
    IReadOnlyList<string> PlacementsWithUnknownGeometry,
    IReadOnlyList<RackFinding> Findings,
    bool IsDesign);

public sealed record RackPanelItem(
    string PlacementId,
    string EquipmentId,
    string Label,
    decimal LeftMm,
    decimal BottomMm,
    decimal WidthMm,
    decimal HeightMm,
    decimal FrontMm,
    decimal BackMm,
    IReadOnlyList<string> FindingCodes);
