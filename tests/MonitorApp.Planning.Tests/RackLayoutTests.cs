using MonitorApp.Model;
using MonitorApp.Planning;
using MonitorApp.Planning.Model;
using Xunit;

namespace MonitorApp.Planning.Tests;

/// <summary>Racks on the shared model (T082): placing is an edit ("U5"), sizes are values, fit problems are findings.</summary>
public sealed class RackLayoutTests : IDisposable
{
    private static readonly Evidence Datasheet = new(EvidenceLevel.Specified, "datasheet");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-rack-layout-tests", Guid.NewGuid().ToString("N"));
    private ModelStore? store;

    [Fact]
    public async Task ThingsPlacedAtUnitsAreDrawnTopDownAndOverlapsAndOverhangsAreFindings()
    {
        await BuildAsync(
            new ObjectCreated("rack", "Studio rack", RackLayouts.RackKind, Lifecycle.Owned),
            new PropertyAsserted("rack", RackLayouts.RackUnitsKey, PropertyValue.Number(6, "U"), Datasheet),
            new PropertyAsserted("rack", RackLayouts.DepthKey, PropertyValue.Number(400, "mm"), Datasheet),
            new ObjectCreated("amp", "Power amp", "device.audio", Lifecycle.Owned),
            new PropertyAsserted("amp", RackLayouts.RackUnitsKey, PropertyValue.Number(2, "U"), Datasheet),
            new PropertyAsserted("amp", RackLayouts.DepthKey, PropertyValue.Number(450, "mm"), Datasheet),
            new ObjectCreated("patch", "Patch bay", "device.audio", Lifecycle.Owned),
            new PropertyAsserted("patch", RackLayouts.RackUnitsKey, PropertyValue.Number(1, "U"), Datasheet),
            new ObjectCreated("dsp", "DSP", "device.audio", Lifecycle.Owned));

        // Put things in the rack the way the screen does: inside, at a unit.
        await Things.EditAsync(store!, "amp", new ThingEdit(InsideId: "rack", Slot: "U1"));
        await Things.EditAsync(store!, "patch", new ThingEdit(InsideId: "rack", Slot: "u6"));
        await Things.EditAsync(store!, "dsp", new ThingEdit(InsideId: "rack"));
        var model = await store!.LoadAsync();
        Assert.Equal("U6", Things.Summarize(model, model.Get("patch")).Slot);

        var rack = RackLayouts.For(model, "rack");
        Assert.Equal((6, true), (rack.Units, rack.UnitsKnown));
        Assert.Equal(["6 Patch bay 1", "5 - 1", "4 - 1", "3 - 1", "2 Power amp 2"], rack.Blocks.Select(Text));
        Assert.Equal(["DSP"], rack.Unplaced);
        var deep = Assert.Single(rack.Findings, f => f.Code == "too-deep");
        Assert.Equal(("Power amp is 450 mm deep, but Studio rack has 400 mm.", FindingCertainty.VerifiedProblem), (deep.Explanation, deep.Certainty));
        Assert.Contains(rack.Findings, f => f.Code == "not-placed");

        // The DSP goes at U2, on top of the amp's second unit; its height is unknown, so the overlap is only likely.
        await Things.EditAsync(store!, "dsp", new ThingEdit(Slot: "U2"));
        // The patch bay moves to U6 with a 2U height: past the top.
        await store!.AppendAsync([new PropertyAsserted("patch", RackLayouts.RackUnitsKey, PropertyValue.Number(2, "U"), Datasheet)], "test");
        rack = RackLayouts.For(await store.LoadAsync(), "rack");
        var overlap = Assert.Single(rack.Findings, f => f.Code == "overlap");
        Assert.Equal(FindingCertainty.LikelyProblem, overlap.Certainty);
        Assert.Contains(rack.Findings, f => f.Code == "height-unknown");
        Assert.Contains(rack.Findings, f => f.Code == "past-the-top" && f.Certainty == FindingCertainty.VerifiedProblem);
        Assert.Contains(rack.Blocks, b => b.Name == "DSP" && b.Problem);
        Assert.Equal(3, rack.Blocks.Count(b => b.ThingId is not null));

        // A bad position is refused with words a person understands, and nothing changes.
        var refused = await Assert.ThrowsAsync<ModelValidationException>(() => Things.EditAsync(store, "dsp", new ThingEdit(Slot: "U0")));
        Assert.Contains("like \"U5\"", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARackWithNoSizeIsDrawnAtTheAssumedSizeAndSaysSo()
    {
        var model = await BuildAsync(new ObjectCreated("rack", "Shelf rack", RackLayouts.RackKind, Lifecycle.Idea));
        var rack = RackLayouts.For(model, "rack");
        Assert.Equal((RackLayouts.AssumedUnits, false), (rack.Units, rack.UnitsKnown));
        Assert.Equal(RackLayouts.AssumedUnits, rack.Blocks.Count);
        Assert.Equal("rack-size-unknown", Assert.Single(rack.Findings).Code);
    }

    private static string Text(RackBlock block) => $"{block.TopUnit} {block.Name ?? "-"} {block.Units}";

    private async Task<SystemModel> BuildAsync(params ModelChange[] changes)
    {
        Directory.CreateDirectory(directory);
        store = new ModelStore(Path.Combine(directory, "model.db"));
        await store.InitializeAsync();
        return await store.AppendAsync(changes, "test");
    }

    public void Dispose()
    {
        store?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
