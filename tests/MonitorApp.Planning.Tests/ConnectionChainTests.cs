using MonitorApp.Model;
using MonitorApp.Planning;
using MonitorApp.Planning.Model;
using Xunit;

namespace MonitorApp.Planning.Tests;

/// <summary>
/// Scenario 8 (docs/SCENARIOS.md) over the shared model: the examples are only data, and one engine explains each.
/// </summary>
public sealed class ConnectionChainTests : IDisposable
{
    private static readonly Evidence Datasheet = new(EvidenceLevel.Specified, "datasheet");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-connection-chain-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UsbCFitsButTheMonitorNeedsDisplayPortAltModeTheLaptopPortLacks()
    {
        var model = await BuildAsync(
            Item("laptop", "Laptop", "device.laptop", new PortSpec("usbc", "USB-C", "usb-c", PortGender.Socket, PortDirection.Both, ["usb-3", "usb-pd"])),
            Item("cable", "USB-C cable", "cable",
                new PortSpec("a", "end A", "usb-c", PortGender.Plug, PortDirection.Both, ["usb-3", "usb-pd", "dp-alt"]),
                new PortSpec("b", "end B", "usb-c", PortGender.Plug, PortDirection.Both, ["usb-3", "usb-pd", "dp-alt"])),
            Item("monitor", "Monitor", "device", new PortSpec("in", "USB-C in", "usb-c", PortGender.Socket, PortDirection.Input, ["dp-alt"])),
            Connect("c1", "laptop", "usbc", "cable", "a"),
            Connect("c2", "cable", "b", "monitor", "in"));

        var report = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("monitor", "in"))!);

        Assert.Equal("Laptop USB-C → USB-C cable → Monitor USB-C in", report.Route);
        Assert.Equal(ConnectionVerdict.WillNotWork, report.Verdict);
        var problem = Assert.Single(report.Findings, f => f.IsProblem);
        Assert.Equal("signal-mismatch", problem.Code);
        Assert.Equal("Monitor USB-C in needs dp-alt, but what reaches it is usb-3 or usb-pd.", problem.Explanation);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("connector", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuarterInchPlugsFitButLineLevelIntoAnInstrumentInputIsFlaggedAndADiBoxFixesIt()
    {
        var model = await BuildAsync(
            Item("synth", "Synth", "device.instrument", new PortSpec("out", "line out", "ts-635", PortGender.Socket, PortDirection.Output, ["analog-audio"])),
            Item("lead", "Instrument lead", "cable",
                new PortSpec("a", "tip", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"]),
                new PortSpec("b", "tip", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"])),
            Item("pedal", "Fuzz pedal", "device.audio.effect", new PortSpec("in", "input", "ts-635", PortGender.Socket, PortDirection.Input, ["analog-audio"])),
            Level("synth", "out", "line"),
            Level("pedal", "in", "instrument"),
            Connect("c1", "synth", "out", "lead", "a"),
            Connect("c2", "lead", "b", "pedal", "in"));

        var report = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("synth", "out"))!);
        var level = Assert.Single(report.Findings, f => f.IsProblem);
        Assert.Equal(("audio-level-mismatch", FindingCertainty.VerifiedProblem), (level.Code, level.Certainty));
        Assert.Equal("Synth line out sends line level, but Fuzz pedal input expects instrument level.", level.Explanation);

        // Insert a re-amp box (an active converter) between the lead and the pedal: the whole path is evaluated again.
        model = await AppendAsync(
            Item("reamp", "Re-amp box", "adapter",
                new PortSpec("in", "line in", "ts-635", PortGender.Socket, PortDirection.Input, ["analog-audio"]),
                new PortSpec("out", "instrument out", "ts-635", PortGender.Socket, PortDirection.Output, ["analog-audio"])),
            Item("patch", "Patch lead", "cable",
                new PortSpec("a", "tip", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"]),
                new PortSpec("b", "tip", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"])),
            new PropertyAsserted("reamp", ConnectionChains.ActiveKey, PropertyValue.Of(true), Datasheet),
            Level("reamp", "in", "line"),
            Level("reamp", "out", "instrument"),
            new ConnectionRemoved("c2"),
            Connect("c3", "lead", "b", "reamp", "in"),
            Connect("c4", "reamp", "out", "patch", "a"),
            Connect("c5", "patch", "b", "pedal", "in"));

        var fixedReport = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("pedal", "in"))!);
        Assert.Equal("Synth line out → Instrument lead → Re-amp box → Patch lead → Fuzz pedal input", fixedReport.Route);
        Assert.DoesNotContain(fixedReport.Findings, f => f.IsProblem);
    }

    [Fact]
    public async Task ADcPlugThatFitsWithTheWrongVoltageOrPolarityIsCaughtAndTypedValuesAreOnlyLikely()
    {
        var model = await BuildAsync(
            Item("psu", "18 V supply", "device.power.supply", new PortSpec("out", "DC out", "dc-barrel-5521", PortGender.Plug, PortDirection.Output, ["dc-fixed"])),
            Item("pedal", "Delay pedal", "device.audio.effect", new PortSpec("dc", "9 V in", "dc-barrel-5521", PortGender.Socket, PortDirection.Input, ["dc-fixed"])),
            Volts("psu", "out", 18, Datasheet),
            Volts("pedal", "dc", 9, Datasheet),
            Polarity("psu", "out", "center-positive", Datasheet),
            Polarity("pedal", "dc", "center-negative", Evidence.UserEntered),
            Connect("c1", "psu", "out", "pedal", "dc"));

        var report = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("pedal", "dc"))!);

        Assert.Equal(ConnectionVerdict.WillNotWork, report.Verdict);
        var voltage = Assert.Single(report.Findings, f => f.Code == "voltage-mismatch");
        Assert.Equal(FindingCertainty.VerifiedProblem, voltage.Certainty);
        Assert.Equal("18 V supply DC out supplies 18 V, but Delay pedal 9 V in accepts 9 V.", voltage.Explanation);
        var polarity = Assert.Single(report.Findings, f => f.Code == "polarity-mismatch");
        Assert.Equal(FindingCertainty.LikelyProblem, polarity.Certainty); // the pedal's polarity was typed, not from a spec.
        Assert.Equal(["pedal#dc"], polarity.Subjects);
    }

    [Fact]
    public async Task MissingFactsAreUnknownNotOkAndAMatchingPathIsOkOnlyAsDeclared()
    {
        var model = await BuildAsync(
            Item("switch", "Switch", "device.network.switch", new PortSpec("p1", "port 1", "rj45-8p8c", PortGender.Socket, PortDirection.Both, ["ethernet"])),
            Item("patch", "Patch cable", "cable",
                new PortSpec("a", "end A", "rj45-8p8c", PortGender.Plug, PortDirection.Both),
                new PortSpec("b", "end B", "rj45-8p8c", PortGender.Plug, PortDirection.Both)),
            Item("nas", "NAS", "device.server", new PortSpec("eth", "LAN", "rj45-8p8c", PortGender.Socket, PortDirection.Both, ["ethernet"])),
            Item("odd", "Mystery box", "device", new PortSpec("x", "port"), new PortSpec("y", "other port")),
            Connect("c1", "switch", "p1", "patch", "a"),
            Connect("c2", "patch", "b", "nas", "eth"));

        var unknown = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("switch", "p1"))!);
        Assert.Equal(ConnectionVerdict.CannotTellYet, unknown.Verdict);
        Assert.Equal("What Patch cable can carry is not recorded.", Assert.Single(unknown.Findings).Explanation);

        model = await AppendAsync(
            new ConnectionRemoved("c1"), new ConnectionRemoved("c2"),
            new PortRemoved("patch", "a"), new PortRemoved("patch", "b"),
            new PortAdded("patch", new PortSpec("a", "end A", "rj45-8p8c", PortGender.Plug, PortDirection.Both, ["ethernet"])),
            new PortAdded("patch", new PortSpec("b", "end B", "rj45-8p8c", PortGender.Plug, PortDirection.Both, ["ethernet"])),
            Connect("c3", "switch", "p1", "patch", "a"),
            Connect("c4", "patch", "b", "nas", "eth"));
        var ok = Assert.Single(ConnectionChains.ReportsFor(model, "nas"));
        Assert.Equal(ConnectionVerdict.WorksAsRecorded, ok.Verdict);
        Assert.Equal(FindingCertainty.OkAsDeclared, Assert.Single(ok.Findings).Certainty);
        Assert.Empty(ConnectionChains.ReportsFor(model, "odd"));
        Assert.Empty(ConnectionChains.ReportsFor(model, "patch")); // cables are part of a path, not the end of one.
    }

    [Fact]
    public async Task PlugsThatDoNotMateAndALooseCableEndAreReported()
    {
        var model = await BuildAsync(
            Item("mixer", "Mixer", "device.audio", new PortSpec("out", "main out", "xlr3", PortGender.Plug, PortDirection.Output, ["analog-audio"])),
            Item("lead", "Jack lead", "cable",
                new PortSpec("a", "end A", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"]),
                new PortSpec("b", "end B", "ts-635", PortGender.Plug, PortDirection.Both, ["analog-audio"])),
            Connect("c1", "mixer", "out", "lead", "a"));

        var report = ConnectionChains.Evaluate(model, ConnectionChains.Trace(model, new PortRef("mixer", "out"))!);

        Assert.Contains(report.Findings, f => f.Code == "connector-mismatch" && f.Explanation.StartsWith("XLR 3-pin does not plug into 6.35 mm TS", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Code == "gender-mismatch");
        Assert.Contains(report.Findings, f => f.Code == "path-open" && f.Certainty == FindingCertainty.Unknown);
        Assert.Equal("Mixer main out → Jack lead → (nothing)", report.Route);
    }

    [Fact]
    public async Task ModelPortsAreViewedAsPlanningPortsWithModelIdsAndTheirOwnElectricalValues()
    {
        var model = await BuildAsync(
            Item("psu", "Supply", "device.power.supply", new PortSpec("out", "DC out", "dc-barrel-5521", PortGender.Plug, PortDirection.Output, ["dc-fixed"])),
            Volts("psu", "out", 9, Datasheet),
            Polarity("psu", "out", "center-negative", Datasheet),
            new PropertyAsserted("psu", "electrical.current.max", PropertyValue.Number(500, "mA"), Datasheet, "out"));

        var port = ModelPlanningViews.ToConnectionPort(model.Get("psu"), model.Get("psu").Port("out")!);

        Assert.Equal(("psu#out", "psu", "dc-barrel-5521"), (port.Id, port.DeviceId, port.ConnectorTypeId));
        Assert.Equal((Connections.ConnectorGender.Plug, Connections.ConnectionDirection.Source), (port.Gender, port.Direction));
        Assert.Equal(new Connections.ElectricalRange(9m, 9m, 0.5m, null, "center-negative"), port.Power);
        Assert.Equal("datasheet", port.VerifiedSpecification);
        Assert.Contains("dc-fixed", port.ConfirmedProtocols);
    }

    private static ModelChange[] Item(string id, string name, string kind, params PortSpec[] ports) =>
        [new ObjectCreated(id, name, kind, Lifecycle.Owned), .. ports.Select(p => new PortAdded(id, p))];

    private static ModelChange[] Connect(string id, string a, string aPort, string b, string bPort) =>
        [new ConnectionAdded(id, new PortRef(a, aPort), new PortRef(b, bPort))];

    private static ModelChange[] Level(string id, string port, string level) =>
        [new PropertyAsserted(id, "audio.level", PropertyValue.Of(level), Datasheet, port)];

    private static ModelChange[] Volts(string id, string port, double volts, Evidence evidence) =>
        [new PropertyAsserted(id, "electrical.voltage", PropertyValue.Number(volts, "V"), evidence, port)];

    private static ModelChange[] Polarity(string id, string port, string polarity, Evidence evidence) =>
        [new PropertyAsserted(id, "electrical.polarity", PropertyValue.Of(polarity), evidence, port)];

    private ModelStore? store;

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
