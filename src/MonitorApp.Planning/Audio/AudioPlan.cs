namespace MonitorApp.Planning.Audio;

/// <summary>
/// Audio, MIDI, CV and power remain distinct physical/electrical domains.
/// Connector shape by itself is never proof of signal or voltage compatibility.
/// </summary>
public enum AudioSignalKind
{
    AnalogAudio = 1, DigitalAudio = 2, Midi = 3, ControlVoltage = 4, Gate = 5,
    WordClock = 6, UsbAudio = 7, RadioFrequency = 8,
}
public enum AudioLevel
{
    Unknown = 0, Microphone = 1, Instrument = 2, ConsumerLine = 3, ProfessionalLine = 4,
    Speaker = 5, EurorackModular = 6, ControlVoltage = 7,
}
public enum AudioConnector
{
    Unknown = 0, Ts635 = 1, Trs635 = 2, Ts35 = 3, Trs35 = 4, Trs25 = 5,
    Xlr3 = 6, Xlr5 = 7, Rca = 8, Speakon = 9, Bnc = 10, OpticalToslink = 11,
    MidiDin5 = 12, UsbA = 13, UsbB = 14, UsbC = 15, BarrelDc = 16,
    Banana = 17, DSub25 = 18, TerminalBlock = 19, EighthInchModular = 20,
}
public enum AudioPortDirection { Input = 1, Output = 2, Bidirectional = 3 }
public enum AudioFindingLevel { Error = 1, Warning = 2, Unresolved = 3, Advisory = 4 }
public enum PowerPolarity { Unknown = 0, CenterNegative = 1, CenterPositive = 2, Ac = 3 }

public sealed record AudioDevice(string Id, string DisplayName, string? PlannedRackEquipmentId = null);
public sealed record AudioPort
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required AudioPortDirection Direction { get; init; }
    public required AudioSignalKind Kind { get; init; }
    public required AudioConnector Connector { get; init; }
    public required AudioLevel Level { get; init; }
    public int? Channels { get; init; }
    public bool? Balanced { get; init; }
    public decimal? NominalLevelDbu { get; init; }
    public decimal? ImpedanceOhms { get; init; }
    public decimal? MaximumLevelDbu { get; init; }
    public bool? PhantomPowerEnabled { get; init; }
    public string? MidiElectricalStandard { get; init; } // e.g. TRS-A, TRS-B; not inferred from TRS barrel
    public string? VoltagePerOctaveMode { get; init; }  // Volt/Oct vs Hz/V etc are non-equivalent
    public decimal? ProcessingLatencyMs { get; init; }
}

public sealed record AudioCable
{
    public required string Id { get; init; }
    public required AudioConnector EndA { get; init; }
    public required AudioConnector EndB { get; init; }
    public decimal? MeasuredLengthMm { get; init; }
    public decimal? RequiredRoutedLengthMm { get; init; }
    public bool? Shielded { get; init; }
    public string? ExplicitAdapterId { get; init; }
    public string? VerifiedElectricalConversion { get; init; }
}

public sealed record AudioPatch(string Id, string FromOutputPortId, string ToInputPortId, string CableId);

public sealed record DcLoad
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required AudioConnector Connector { get; init; }
    public required PowerPolarity Polarity { get; init; }
    public decimal? RequiredVoltage { get; init; }
    public decimal? MaximumCurrentMa { get; init; }
    public bool? RequiresIsolatedSupply { get; init; }
    public string? ConnectorDimensions { get; init; } // 2.1 mm vs 2.5 mm center pin, etc.
}

public sealed record DcSupplyOutput
{
    public required string Id { get; init; }
    public required string SupplyDeviceId { get; init; }
    public required AudioConnector Connector { get; init; }
    public required PowerPolarity Polarity { get; init; }
    public decimal? Voltage { get; init; }
    public decimal? RatedCurrentMa { get; init; }
    public string? IsolationGroupId { get; init; }
    public string? ConnectorDimensions { get; init; }
}

public sealed record DcPowerAssignment(string LoadId, string SupplyOutputId);

public sealed record AudioPlan(
    string ScenarioId,
    int Revision,
    IReadOnlyList<AudioDevice> Devices,
    IReadOnlyList<AudioPort> Ports,
    IReadOnlyList<AudioCable> Cables,
    IReadOnlyList<AudioPatch> Patches,
    IReadOnlyList<DcLoad> DcLoads,
    IReadOnlyList<DcSupplyOutput> DcOutputs,
    IReadOnlyList<DcPowerAssignment> DcAssignments,
    decimal? MaximumDesiredChainLatencyMs = null);

public sealed record AudioFinding(
    string Code, AudioFindingLevel Level, string? SubjectId, string Detail,
    IReadOnlyList<string> Evidence, string RuleVersion = "audio-plan/1");

public sealed record AudioEvaluation(IReadOnlyList<AudioFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Level == AudioFindingLevel.Error);
    public bool HasUnknowns => Findings.Any(f => f.Level == AudioFindingLevel.Unresolved);
}

/// <summary>Traceable, design-only audio patch and DC-power validation. Never certifies safe hardware wiring.</summary>
public static class AudioPlanEvaluator
{
    public static AudioEvaluation Evaluate(AudioPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var findings = new List<AudioFinding>();
        var devices = Index(plan.Devices, x => x.Id, "duplicate-device");
        var ports = Index(plan.Ports, x => x.Id, "duplicate-port");
        var cables = Index(plan.Cables, x => x.Id, "duplicate-cable");
        var loads = Index(plan.DcLoads, x => x.Id, "duplicate-dc-load");
        var outputs = Index(plan.DcOutputs, x => x.Id, "duplicate-dc-output");

        foreach (var port in ports.Values)
        {
            if (!devices.ContainsKey(port.DeviceId))
                Add("missing-device", AudioFindingLevel.Error, port.Id, "Audio port references an unknown device.");
            if (port.Channels is null or < 1)
                Add("unknown-channels", AudioFindingLevel.Unresolved, port.Id, "Channel count is not specified.");
            if (port.ImpedanceOhms is < 0 || port.ProcessingLatencyMs is < 0)
                Add("invalid-port-fact", AudioFindingLevel.Error, port.Id, "Impedance and latency cannot be negative.");
        }

        foreach (var patch in plan.Patches)
        {
            if (!ports.TryGetValue(patch.FromOutputPortId, out var from) ||
                !ports.TryGetValue(patch.ToInputPortId, out var to) ||
                !cables.TryGetValue(patch.CableId, out var cable))
            {
                Add("missing-patch-reference", AudioFindingLevel.Error, patch.Id, "A patch references a missing port or cable.");
                continue;
            }
            if (from.Direction == AudioPortDirection.Input || to.Direction == AudioPortDirection.Output)
                Add("port-direction", AudioFindingLevel.Error, patch.Id, "Connection must leave an output and enter an input.");
            if (from.Kind != to.Kind)
            {
                Add("signal-protocol", AudioFindingLevel.Error, patch.Id,
                    $"{from.Kind} and {to.Kind} require an explicit converter; matching connectors do not convert signals.");
                continue;
            }
            if (from.Connector == AudioConnector.Unknown || to.Connector == AudioConnector.Unknown ||
                cable.EndA == AudioConnector.Unknown || cable.EndB == AudioConnector.Unknown)
                Add("unknown-audio-connector", AudioFindingLevel.Unresolved, patch.Id,
                    "Exact connector identities are not specified.");
            else if (cable.EndA != from.Connector || cable.EndB != to.Connector)
                Add("connector-mismatch", AudioFindingLevel.Error, patch.Id,
                    "Cable ends do not match device ports; specify a documented physical adapter.");
            if (cable.MeasuredLengthMm is null || cable.RequiredRoutedLengthMm is null)
                Add("unknown-patch-route", AudioFindingLevel.Unresolved, patch.Id,
                    "Cable length or routed path is unknown; slack cannot be verified.");
            else if (cable.MeasuredLengthMm < cable.RequiredRoutedLengthMm)
                Add("patch-cable-short", AudioFindingLevel.Error, patch.Id, "The patch cable does not reach its routed endpoint.");

            if (from.Kind == AudioSignalKind.AnalogAudio)
            {
                if (from.Level == AudioLevel.Unknown || to.Level == AudioLevel.Unknown)
                    Add("unknown-signal-level", AudioFindingLevel.Unresolved, patch.Id,
                        "Signal-level handling cannot be inferred from connector geometry.");
                else if (from.Level == AudioLevel.Speaker && to.Level != AudioLevel.Speaker)
                    Add("speaker-into-low-level", AudioFindingLevel.Error, patch.Id,
                        "Amplified speaker power must not be fed into a line, mic, modular or instrument input.");
                else if (from.Level == AudioLevel.EurorackModular &&
                         to.Level is AudioLevel.Microphone or AudioLevel.Instrument or AudioLevel.ConsumerLine or AudioLevel.ProfessionalLine)
                    Add("modular-level", AudioFindingLevel.Warning, patch.Id,
                        "Eurorack signal level may overload conventional audio inputs; use attenuation and verify headroom.");
                else if (from.Level != to.Level)
                    Add("gain-staging", AudioFindingLevel.Warning, patch.Id,
                        $"From {from.Level} to {to.Level}: level conversion or gain staging may be needed.");
                if (from.Balanced is null || to.Balanced is null)
                    Add("unknown-balance", AudioFindingLevel.Unresolved, patch.Id, "Balanced/unbalanced wiring is not confirmed.");
                else if (from.Balanced != to.Balanced)
                    Add("balance-conversion", AudioFindingLevel.Warning, patch.Id,
                        "Balanced versus unbalanced connection requires a correct wiring scheme and may affect noise.");
                if (from.ImpedanceOhms is null || to.ImpedanceOhms is null)
                    Add("unknown-impedance", AudioFindingLevel.Unresolved, patch.Id, "Output and input impedances are not both known.");
                else if (from.ImpedanceOhms <= 0 || to.ImpedanceOhms <= 0)
                    Add("invalid-impedance", AudioFindingLevel.Error, patch.Id, "Impedances must be strictly positive.");
                else if (to.ImpedanceOhms < 10 * from.ImpedanceOhms)
                    Add("impedance-loading", AudioFindingLevel.Warning, patch.Id,
                        "Input impedance is less than 10 times source output impedance; expected bridging margin is low.");
                if (to.PhantomPowerEnabled == true)
                    Add("phantom-power-review", AudioFindingLevel.Warning, patch.Id,
                        "Input phantom power is enabled: verify the connected source can tolerate the actual wiring.");
                if (from.MaximumLevelDbu is { } sourceMax && to.MaximumLevelDbu is { } sinkMax && sourceMax > sinkMax)
                    Add("headroom", AudioFindingLevel.Warning, patch.Id,
                        "Output maximum level exceeds input maximum headroom; padding or attenuation may be needed.");
            }

            if (from.Channels is { } sourceChannels && to.Channels is { } sinkChannels &&
                sourceChannels > sinkChannels)
                Add("channel-downmix", AudioFindingLevel.Warning, patch.Id,
                    "More output channels than input channels; an explicit downmix or split is required.");

            if (from.Kind == AudioSignalKind.Midi &&
                (!string.Equals(from.MidiElectricalStandard, to.MidiElectricalStandard, StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(from.MidiElectricalStandard)))
                Add("midi-electrical-standard", AudioFindingLevel.Unresolved, patch.Id,
                    "MIDI electrical subtype differs or is unknown (e.g., TRS MIDI Type A versus B).");

            if (from.Kind == AudioSignalKind.ControlVoltage &&
                (!string.Equals(from.VoltagePerOctaveMode, to.VoltagePerOctaveMode, StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(from.VoltagePerOctaveMode)))
                Add("cv-scaling", AudioFindingLevel.Warning, patch.Id,
                    "CV scaling may differ (e.g., 1V/oct versus Hz/V); calibration or a converter is needed.");
        }

        foreach (var load in loads.Values)
            if (!devices.ContainsKey(load.DeviceId))
                Add("missing-dc-device", AudioFindingLevel.Error, load.Id, "DC load references an unknown device.");

        foreach (var supply in outputs.Values)
            if (!devices.ContainsKey(supply.SupplyDeviceId))
                Add("missing-supply-device", AudioFindingLevel.Error, supply.Id, "Power source references an unknown device.");

        var outputAssignments = new Dictionary<string, List<DcLoad>>(StringComparer.Ordinal);
        foreach (var assignment in plan.DcAssignments)
        {
            if (!loads.TryGetValue(assignment.LoadId, out var load) ||
                !outputs.TryGetValue(assignment.SupplyOutputId, out var supply))
            {
                Add("missing-power-reference", AudioFindingLevel.Error, assignment.LoadId,
                    "DC assignment references an unknown load or source.");
                continue;
            }
            if (!outputAssignments.TryGetValue(supply.Id, out var group))
                outputAssignments[supply.Id] = group = [];
            group.Add(load);
            if (load.Connector != supply.Connector)
                Add("dc-connector", AudioFindingLevel.Error, load.Id,
                    "DC supply and device have mismatched connector families.");
            if (load.ConnectorDimensions is null || supply.ConnectorDimensions is null)
                Add("unknown-barrel-dimensions", AudioFindingLevel.Unresolved, load.Id,
                    "Exact barrel-pin size/keying is not known.");
            else if (load.ConnectorDimensions != supply.ConnectorDimensions)
                Add("dc-connector-dimensions", AudioFindingLevel.Error, load.Id,
                    "DC connector sizes differ even if the barrel connector family matches.");
            if (load.Polarity == PowerPolarity.Unknown || supply.Polarity == PowerPolarity.Unknown)
                Add("dc-polarity-unknown", AudioFindingLevel.Unresolved, load.Id, "Power polarity is unknown.");
            else if (load.Polarity != supply.Polarity)
                Add("dc-polarity", AudioFindingLevel.Error, load.Id,
                    "DC polarity/AC power type does not match the connected device.");
            if (load.RequiredVoltage is null || supply.Voltage is null)
                Add("dc-voltage-unknown", AudioFindingLevel.Unresolved, load.Id,
                    "Operating voltage is unknown; a connector fitting is not proof of compatible power.");
            else if (load.RequiredVoltage != supply.Voltage)
                Add("dc-voltage", AudioFindingLevel.Error, load.Id,
                    "Power-supply voltage differs from the device's explicitly required voltage.");
            if (load.MaximumCurrentMa is null || supply.RatedCurrentMa is null)
                Add("dc-current-unknown", AudioFindingLevel.Unresolved, load.Id,
                    "Load current or source rated current is unknown.");
            if (load.RequiresIsolatedSupply == true && string.IsNullOrWhiteSpace(supply.IsolationGroupId))
                Add("dc-isolation-unknown", AudioFindingLevel.Unresolved, load.Id,
                    "The device requests isolated power but the output isolation group is unknown.");
        }

        foreach (var (outputId, consumers) in outputAssignments)
        {
            var output = outputs[outputId];
            if (consumers.Count > 1)
            {
                Add("shared-dc-output", AudioFindingLevel.Warning, outputId,
                    "Multiple pedals share a DC output; noise, startup demand and ground paths require review.");
                if (consumers.Any(c => c.RequiresIsolatedSupply == true))
                    Add("isolation-not-dedicated", AudioFindingLevel.Error, outputId,
                        "A device requiring isolation cannot share a DC output.");
            }
            if (consumers.Any(c => c.MaximumCurrentMa is null) || output.RatedCurrentMa is null)
                Add("dc-current-budget-unresolved", AudioFindingLevel.Unresolved, outputId,
                    "Unknown load current prevents checking source capacity.");
            else if (consumers.Sum(c => c.MaximumCurrentMa!.Value) > output.RatedCurrentMa)
                Add("dc-current-overload", AudioFindingLevel.Error, outputId,
                    "The combined rated load current exceeds supply output capacity.");
        }

        foreach (var load in loads.Values)
            if (!plan.DcAssignments.Any(a => a.LoadId == load.Id))
                Add("unpowered-device", AudioFindingLevel.Unresolved, load.Id,
                    "No power source is assigned for the planned device.");

        if (plan.MaximumDesiredChainLatencyMs is { } budget)
        {
            // Per-port processing delay is counted once per actual path edge endpoint, not summed over
            // all devices in a graph where parallel branches could inflate the result.
            var perDevice = ports.Values.Where(p => p.ProcessingLatencyMs is >= 0)
                .GroupBy(p => p.DeviceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(p => p.ProcessingLatencyMs!.Value), StringComparer.Ordinal);
            var longest = ComputeLongestPath(perDevice, ports, plan.Patches);
            if (longest is null)
                Add("latency-path-unknown", AudioFindingLevel.Unresolved, null,
                    "Signal graph contains a feedback cycle; latency cannot be summarized by a simple linear chain.");
            else if (longest > budget)
                Add("latency-budget", AudioFindingLevel.Warning, null,
                    $"Estimated longest processing path {longest:0.##} ms exceeds the planned {budget:0.##} ms target.");
            if (ports.Values.Any(p => p.ProcessingLatencyMs is null))
                Add("processing-latency-unknown", AudioFindingLevel.Unresolved, null,
                    "At least one port lacks a latency specification; the estimate is not a complete end-to-end budget.");
        }

        return new AudioEvaluation(findings);

        Dictionary<string, T> Index<T>(IEnumerable<T> source, Func<T, string> key, string code)
        {
            var dict = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in source)
                if (string.IsNullOrWhiteSpace(key(item)) || !dict.TryAdd(key(item), item))
                    Add(code, AudioFindingLevel.Error, key(item), "Identifiers must be nonempty and unique.");
            return dict;
        }

        void Add(string code, AudioFindingLevel level, string? id, string detail) =>
            findings.Add(new AudioFinding(code, level, id, detail, ["Source: design scenario; no physical verification."]));
    }

    private static decimal? ComputeLongestPath(
        IReadOnlyDictionary<string, decimal> deviceLatency,
        IReadOnlyDictionary<string, AudioPort> ports,
        IReadOnlyList<AudioPatch> patches)
    {
        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var patch in patches)
        {
            if (!ports.TryGetValue(patch.FromOutputPortId, out var from) ||
                !ports.TryGetValue(patch.ToInputPortId, out var to)) continue;
            if (!paths.TryGetValue(from.DeviceId, out var destinations))
                paths[from.DeviceId] = destinations = [];
            destinations.Add(to.DeviceId);
        }
        var colors = new Dictionary<string, int>(StringComparer.Ordinal);
        var memo = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal? Visit(string device)
        {
            if (colors.GetValueOrDefault(device) == 1) return null;
            if (colors.GetValueOrDefault(device) == 2) return memo[device];
            colors[device] = 1;
            var downstream = 0m;
            if (paths.TryGetValue(device, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    var value = Visit(neighbor);
                    if (value is null) return null;
                    downstream = decimal.Max(downstream, value.Value);
                }
            }
            colors[device] = 2;
            return memo[device] = deviceLatency.GetValueOrDefault(device) + downstream;
        }
        foreach (var device in deviceLatency.Keys.Concat(paths.Keys))
            if (Visit(device) is null) return null;
        return memo.Count == 0 ? 0 : memo.Values.Max();
    }
}
