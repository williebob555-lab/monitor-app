using MonitorApp.Model;
using MonitorApp.Planning;
using MonitorApp.Planning.Model;
using Xunit;

namespace MonitorApp.Planning.Tests;

/// <summary>"What's missing" (T069) on a small pedalboard: every need comes from ports, connections and values, nothing per scenario.</summary>
public sealed class WhatsMissingTests : IDisposable
{
    private static readonly Evidence Datasheet = new(EvidenceLevel.Specified, "datasheet");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-whats-missing-tests", Guid.NewGuid().ToString("N"));
    private ModelStore? store;

    [Fact]
    public async Task APedalboardShowsItsPowerShortfallUnpoweredPedalMissingCableAndLooseLead()
    {
        var model = await BuildAsync(
            // A supply with three outputs, 500 mA in total.
            Item("psu", "Pedal power", "device.power",
                Dc("o1", "Out 1", PortGender.Plug, PortDirection.Output), Dc("o2", "Out 2", PortGender.Plug, PortDirection.Output), Dc("o3", "Out 3", PortGender.Plug, PortDirection.Output)),
            Amps("psu", null, "electrical.current.max", 0.5),
            Pedal("fuzz", "Fuzz", 0.1),
            Pedal("delay", "Delay", 0.3),
            Pedal("reverb", "Reverb", 0.25),
            Pedal("tuner", "Tuner", 0.05),
            Item("lead", "Patch lead", "cable", Jack("a", "tip", PortGender.Plug, PortDirection.Both), Jack("b", "tip", PortGender.Plug, PortDirection.Both)),
            Connect("p1", "psu", "o1", "fuzz", "dc"),
            Connect("p2", "psu", "o2", "delay", "dc"),
            Connect("p3", "psu", "o3", "reverb", "dc"),
            Connect("s1", "fuzz", "out", "delay", "in"), // two sockets joined directly: a cable is missing
            Connect("s2", "delay", "out", "lead", "a")); // the lead's other end hangs free

        var missing = WhatsMissing.Derive(model);
        var needs = missing.Select(m => m.Need).ToArray();

        var shortfall = Assert.Single(missing, m => m.Code == "power-shortfall");
        Assert.Equal(FindingCertainty.VerifiedProblem, shortfall.Certainty);
        Assert.Equal("Delay, Fuzz, Reverb draw 0.65 A together, but Pedal power supplies 0.5 A in total: 0.15 A short.", shortfall.Explanation);
        Assert.Contains("Power for Tuner", needs);
        Assert.Contains("Cable: 6.35 mm TS / 1/4-inch mono jack plug to 6.35 mm TS / 1/4-inch mono jack plug", needs);
        Assert.Contains("Plug in the other end of Patch lead", needs);
        Assert.Contains("Something into Fuzz Input", needs); // Fuzz is wired, but nothing feeds its input
        Assert.Contains("Something into Reverb Input", needs);
        Assert.DoesNotContain(needs, n => n.Contains("Tuner Input", StringComparison.Ordinal)); // the tuner is not wired at all yet
        Assert.DoesNotContain(missing, m => m.Code == "power-missing" && m.Things.Contains("fuzz"));

        // Once a need is on the person's list it stops being suggested; fixing the board makes the rest go away.
        model = await AppendAsync(
            new ObjectCreated("req", "Power for Tuner", WhatsMissing.RequirementKind, Lifecycle.Idea),
            Item("cable2", "Short patch", "cable", Jack("a", "tip", PortGender.Plug, PortDirection.Both), Jack("b", "tip", PortGender.Plug, PortDirection.Both)),
            new ConnectionRemoved("s1"),
            Connect("s3", "fuzz", "out", "cable2", "a"),
            Connect("s4", "cable2", "b", "delay", "in"),
            Amps("psu", null, "electrical.current.max", 1.0));

        needs = WhatsMissing.Derive(model).Select(m => m.Need).ToArray();
        Assert.DoesNotContain("Power for Tuner", needs);
        Assert.DoesNotContain(needs, n => n.StartsWith("Cable:", StringComparison.Ordinal));
        Assert.DoesNotContain(needs, n => n.StartsWith("More power", StringComparison.Ordinal));
        Assert.Contains("Plug in the other end of Patch lead", needs);
    }

    [Fact]
    public async Task PlugsThatDoNotFitAskForAnAdapterAndAThingWithNothingWiredStaysQuiet()
    {
        var model = await BuildAsync(
            Item("laptop", "Laptop", "device.computer",
                new PortSpec("usbc", "USB-C", "usb-c", PortGender.Socket, PortDirection.Both, ["usb-3"]),
                new PortSpec("eth", "Ethernet", "rj45-8p8c", PortGender.Socket, PortDirection.Input, ["ethernet"])),
            Item("drive", "Old drive", "device", new PortSpec("usb", "USB-A plug", "usb-a", PortGender.Plug, PortDirection.Both, ["usb-3"])),
            Item("desk", "Desk computer", "device.computer", new PortSpec("in", "Line in", "ts-635", PortGender.Socket, PortDirection.Input, ["analog-audio"])));

        Assert.Empty(WhatsMissing.Derive(model));

        model = await AppendAsync(Connect("c", "drive", "usb", "laptop", "usbc"));
        var missing = WhatsMissing.Derive(model);
        var adapter = Assert.Single(missing, m => m.Code == "adapter-needed");
        Assert.StartsWith("Adapter: ", adapter.Need, StringComparison.Ordinal);
        Assert.Equal(["drive", "laptop"], adapter.Things);
        Assert.Contains(missing, m => m.Need == "Something into Laptop Ethernet");
        Assert.DoesNotContain(missing, m => m.Things.Contains("desk"));
    }

    private static PortSpec Dc(string id, string name, PortGender gender, PortDirection direction) =>
        new(id, name, "dc-barrel-5521", gender, direction, ["dc-power"]);

    private static PortSpec Jack(string id, string name, PortGender gender, PortDirection direction) =>
        new(id, name, "ts-635", gender, direction, ["analog-audio"]);

    private static ModelChange[] Pedal(string id, string name, double drawAmps) =>
    [
        .. Item(id, name, "device.audio",
            Dc("dc", "DC in", PortGender.Socket, PortDirection.Input),
            Jack("in", "Input", PortGender.Socket, PortDirection.Input),
            Jack("out", "Output", PortGender.Socket, PortDirection.Output)),
        new PropertyAsserted(id, "electrical.voltage", PropertyValue.Number(9, "V"), Datasheet, "dc"),
        .. Amps(id, "dc", "electrical.current.draw", drawAmps),
    ];

    private static ModelChange[] Amps(string id, string? port, string key, double amps) =>
        [new PropertyAsserted(id, key, PropertyValue.Number(amps, "A"), Datasheet, port)];

    private static ModelChange[] Item(string id, string name, string kind, params PortSpec[] ports) =>
        [new ObjectCreated(id, name, kind, Lifecycle.Owned), .. ports.Select(p => new PortAdded(id, p))];

    private static ModelChange[] Connect(string id, string a, string aPort, string b, string bPort) =>
        [new ConnectionAdded(id, new PortRef(a, aPort), new PortRef(b, bPort))];

    private async Task<SystemModel> BuildAsync(params object[] parts)
    {
        Directory.CreateDirectory(directory);
        store = new ModelStore(Path.Combine(directory, "model.db"));
        await store.InitializeAsync();
        return await AppendAsync(parts);
    }

    private Task<SystemModel> AppendAsync(params object[] parts) =>
        store!.AppendAsync(parts.SelectMany(p => p switch
        {
            ModelChange[] many => many,
            ModelChange one => [one],
            _ => throw new ArgumentException("Not a change.", nameof(parts)),
        }), "test");

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
