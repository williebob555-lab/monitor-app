using System.Collections.ObjectModel;

namespace MonitorApp.Planning.Connections;

public enum ConnectionDomain
{
    Display = 1, Usb = 2, Network = 3, Power = 4, Audio = 5,
    RadioFrequency = 6, DataStorage = 7, Control = 8, Internal = 9,
}

public enum ConnectorGender { Plug = 1, Receptacle = 2, Genderless = 3, Unknown = 4 }

/// <summary>
/// Knowledge of a mechanical connector. A fit key only says what can physically mate:
/// it does not assert wiring, signal/protocol, power, gender, direction or version compatibility.
/// For example, USB-C may carry USB, DisplayPort alt mode, Thunderbolt, or just power.
/// </summary>
public sealed record ConnectorType(
    string Id,
    string Label,
    ConnectionDomain Domain,
    string FitKey,
    IReadOnlySet<string> PotentialProtocols,
    string? MechanicalNote = null);

/// <summary>
/// Expandable, versioned, case-insensitive catalog. Specific shapes and pin counts are separate IDs.
/// A vendor adapter can add definitions without changing the built-in registry or serialized IDs.
/// </summary>
public sealed class ConnectorCatalog
{
    private readonly ReadOnlyDictionary<string, ConnectorType> types;
    public string Version { get; }
    public IReadOnlyCollection<ConnectorType> Types => types.Values;

    public ConnectorCatalog(string version, IEnumerable<ConnectorType> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        var index = new Dictionary<string, ConnectorType>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (string.IsNullOrWhiteSpace(definition.Id) ||
                string.IsNullOrWhiteSpace(definition.FitKey) ||
                !index.TryAdd(definition.Id, definition))
                throw new ArgumentException("Every connector needs a unique stable ID and fit key.", nameof(definitions));
        }

        types = new ReadOnlyDictionary<string, ConnectorType>(index);
    }

    public bool TryGet(string id, out ConnectorType? type) =>
        types.TryGetValue(id, out type);

    public ConnectorCatalog Extend(IEnumerable<ConnectorType> additional) =>
        new(Version, types.Values.Concat(additional));

    public static ConnectorCatalog BuiltIn { get; } = BuildDefaults();

    private static ConnectorCatalog BuildDefaults()
    {
        var d = new List<ConnectorType>();
        void Add(ConnectionDomain domain, string fit, string protocols, params string[] entries)
        {
            var allowed = protocols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var line in entries)
            {
                var pair = line.Split('|', 2);
                d.Add(new ConnectorType(pair[0], pair.Length == 2 ? pair[1] : pair[0],
                    domain, fit, allowed));
            }
        }

        // USB connector shape and USB protocol are distinct. Alternate modes are capability claims.
        Add(ConnectionDomain.Usb, "usb-a", "usb-1,usb-2,usb-3,usb-bc",
            "usb-a|USB Type-A", "usb-a-ss|USB Type-A SuperSpeed");
        Add(ConnectionDomain.Usb, "usb-b", "usb-1,usb-2", "usb-b|USB Type-B");
        Add(ConnectionDomain.Usb, "usb-b3", "usb-3", "usb-b3|USB 3.x Type-B");
        Add(ConnectionDomain.Usb, "mini-usb-b", "usb-2", "mini-usb-b|Mini-USB Type-B");
        Add(ConnectionDomain.Usb, "micro-usb-b", "usb-2,usb-bc", "micro-usb-b|Micro-USB Type-B");
        Add(ConnectionDomain.Usb, "micro-usb-b3", "usb-3", "micro-usb-b3|Micro-USB 3.x Type-B");
        Add(ConnectionDomain.Usb, "usb-c", "usb-2,usb-3,usb4,usb-pd,dp-alt,thunderbolt-3,thunderbolt-4,thunderbolt-5",
            "usb-c|USB Type-C");
        Add(ConnectionDomain.Usb, "apple-lightning", "lightning,usb-2", "lightning|Apple Lightning");
        Add(ConnectionDomain.Usb, "usb-micro-ab", "usb-2", "usb-micro-ab|Micro-USB Type-AB");
        Add(ConnectionDomain.Usb, "usb-mini-a", "usb-2", "usb-mini-a|Mini-USB Type-A");

        Add(ConnectionDomain.Display, "hdmi-a", "hdmi-tmds,hdmi-frl", "hdmi-a|HDMI Type-A");
        Add(ConnectionDomain.Display, "hdmi-c", "hdmi-tmds,hdmi-frl", "hdmi-c|Mini HDMI Type-C");
        Add(ConnectionDomain.Display, "hdmi-d", "hdmi-tmds,hdmi-frl", "hdmi-d|Micro HDMI Type-D");
        Add(ConnectionDomain.Display, "displayport", "displayport,dp++", "displayport|DisplayPort");
        Add(ConnectionDomain.Display, "mini-displayport", "displayport,thunderbolt-1,thunderbolt-2",
            "mini-displayport|Mini DisplayPort / Thunderbolt 1-2");
        Add(ConnectionDomain.Display, "dvi-d-sl", "dvi-d-tmds", "dvi-d-single|DVI-D Single Link");
        Add(ConnectionDomain.Display, "dvi-d-dl", "dvi-d-tmds", "dvi-d-dual|DVI-D Dual Link");
        Add(ConnectionDomain.Display, "dvi-i-sl", "dvi-d-tmds,analog-rgb", "dvi-i-single|DVI-I Single Link");
        Add(ConnectionDomain.Display, "dvi-i-dl", "dvi-d-tmds,analog-rgb", "dvi-i-dual|DVI-I Dual Link");
        Add(ConnectionDomain.Display, "dvi-a", "analog-rgb", "dvi-analog|DVI-A");
        Add(ConnectionDomain.Display, "vga-de15", "analog-rgb", "vga-de15|VGA / DE-15");
        Add(ConnectionDomain.Display, "svideo-mini-din4", "s-video", "svideo|S-Video Mini-DIN 4");
        Add(ConnectionDomain.Display, "scart", "analog-rgb,composite-video", "scart|SCART");
        Add(ConnectionDomain.Display, "sdi-bnc", "sdi,12g-sdi,3g-sdi,hd-sdi", "sdi-bnc|Serial Digital Interface BNC");
        Add(ConnectionDomain.Display, "composite-rca", "composite-video", "composite-rca|Composite video RCA");

        Add(ConnectionDomain.Network, "8p8c", "ethernet,poe,ethercon,aes50,dante",
            "rj45-8p8c|8P8C / RJ45-style network connector",
            "ethercon|Neutrik etherCON 8P8C insert");
        Add(ConnectionDomain.Network, "6p2c", "telephone", "rj11-6p2c|RJ11 6P2C");
        Add(ConnectionDomain.Network, "6p4c", "telephone", "rj11-6p4c|RJ11 6P4C");
        Add(ConnectionDomain.Network, "6p6c", "telephone", "rj12-6p6c|RJ12 6P6C");
        Add(ConnectionDomain.Network, "sfp", "ethernet,fiber-channel", "sfp|SFP transceiver cage");
        Add(ConnectionDomain.Network, "sfp-plus", "ethernet,fiber-channel", "sfp-plus|SFP+ transceiver cage");
        Add(ConnectionDomain.Network, "sfp28", "ethernet", "sfp28|SFP28 transceiver cage");
        Add(ConnectionDomain.Network, "qsfp-plus", "ethernet", "qsfp-plus|QSFP+ cage");
        Add(ConnectionDomain.Network, "qsfp28", "ethernet", "qsfp28|QSFP28 cage");
        Add(ConnectionDomain.Network, "qsfp56", "ethernet", "qsfp56|QSFP56 cage");
        Add(ConnectionDomain.Network, "qsfp-dd", "ethernet", "qsfp-dd|QSFP-DD cage");
        Add(ConnectionDomain.Network, "lc-duplex", "optical", "lc-duplex|LC duplex fiber");
        Add(ConnectionDomain.Network, "lc-simplex", "optical", "lc-simplex|LC simplex fiber");
        Add(ConnectionDomain.Network, "sc", "optical", "sc|SC fiber");
        Add(ConnectionDomain.Network, "st-fiber", "optical", "st-fiber|ST fiber");
        Add(ConnectionDomain.Network, "fc-fiber", "optical", "fc-fiber|FC fiber");
        Add(ConnectionDomain.Network, "mpo-mtp", "optical", "mpo-mtp|MPO / MTP fiber");
        Add(ConnectionDomain.Network, "mtrj", "optical", "mtrj|MT-RJ fiber");

        Add(ConnectionDomain.Audio, "xlr3", "analog-audio,aes3,phantom-48v", "xlr3|XLR 3-pin");
        Add(ConnectionDomain.Audio, "xlr4", "intercom,headset-dc", "xlr4|XLR 4-pin");
        Add(ConnectionDomain.Audio, "xlr5", "dmx512,intercom", "xlr5|XLR 5-pin");
        Add(ConnectionDomain.Audio, "xlr7", "headset,intercom", "xlr7|XLR 7-pin");
        Add(ConnectionDomain.Audio, "ts-635", "analog-audio,instrument,speaker", "ts-635|6.35 mm TS / 1/4-inch mono jack");
        Add(ConnectionDomain.Audio, "trs-635", "analog-audio,balanced-audio,stereo,insert",
            "trs-635|6.35 mm TRS / 1/4-inch stereo jack");
        Add(ConnectionDomain.Audio, "trrs-635", "analog-audio,headset", "trrs-635|6.35 mm TRRS");
        Add(ConnectionDomain.Audio, "ts-35", "analog-audio,cv,gate", "ts-35|3.5 mm TS / Eurorack patch");
        Add(ConnectionDomain.Audio, "trs-35", "analog-audio,stereo,trs-midi,headset", "trs-35|3.5 mm TRS");
        Add(ConnectionDomain.Audio, "trrs-35", "analog-audio,headset", "trrs-35|3.5 mm TRRS");
        Add(ConnectionDomain.Audio, "trs-25", "analog-audio,balanced-audio,stereo", "trs-25|2.5 mm TRS");
        Add(ConnectionDomain.Audio, "trrs-25", "analog-audio,headset", "trrs-25|2.5 mm TRRS");
        Add(ConnectionDomain.Audio, "rca", "analog-audio,spdif-coax,composite-video",
            "rca|RCA / phono");
        Add(ConnectionDomain.Audio, "din5-180", "midi-din,analog-audio", "din5-180|5-pin DIN 180° MIDI");
        Add(ConnectionDomain.Audio, "din5-240", "analog-audio", "din5-240|5-pin DIN 240°");
        Add(ConnectionDomain.Audio, "speakon-nl2", "speaker", "speakon-nl2|speakON NL2");
        Add(ConnectionDomain.Audio, "speakon-nl4", "speaker", "speakon-nl4|speakON NL4");
        Add(ConnectionDomain.Audio, "speakon-nl8", "speaker", "speakon-nl8|speakON NL8");
        Add(ConnectionDomain.Audio, "db25", "analog-audio,aes3", "db25-audio|D-sub 25-pin analog/AES audio");
        Add(ConnectionDomain.Audio, "toslink", "adat,spdif-optical", "toslink|TOSLINK optical audio");
        Add(ConnectionDomain.Audio, "mini-toslink", "adat,spdif-optical", "mini-toslink|Mini-TOSLINK optical");
        Add(ConnectionDomain.Audio, "wordclock-bnc", "word-clock,spdif-coax,madi-coax", "wordclock-bnc|Word clock / MADI BNC");

        Add(ConnectionDomain.Power, "nema-5-15", "ac-120v", "nema-5-15|NEMA 5-15");
        Add(ConnectionDomain.Power, "nema-5-20", "ac-120v", "nema-5-20|NEMA 5-20");
        Add(ConnectionDomain.Power, "nema-6-15", "ac-240v", "nema-6-15|NEMA 6-15");
        Add(ConnectionDomain.Power, "nema-6-20", "ac-240v", "nema-6-20|NEMA 6-20");
        Add(ConnectionDomain.Power, "iec-c5-c6", "ac-mains", "iec-c5-c6|IEC C5/C6 cloverleaf");
        Add(ConnectionDomain.Power, "iec-c7-c8", "ac-mains", "iec-c7-c8|IEC C7/C8 figure-eight");
        Add(ConnectionDomain.Power, "iec-c13-c14", "ac-mains", "iec-c13-c14|IEC C13/C14");
        Add(ConnectionDomain.Power, "iec-c15-c16", "ac-mains", "iec-c15-c16|IEC C15/C16 high-temperature");
        Add(ConnectionDomain.Power, "iec-c19-c20", "ac-mains", "iec-c19-c20|IEC C19/C20");
        Add(ConnectionDomain.Power, "iec-60309", "ac-mains", "iec-60309|IEC 60309 industrial");
        Add(ConnectionDomain.Power, "powercon-a", "ac-mains", "powercon-a|powerCON A (power-in)");
        Add(ConnectionDomain.Power, "powercon-b", "ac-mains", "powercon-b|powerCON B (power-out)");
        Add(ConnectionDomain.Power, "powercon-true1", "ac-mains", "powercon-true1|powerCON TRUE1");
        Add(ConnectionDomain.Power, "barrel-55-21", "dc-fixed", "dc-barrel-5521|DC barrel 5.5/2.1 mm");
        Add(ConnectionDomain.Power, "barrel-55-25", "dc-fixed", "dc-barrel-5525|DC barrel 5.5/2.5 mm");
        Add(ConnectionDomain.Power, "barrel-35-135", "dc-fixed", "dc-barrel-35135|DC barrel 3.5/1.35 mm");
        Add(ConnectionDomain.Power, "xt30", "dc-fixed", "xt30|XT30");
        Add(ConnectionDomain.Power, "xt60", "dc-fixed", "xt60|XT60");
        Add(ConnectionDomain.Power, "anderson-pp", "dc-fixed", "anderson-powerpole|Anderson Powerpole");
        Add(ConnectionDomain.Power, "atx24", "pc-power", "atx-24|ATX 24-pin");
        Add(ConnectionDomain.Power, "eps8", "pc-power", "eps-8|EPS CPU 8-pin");
        Add(ConnectionDomain.Power, "pcie6", "pc-power", "pcie-6|PCIe GPU 6-pin");
        Add(ConnectionDomain.Power, "pcie8", "pc-power", "pcie-8|PCIe GPU 8-pin");
        Add(ConnectionDomain.Power, "pcie12v2x6", "pc-power", "12v-2x6|PCIe 12V-2x6");
        Add(ConnectionDomain.Power, "sata-power", "pc-power", "sata-power|SATA 15-pin power");
        Add(ConnectionDomain.Power, "molex-peripheral", "pc-power", "molex-4pin|Molex 4-pin peripheral");

        Add(ConnectionDomain.DataStorage, "sata-data", "sata", "sata7|SATA 7-pin");
        Add(ConnectionDomain.DataStorage, "sas-sff8087", "sas", "sff8087|SFF-8087 internal mini-SAS");
        Add(ConnectionDomain.DataStorage, "sas-sff8088", "sas", "sff8088|SFF-8088 external mini-SAS");
        Add(ConnectionDomain.DataStorage, "sas-sff8643", "sas", "sff8643|SFF-8643 internal mini-SAS HD");
        Add(ConnectionDomain.DataStorage, "sas-sff8644", "sas", "sff8644|SFF-8644 external mini-SAS HD");
        Add(ConnectionDomain.DataStorage, "sff8639", "nvme,pcie,sas", "u2-sff8639|U.2 / SFF-8639");
        Add(ConnectionDomain.DataStorage, "oculink", "pcie", "oculink|OCuLink");
        Add(ConnectionDomain.DataStorage, "slimsas", "pcie,sas", "slimsas|SlimSAS");
        Add(ConnectionDomain.DataStorage, "m2-m", "nvme,pcie,sata", "m2-key-m|M.2 Key M");
        Add(ConnectionDomain.DataStorage, "m2-b", "pcie,sata", "m2-key-b|M.2 Key B");
        Add(ConnectionDomain.DataStorage, "m2-ae", "pcie,usb", "m2-key-ae|M.2 Key A/E");
        Add(ConnectionDomain.DataStorage, "pcie-x1", "pcie", "pcie-x1|PCIe x1");
        Add(ConnectionDomain.DataStorage, "pcie-x4", "pcie", "pcie-x4|PCIe x4");
        Add(ConnectionDomain.DataStorage, "pcie-x8", "pcie", "pcie-x8|PCIe x8");
        Add(ConnectionDomain.DataStorage, "pcie-x16", "pcie", "pcie-x16|PCIe x16");

        Add(ConnectionDomain.RadioFrequency, "sma", "rf-coax", "sma|SMA");
        Add(ConnectionDomain.RadioFrequency, "rp-sma", "rf-coax", "rp-sma|Reverse-polarity SMA");
        Add(ConnectionDomain.RadioFrequency, "bnc", "rf-coax,word-clock", "bnc|BNC");
        Add(ConnectionDomain.RadioFrequency, "tnc", "rf-coax", "tnc|TNC");
        Add(ConnectionDomain.RadioFrequency, "rp-tnc", "rf-coax", "rp-tnc|Reverse-polarity TNC");
        Add(ConnectionDomain.RadioFrequency, "n-type", "rf-coax", "n-type|N-type RF");
        Add(ConnectionDomain.RadioFrequency, "f-type", "rf-coax,catv", "f-type|F-type coaxial");
        Add(ConnectionDomain.RadioFrequency, "mcx", "rf-coax", "mcx|MCX");
        Add(ConnectionDomain.RadioFrequency, "mmcx", "rf-coax", "mmcx|MMCX");
        Add(ConnectionDomain.RadioFrequency, "ufl", "rf-coax", "u-fl|U.FL / I-PEX MHF");
        Add(ConnectionDomain.RadioFrequency, "fakra", "rf-coax", "fakra|FAKRA keyed coax");
        Add(ConnectionDomain.RadioFrequency, "smb", "rf-coax", "smb|SMB");

        Add(ConnectionDomain.Control, "dmx-xlr5", "dmx512,rdm", "dmx-xlr5|DMX512 XLR-5");
        Add(ConnectionDomain.Control, "can-db9", "can-bus", "can-db9|CAN bus D-sub 9");
        Add(ConnectionDomain.Control, "rs232-db9", "rs232", "rs232-db9|RS-232 DE-9");
        Add(ConnectionDomain.Control, "rs485-terminal", "rs485", "rs485-terminal|RS-485 terminal block");
        Add(ConnectionDomain.Control, "jst-ph-2", "dc-fixed", "jst-ph2|JST PH 2-pin");
        Add(ConnectionDomain.Control, "jst-xh-2", "dc-fixed", "jst-xh2|JST XH 2-pin");
        Add(ConnectionDomain.Control, "jst-sh-4", "i2c", "qwiic-jst-sh4|JST SH 4-pin / Qwiic");
        Add(ConnectionDomain.Control, "grove-4", "i2c,uart,analog", "grove-4|Grove 4-pin");
        Add(ConnectionDomain.Control, "gpio254", "gpio,i2c,spi,uart", "gpio-254|2.54 mm GPIO header");
        Add(ConnectionDomain.Control, "fan-3pin", "fan-power,tach", "fan-3|3-pin PC fan");
        Add(ConnectionDomain.Control, "fan-4pin", "fan-power,tach,pwm", "fan-4|4-pin PWM PC fan");

        return new ConnectorCatalog("connector-registry/1", d);
    }
}
