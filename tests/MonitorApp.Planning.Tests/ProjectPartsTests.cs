using MonitorApp.Model;
using MonitorApp.Planning.Model;
using Xunit;

namespace MonitorApp.Planning.Tests;

/// <summary>A project's parts list and cost (T084, P1 and P2), built the way the Things screen builds it.</summary>
public sealed class ProjectPartsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-project-parts-tests", Guid.NewGuid().ToString("N"));
    private ModelStore? store;

    [Fact]
    public async Task APartsListCountsWhatIsInHandAndAddsUpWhatIsLeftToBuyPerCurrency()
    {
        Directory.CreateDirectory(directory);
        store = new ModelStore(Path.Combine(directory, "model.db"));
        await store.InitializeAsync();

        var project = await Things.CaptureAsync(store, "Pedalboard build", "project");
        var fuzz = await Things.CaptureAsync(store, "Fuzz", "device.audio");               // owned by default
        var delay = await Things.CaptureAsync(store, "Delay", "device.audio");
        var patch = await Things.CaptureAsync(store, "Patch cables", "cable");
        var di = await Things.CaptureAsync(store, "DI box\nsomething small", "requirement");
        var case_ = await Things.CaptureAsync(store, "Flight case", "thing");
        var psu = await Things.CaptureAsync(store, "Power supply", "device.power");
        var note = await Things.CaptureAsync(store, "Ideas for the layout", "note");

        foreach (var part in new[] { fuzz, delay, patch, di, case_, note })
        {
            await Things.EditAsync(store, part.Id, new ThingEdit(AddLinks: [new ThingLinkEdit(RelationshipTypes.PartOfProject, project.Id)]));
        }

        await Things.EditAsync(store, psu.Id, new ThingEdit(InsideId: project.Id, Lifecycle: "Ordered")); // inside the project counts too
        await Things.EditAsync(store, delay.Id, new ThingEdit(Lifecycle: "Planned", Values: [new ThingValueEdit("Price", "$129.50")]));
        await Things.EditAsync(store, patch.Id, new ThingEdit(Lifecycle: "Planned",
            Values: [new ThingValueEdit("price", "4 USD"), new ThingValueEdit("How many", "6")]));
        await Things.EditAsync(store, case_.Id, new ThingEdit(Lifecycle: "Planned", Values: [new ThingValueEdit("Price", "€80")]));
        await Things.EditAsync(store, fuzz.Id, new ThingEdit(Values: [new ThingValueEdit("Price", "$60", FromSpec: true)]));

        var model = await store.LoadAsync();
        Assert.Equal("$129.50", Things.Summarize(model, model.Get(delay.Id)).Facts.Single(f => f.Key == ProjectParts.PriceKey).Value);
        Assert.Equal("6", Things.Summarize(model, model.Get(patch.Id)).Facts.Single(f => f.Key == ProjectParts.QuantityKey).Value);

        var list = ProjectParts.For(model, project.Id);
        Assert.Equal(["Delay ToBuy", "DI box ToBuy", "Flight case ToBuy", "Patch cables ToBuy", "Power supply Ordered", "Fuzz Have"],
            list.Parts.Select(p => $"{p.Name} {p.State}"));
        Assert.Equal("1 of 6 in hand, 1 ordered, 4 to buy", list.Summary);
        Assert.Equal([("EUR", 80.0), ("USD", 153.5)], list.ToBuyCost.Select(c => (c.Currency, c.Amount)));
        Assert.Equal("To buy: €80 + $153.50 (1 price unknown).", list.CostText);
        Assert.Equal(EvidenceLevel.Specified, list.Parts.Single(p => p.Name == "Fuzz").PriceEvidence);

        // The screen's words: prices written out, one list per project.
        var shown = Assert.Single(ThingsWiring.Projects(model));
        Assert.Equal("$4", shown.Parts.Single(p => p.Name == "Patch cables").Price);
        Assert.Null(shown.Parts.Single(p => p.Name == "DI box").Price);

        // A price that is not money, or a count that is not whole, is refused in words a person understands.
        var refused = await Assert.ThrowsAsync<ModelValidationException>(() => Things.EditAsync(store, delay.Id, new ThingEdit(Values: [new ThingValueEdit("Price", "cheap")])));
        Assert.Contains("like \"$45", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ModelValidationException>(() => Things.EditAsync(store, patch.Id, new ThingEdit(Values: [new ThingValueEdit("Quantity", "2.5")])));

        // Clearing the price takes it off the total.
        await Things.EditAsync(store, case_.Id, new ThingEdit(Values: [new ThingValueEdit("Price", "")]));
        list = ProjectParts.For(await store.LoadAsync(), project.Id);
        Assert.Equal("To buy: $153.50 (2 prices unknown).", list.CostText);
    }

    [Fact]
    public async Task TypedSizesBecomeTheValuesRacksAndBoardsUse()
    {
        Directory.CreateDirectory(directory);
        store = new ModelStore(Path.Combine(directory, "model.db"));
        await store.InitializeAsync();
        var rack = await Things.CaptureAsync(store, "Studio rack", "thing");
        await store.AppendAsync([new ObjectKindChanged(rack.Id, RackLayouts.RackKind)], "test");
        await Things.EditAsync(store, rack.Id, new ThingEdit(Values: [new ThingValueEdit("Rack units", "8"), new ThingValueEdit("Depth", "450 mm")]));
        var pedal = await Things.CaptureAsync(store, "Fuzz", "device.audio");
        await Things.EditAsync(store, pedal.Id, new ThingEdit(Values: [new ThingValueEdit("Width", "7 cm"), new ThingValueEdit("Rack units", "2U")]));

        var model = await store.LoadAsync();
        Assert.Equal((8, true), (RackLayouts.For(model, rack.Id).Units, RackLayouts.For(model, rack.Id).UnitsKnown));
        Assert.Equal(new Quantity(7, "cm"), model.Get(pedal.Id).Strongest(SurfaceLayouts.WidthKey)!.Value.Quantity);
        await Assert.ThrowsAsync<ModelValidationException>(() => Things.EditAsync(store, pedal.Id, new ThingEdit(Values: [new ThingValueEdit("Width", "70")])));
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
