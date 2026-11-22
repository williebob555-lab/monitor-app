using MonitorApp.Planning.Connections;
using Xunit;

namespace MonitorApp.Planning.Tests;

[Trait("Category", "Unit")]
public sealed class ConnectorCompatibilityTests
{
    private static IReadOnlySet<string> Set(params string[] x) => x.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static ConnectionPort P(string id, string type, ConnectionDirection direction, params string[] signals) =>
        new(id, id, type, ConnectorGender.Receptacle, direction, Set(signals),
            Pinout: "standard-fixture", VerifiedSpecification: "fixture");
    private static ConnectionCable C(string first, string second, params string[] signals) =>
        new("test-cable", new(first, ConnectorGender.Plug), new(second, ConnectorGender.Plug),
            Set(signals), PinoutVerified: true, LengthMetres: 1m, MaximumCurrentAmps: 3m,
            Shielding: "shielded", CertificationSource: "fixture");

    [Fact]
    public void RegistryHasBroadCoverageAndRejectsSilentOverrides()
    {
        var catalog = ConnectorCatalog.BuiltIn;
        Assert.True(catalog.Types.Count >= 110, "The catalog should include a broad variety of connectors.");
        foreach (var id in new[] { "usb-c", "hdmi-a", "displayport", "dvi-d-sl", "vga-de15",
            "xlr3", "trs-635", "ts-35", "rj45-8p8c", "lc-duplex", "sfp28", "dc-barrel-5521",
            "dc-barrel-5525", "iec-c13-c14", "sma", "rp-sma", "pcie-x16", "m2-key-m" })
            Assert.True(catalog.TryGet(id, out _), "Missing " + id);
        Assert.True(catalog.TryGet("USB-C", out _));
        Assert.NotEqual(catalog.Types.Single(t => t.Id == "sma").FitKey,
            catalog.Types.Single(t => t.Id == "rp-sma").FitKey);
        var type = new ConnectorType("custom-seven", "Custom 7-pin", ConnectionDomain.Internal, "unique-fit", Set("vendor"));
        Assert.True(catalog.Extend([type]).TryGet("custom-seven", out _));
        Assert.Throws<ArgumentException>(() => catalog.Extend([type, type]));
    }

    [Fact]
    public void ExplicitHdmiCableCanBePlausibleButPhysicalAndProtocolDifferencesAreErrors()
    {
        var evaluator = new ConnectionCompatibilityEvaluator();
        var a = P("gpu", "hdmi-a", ConnectionDirection.Source, "hdmi-tmds");
        var b = P("screen", "hdmi-a", ConnectionDirection.Sink, "hdmi-tmds");
        Assert.Equal(ConnectionAssessment.PotentiallyCompatible,
            evaluator.EvaluateDirect(a, b, C("hdmi-a", "hdmi-a", "hdmi-tmds")).Assessment);
        Assert.Contains(evaluator.EvaluateDirect(a, b with { ConnectorTypeId = "displayport" },
            C("hdmi-a", "hdmi-a", "hdmi-tmds")).Findings, f => f.Code == "physical-mismatch");
        Assert.Contains(evaluator.EvaluateDirect(a, b with { ConfirmedProtocols = Set("displayport") },
            C("hdmi-a", "hdmi-a", "hdmi-tmds")).Findings, f => f.Code == "protocol-incompatible");
    }

    [Fact]
    public void UsbCShapeDoesNotImplyDisplayPortAltModeOrThunderbolt()
    {
        var a = P("laptop", "usb-c", ConnectionDirection.Source, "usb-3");
        var b = P("display", "usb-c", ConnectionDirection.Sink, "dp-alt");
        var result = new ConnectionCompatibilityEvaluator().EvaluateDirect(a, b,
            C("usb-c", "usb-c", "usb-3", "dp-alt"));
        Assert.Equal(ConnectionAssessment.Incompatible, result.Assessment);
        Assert.Contains(result.Findings, f => f.Code == "protocol-incompatible");
        var unknown = new ConnectionCable("unknown", new("usb-c", ConnectorGender.Plug),
            new("usb-c", ConnectorGender.Plug), Set(), null, null);
        Assert.Equal(ConnectionAssessment.Unresolved, new ConnectionCompatibilityEvaluator()
            .EvaluateDirect(a with { ConfirmedProtocols = Set("dp-alt") }, b, unknown).Assessment);
    }

    [Fact]
    public void PolarityBarrelPinSizeAndRfReversePolarityMustNeverBeConflated()
    {
        var evaluator = new ConnectionCompatibilityEvaluator();
        var radio = evaluator.EvaluateDirect(
            P("radio", "sma", ConnectionDirection.Source, "rf-coax"),
            P("antenna", "rp-sma", ConnectionDirection.Sink, "rf-coax"),
            C("sma", "sma", "rf-coax"));
        Assert.Contains(radio.Findings, x => x.Code == "physical-mismatch");

        var supply = P("psu", "dc-barrel-5521", ConnectionDirection.Source, "dc-fixed") with
        { Power = new ElectricalRange(12, 12, 0.5m, null, "center-positive") };
        var device = P("pedal", "dc-barrel-5525", ConnectionDirection.Sink, "dc-fixed") with
        { Power = new ElectricalRange(9, 9, null, 1m, "center-negative") };
        var result = evaluator.EvaluateDirect(supply, device,
            C("dc-barrel-5521", "dc-barrel-5521", "dc-fixed") with { MaximumCurrentAmps = 0.75m });
        foreach (var code in new[] { "physical-mismatch", "voltage-mismatch",
                     "polarity-mismatch", "supply-overload", "cable-overcurrent" })
            Assert.Contains(result.Findings, x => x.Code == code);
    }

    [Fact]
    public void NegotiatedUsbPowerAndUnverifiedAdapterRemainUnresolved()
    {
        var supply = P("charger", "usb-c", ConnectionDirection.Source, "usb-pd") with
        { Power = new ElectricalRange(5, 20, 3, null, "negotiated", true) };
        var laptop = P("laptop", "usb-c", ConnectionDirection.Sink, "usb-pd") with
        { Power = new ElectricalRange(9, 20, null, 3, "negotiated", true) };
        var result = new ConnectionCompatibilityEvaluator().EvaluateDirect(supply, laptop,
            C("usb-c", "usb-c", "usb-pd"));
        Assert.Equal(ConnectionAssessment.Unresolved, result.Assessment);
        Assert.Contains(result.Findings, x => x.Code == "power-negotiation-unknown");

        var hdmi = P("gpu", "hdmi-a", ConnectionDirection.Source, "hdmi-tmds");
        var dp = P("display", "displayport", ConnectionDirection.Sink, "displayport");
        var adapter = new AdapterCapability("hdmi-to-dp",
            new("hdmi-a", ConnectorGender.Plug), new("displayport", ConnectorGender.Plug),
            Set("hdmi-tmds"), Set("displayport"), ActiveConversion: true, Verified: false);
        Assert.Equal(ConnectionAssessment.Unresolved, new ConnectionCompatibilityEvaluator()
            .EvaluateAdapter(hdmi, dp, adapter).Assessment);
        var passive = new ConnectionCompatibilityEvaluator().EvaluateAdapter(hdmi, dp,
            adapter with { ActiveConversion = false });
        Assert.Contains(passive.Findings, x => x.Code == "passive-conversion-not-supported");
    }
}
