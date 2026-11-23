using MonitorApp.Model;
using MonitorApp.Planning;
using MonitorApp.Planning.Model;
using Xunit;

namespace MonitorApp.Planning.Tests;

/// <summary>Pedalboards and desks on the shared model (T082): an "x,y" position in mm, sizes as values, fit as findings.</summary>
public sealed class SurfaceLayoutTests : IDisposable
{
    private static readonly Evidence Datasheet = new(EvidenceLevel.Specified, "datasheet");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-surface-layout-tests", Guid.NewGuid().ToString("N"));
    private ModelStore? store;

    [Fact]
    public async Task PedalsPlacedOnABoardShowWhereTheyAreAndWhatHangsOverOrCollides()
    {
        Directory.CreateDirectory(directory);
        store = new ModelStore(Path.Combine(directory, "model.db"));
        await store.InitializeAsync();
        await store.AppendAsync(
        [
            new ObjectCreated("board", "Pedalboard", "assembly.pedalboard", Lifecycle.Owned),
            .. Size("board", 500, 250, Datasheet),
            new ObjectCreated("fuzz", "Fuzz", "device.audio", Lifecycle.Owned),
            .. Size("fuzz", 70, 120, Datasheet),
            new ObjectCreated("delay", "Delay", "device.audio", Lifecycle.Owned),
            .. Size("delay", 100, 130, Datasheet),
            new ObjectCreated("tuner", "Tuner", "device.audio", Lifecycle.Owned),
            new ObjectCreated("looper", "Looper", "device.audio", Lifecycle.Owned),
        ], "test");

        await Things.EditAsync(store, "fuzz", new ThingEdit(InsideId: "board", Slot: "20, 10"));
        await Things.EditAsync(store, "delay", new ThingEdit(InsideId: "board", Slot: "60,40")); // overlaps the fuzz
        await Things.EditAsync(store, "tuner", new ThingEdit(InsideId: "board", Slot: "460, 10")); // size unknown, drawn 70 wide: over the edge
        await Things.EditAsync(store, "looper", new ThingEdit(InsideId: "board"));
        var model = await store.LoadAsync();
        Assert.Equal("20,10", Things.Summarize(model, model.Get("fuzz")).Slot);

        var board = SurfaceLayouts.For(model, "board");
        Assert.Equal((500.0, 250.0, true), (board.Width, board.Depth, board.SizeKnown));
        Assert.Equal(["Delay 60,40 100x130", "Fuzz 20,10 70x120", "Tuner 460,10 70x120"], board.Items.Select(i => $"{i.Name} {i.X},{i.Y} {i.Width}x{i.Depth}"));
        var overlap = Assert.Single(board.Findings, f => f.Code == "overlap");
        Assert.Equal(("Delay and Fuzz take the same space.", FindingCertainty.VerifiedProblem), (overlap.Explanation, overlap.Certainty));
        var edge = Assert.Single(board.Findings, f => f.Code == "off-the-edge");
        Assert.Equal(FindingCertainty.Unknown, edge.Certainty); // the tuner's size is only assumed
        Assert.Contains(board.Findings, f => f.Code == "item-size-unknown");
        Assert.Equal(["Looper"], board.Unplaced);

        // Move the delay clear of the fuzz: the overlap goes away.
        await Things.EditAsync(store, "delay", new ThingEdit(Slot: "120, 10"));
        board = SurfaceLayouts.For(await store.LoadAsync(), "board");
        Assert.DoesNotContain(board.Findings, f => f.Code == "overlap");

        // A position far outside any real board is refused.
        await Assert.ThrowsAsync<ModelValidationException>(() => Things.EditAsync(store, "delay", new ThingEdit(Slot: "1e9, 4")));
    }

    private static ModelChange[] Size(string id, double width, double depth, Evidence evidence) =>
    [
        new PropertyAsserted(id, SurfaceLayouts.WidthKey, PropertyValue.Number(width, "mm"), evidence),
        new PropertyAsserted(id, SurfaceLayouts.DepthKey, PropertyValue.Number(depth, "mm"), evidence),
    ];

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
