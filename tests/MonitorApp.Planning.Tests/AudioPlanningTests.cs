using MonitorApp.Planning.Audio;
using MonitorApp.Planning.Racks;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class AudioPlanningTests
{
    private static AudioPort Port(string id, string device, AudioPortDirection direction, AudioLevel level,
        AudioConnector connector = AudioConnector.Ts635, AudioSignalKind kind = AudioSignalKind.AnalogAudio) =>
        new()
        {
            Id = id,
            DeviceId = device,
            Direction = direction,
            Kind = kind,
            Level = level,
            Connector = connector,
            Channels = 1,
            Balanced = false,
            ImpedanceOhms = direction == AudioPortDirection.Input ? 100000 : 500,
            MaximumLevelDbu = 18,
            ProcessingLatencyMs = 1,
        };

    private static AudioPlan SignalPlan(AudioPort source, AudioPort sink) =>
        new("pedal-design", 2,
            [new AudioDevice("bass", "Bass"), new AudioDevice("pedal", "Pedal")],
            [source, sink],
            [new AudioCable { Id = "patch", EndA = source.Connector, EndB = sink.Connector,
                MeasuredLengthMm = 400, RequiredRoutedLengthMm = 250, Shielded = true }],
            [new AudioPatch("p", source.Id, sink.Id, "patch")],
            [], [], []);

    [Fact]
    public void InstrumentPedalChainCanHaveNoErrorsWithKnownSignalParameters()
    {
        var plan = SignalPlan(Port("bass-out", "bass", AudioPortDirection.Output, AudioLevel.Instrument),
            Port("pedal-in", "pedal", AudioPortDirection.Input, AudioLevel.Instrument));
        Assert.False(AudioPlanEvaluator.Evaluate(plan).HasErrors);
        Assert.DoesNotContain(AudioPlanEvaluator.Evaluate(plan).Findings,
            f => f.Code == "unknown-signal-level" || f.Code == "connector-mismatch");
    }

    [Fact]
    public void AmplifiedSpeakerOutputMustNotFeedPedalInput()
    {
        var plan = SignalPlan(Port("amp-out", "bass", AudioPortDirection.Output, AudioLevel.Speaker),
            Port("pedal-in", "pedal", AudioPortDirection.Input, AudioLevel.Instrument));
        Assert.Contains(AudioPlanEvaluator.Evaluate(plan).Findings, f => f.Code == "speaker-into-low-level" &&
            f.Level == AudioFindingLevel.Error);
    }

    [Fact]
    public void EurorackAudioIntoLineLevelRequiresAnExplicitAttenuationReview()
    {
        var plan = SignalPlan(Port("modular", "bass", AudioPortDirection.Output, AudioLevel.EurorackModular,
            AudioConnector.Ts35),
            Port("interface", "pedal", AudioPortDirection.Input, AudioLevel.ProfessionalLine, AudioConnector.Ts635));
        Assert.Contains(AudioPlanEvaluator.Evaluate(plan).Findings, f => f.Code == "modular-level");
    }

    [Fact]
    public void TrsMidiTypeAAndBCannotBeAssumedInterchangeable()
    {
        var output = Port("midi-out", "bass", AudioPortDirection.Output, AudioLevel.Unknown,
            AudioConnector.Trs35, AudioSignalKind.Midi) with
        { MidiElectricalStandard = "TRS-A" };
        var input = Port("midi-in", "pedal", AudioPortDirection.Input, AudioLevel.Unknown,
            AudioConnector.Trs35, AudioSignalKind.Midi) with
        { MidiElectricalStandard = "TRS-B" };
        Assert.Contains(AudioPlanEvaluator.Evaluate(SignalPlan(output, input)).Findings,
            f => f.Code == "midi-electrical-standard" && f.Level == AudioFindingLevel.Unresolved);
    }

    [Fact]
    public void WrongPedalPowerVoltageAndPolarityAreErrorsNotWarnings()
    {
        var plan = new AudioPlan("pedal-design", 1,
            [new AudioDevice("pedal", "Digital pedal"), new AudioDevice("supply", "Power")],
            [], [], [],
            [new DcLoad
            {
                Id = "pedal-power", DeviceId = "pedal", Connector = AudioConnector.BarrelDc,
                ConnectorDimensions = "5.5/2.1", Polarity = PowerPolarity.CenterNegative,
                RequiredVoltage = 9, MaximumCurrentMa = 550, RequiresIsolatedSupply = true,
            }],
            [new DcSupplyOutput
            {
                Id = "out-1", SupplyDeviceId = "supply", Connector = AudioConnector.BarrelDc,
                ConnectorDimensions = "5.5/2.1", Polarity = PowerPolarity.CenterPositive,
                Voltage = 12, RatedCurrentMa = 400, IsolationGroupId = "isolated-1",
            }],
            [new DcPowerAssignment("pedal-power", "out-1")]);
        var findings = AudioPlanEvaluator.Evaluate(plan).Findings;
        Assert.Contains(findings, f => f.Code == "dc-polarity" && f.Level == AudioFindingLevel.Error);
        Assert.Contains(findings, f => f.Code == "dc-voltage" && f.Level == AudioFindingLevel.Error);
        Assert.Contains(findings, f => f.Code == "dc-current-overload" && f.Level == AudioFindingLevel.Error);
    }

    [Fact]
    public void SharedDaisyChainRequiresGroundAndIsolationReview()
    {
        var plan = new AudioPlan("pedal-design", 1,
            [new AudioDevice("one", "Pedal one"), new AudioDevice("two", "Pedal two"),
             new AudioDevice("psu", "Isolated PSU")],
            [], [], [],
            [
                new DcLoad { Id = "p1", DeviceId = "one", Connector = AudioConnector.BarrelDc,
                    Polarity = PowerPolarity.CenterNegative, RequiredVoltage = 9,
                    ConnectorDimensions = "5.5/2.1", MaximumCurrentMa = 80, RequiresIsolatedSupply = true },
                new DcLoad { Id = "p2", DeviceId = "two", Connector = AudioConnector.BarrelDc,
                    Polarity = PowerPolarity.CenterNegative, RequiredVoltage = 9,
                    ConnectorDimensions = "5.5/2.1", MaximumCurrentMa = 120, RequiresIsolatedSupply = false },
            ],
            [new DcSupplyOutput { Id = "out", SupplyDeviceId = "psu", Connector = AudioConnector.BarrelDc,
                Polarity = PowerPolarity.CenterNegative, Voltage = 9, RatedCurrentMa = 400,
                ConnectorDimensions = "5.5/2.1", IsolationGroupId = "group-1" }],
            [new DcPowerAssignment("p1", "out"), new DcPowerAssignment("p2", "out")]);
        var findings = AudioPlanEvaluator.Evaluate(plan).Findings;
        Assert.Contains(findings, f => f.Code == "shared-dc-output" && f.Level == AudioFindingLevel.Warning);
        Assert.Contains(findings, f => f.Code == "isolation-not-dedicated" && f.Level == AudioFindingLevel.Error);
    }

    [Fact]
    public void PedalboardRequiresRealTopDownSpacePowerUnderneathAndPatchCableFootprint()
    {
        var board = new RackZone
        {
            Id = "board",
            Format = RackFormat.Pedalboard,
            WidthMm = 450,
            HeightMm = 300,
            UsableDepthMm = 90,
            UnderboardClearanceMm = 18,
            MaxWeightKg = 10,
            PowerBudgetWatts = 40,
            HeatRemovalWatts = 40,
        };
        RackEquipment Pedal(string id) => new()
        {
            Id = id,
            Name = id,
            MountFormat = RackFormat.Pedalboard,
            MountMethod = MountingMethod.HookAndLoop,
            WidthMm = 75,
            HeightMm = 130,
            BodyDepthMm = 60,
            LeftCableClearanceMm = 15,
            RightCableClearanceMm = 15,
            TopCableClearanceMm = 20,
            BottomCableClearanceMm = 5,
            FootAccessClearanceMm = 12,
            UnderboardRequiredMm = 22,
            WeightKg = 0.6m,
            PowerWatts = 3,
            HeatWatts = 3
        };
        var pedalA = Pedal("p1");
        var pedalB = Pedal("p2");
        var layout = new RackLayout("pedal-design", 1, [board], [pedalA, pedalB],
        [
            RackDimensions.AtBoard("a", "board", "p1", 30, 50),
            RackDimensions.AtBoard("b", "board", "p2", 110, 50),
        ]);
        var findings = RackLayoutValidator.Evaluate(layout).Findings;
        Assert.Contains(findings, f => f.Code == "volume-collision"); // cable exits overlap despite pedal bodies clearing
        Assert.Contains(findings, f => f.Code == "underboard-collision");
        Assert.DoesNotContain(findings, f => f.Code == "rack-grid" || f.Code == "unknown-u-footprint");
        var impossible = layout with
        {
            Placements = [RackDimensions.AtBoard("a", "board", "p1", 380, 250)]
        };
        Assert.Contains(RackLayoutValidator.Evaluate(impossible).Findings, f => f.Code == "outside-zone");
    }

    [Fact]
    public void PedalboardAndRackedHardwareDoNotAutoShareMountingInterface()
    {
        var layout = new RackLayout("pedal", 1,
            [new RackZone { Id = "board", Format = RackFormat.Pedalboard,
                WidthMm = 450, HeightMm = 280, UsableDepthMm = 100 }],
            [new RackEquipment { Id = "rack-fx", Name = "Rack FX",
                MountFormat = RackFormat.Eia19, MountMethod = MountingMethod.FrontRails,
                WidthMm = 420, HeightMm = 43, BodyDepthMm = 240 }],
            [RackDimensions.AtBoard("p", "board", "rack-fx", 0, 0)]);
        var issues = RackLayoutValidator.Evaluate(layout).Findings;
        Assert.Contains(issues, f => f.Code == "mount-interface");
        Assert.Contains(issues, f => f.Code == "surface-fastening");
    }
}
