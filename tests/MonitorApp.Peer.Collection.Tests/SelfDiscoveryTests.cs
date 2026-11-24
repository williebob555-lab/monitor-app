using System.Net;
using System.Text;
using MonitorApp.Peer.Collection.Discovery;
using MonitorApp.Peer.Collection.Hardware;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Collection.Services;
using Xunit;

namespace MonitorApp.Peer.Collection.Tests;

public sealed class SelfDiscoveryTests
{
    [Fact]
    public void MacVendorsUsesTheBundledRegistryAndRecognisesPrivateAddresses()
    {
        Assert.Equal("aa:bb:cc:dd:ee:ff", MacVendors.Normalize("AA-BB-CC-DD-EE-FF"));
        Assert.Null(MacVendors.Normalize("ff:ff:ff:ff:ff:ff"));
        Assert.Null(MacVendors.Normalize("01:00:5e:00:00:fb")); // multicast
        Assert.Null(MacVendors.Normalize("00:00:00:00:00:00"));
        Assert.Contains("Raspberry Pi", MacVendors.Lookup("b8:27:eb:12:34:56"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MacVendors.PrivateAddress, MacVendors.Lookup("da:a1:19:12:34:56"));
    }

    [Fact]
    public void NeighborTablesKeepOnlyCompleteUnicastEntries()
    {
        const string arp = """
            IP address       HW type     Flags       HW address            Mask     Device
            192.168.1.1      0x1         0x2         a0:63:91:00:00:01     *        wlp2s0
            192.168.1.20     0x1         0x0         00:00:00:00:00:00     *        wlp2s0
            192.168.1.30     0x1         0x2         ff:ff:ff:ff:ff:ff     *        wlp2s0
            """;
        var parsed = NeighborTables.ParseProcNetArp(arp);
        Assert.Equal([new Neighbor(IPAddress.Parse("192.168.1.1"), "a0:63:91:00:00:01")], parsed);

        var table = new byte[4 + (2 * 24)];
        BitConverter.GetBytes(2u).CopyTo(table, 0);
        WriteRow(table, 0, [0xa0, 0x63, 0x91, 0, 0, 2], [192, 168, 1, 2], type: 3);
        WriteRow(table, 1, [0xa0, 0x63, 0x91, 0, 0, 3], [192, 168, 1, 3], type: 2); // invalid
        Assert.Equal([new Neighbor(IPAddress.Parse("192.168.1.2"), "a0:63:91:00:00:02")], WindowsNeighborTable.Parse(table));
    }

    [Fact]
    public void MdnsAnswersBecomeNamesAndOffersAndJunkIsIgnored()
    {
        var source = IPAddress.Parse("192.168.1.40");
        var packet = Answer(
            Ptr("_googlecast._tcp.local", "Living Room TV._googlecast._tcp.local"),
            Txt("Living Room TV._googlecast._tcp.local", "fn=Living Room TV", "md=Chromecast with Google TV"),
            A("chromecast-1234.local", [192, 168, 1, 40]));
        var described = Assert.Single(Mdns.Describe([(source, packet), (source, [1, 2, 3]), (source, [0x00, 0x00, 0x84, 0x00, 0, 0, 0, 5, 0, 0, 0, 0, 0xC0])]));
        Assert.Equal("Living Room TV", described.FriendlyName);
        Assert.Equal("Chromecast with Google TV", described.Model);
        Assert.Equal("chromecast-1234", described.HostName);
        Assert.Contains("Chromecast", described.Services!);
    }

    [Fact]
    public void UpnpDescriptionGivesNameMakerAndModel()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
              <friendlyName>Home Router</friendlyName><manufacturer>NETGEAR</manufacturer><modelName>Nighthawk</modelName><modelNumber>R7000</modelNumber>
            </device></root>
            """;
        var description = Ssdp.ParseDescription(IPAddress.Parse("192.168.1.1"), xml)!;
        Assert.Equal(("Home Router", "NETGEAR", "Nighthawk R7000"), (description.FriendlyName, description.Maker, description.Model));
        Assert.Equal(["router"], description.Services);
        Assert.Equal(new Uri("http://192.168.1.1:5000/desc.xml"), Ssdp.Location("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/desc.xml\r\n\r\n"));
        Assert.Null(Ssdp.Location("HTTP/1.1 200 OK\r\nLOCATION: file:///etc/passwd\r\n\r\n"));
    }

    [Fact]
    public async Task DiscoveryStaysOnThisComputersOwnPrivateSubnet()
    {
        var probes = new FakeProbes();
        var interfaces = new FixedInterfaces(
            Nic("wlp2s0", "192.168.1.0/24"),
            Nic("eth1", "203.0.113.0/24"),   // public: never swept
            Nic("eth2", "10.0.0.0/16"));      // too large: never swept
        var neighbors = new FixedNeighbors(
            new Neighbor(IPAddress.Parse("192.168.1.1"), "a0:63:91:00:00:01"),
            new Neighbor(IPAddress.Parse("192.168.1.40"), "b8:27:eb:12:34:56"),
            new Neighbor(IPAddress.Parse("192.168.1.7"), "70:85:c2:00:00:07"),  // this computer
            new Neighbor(IPAddress.Parse("203.0.113.5"), "70:85:c2:00:00:05"));
        var discovery = new NetworkDiscovery(interfaces, neighbors, probes, ownAddresses: () => [IPAddress.Parse("192.168.1.7")]);

        var pass = await discovery.RunAsync(CancellationToken.None);

        Assert.Equal(["192.168.1.0/24"], pass.Networks);
        Assert.Equal(253, probes.Pinged.Count); // .1-.254 minus this computer
        Assert.All(probes.Pinged, ip => Assert.Equal("192.168.1", string.Join('.', ip.ToString().Split('.')[..3])));
        Assert.Equal(["192.168.1.1", "192.168.1.40"], pass.Devices.Select(d => d.Address.ToString()).Order());
        var router = pass.Devices.Single(d => d.IsGateway);
        Assert.Equal("device.network.router", router.Kind);
        var pi = pass.Devices.Single(d => !d.IsGateway);
        Assert.Equal("raspberrypi", pi.Name);
        Assert.Equal("device.computer", pi.Kind);
        Assert.True(pi.Answered);

        // A second pass inside 30 minutes does not ping again.
        probes.Pinged.Clear();
        await discovery.RunAsync(CancellationToken.None);
        Assert.Empty(probes.Pinged);

        var readings = NetworkDiscovery.Readings("peer:self", pass);
        Assert.Contains(readings, r => r.SubjectEntityId == "net:b827eb123456" && r.Metrics.Any(m => m.MetricId == DiscoverySignals.Name && m.Value!.StringValue == "raspberrypi"));
        Assert.Contains(readings, r => r.SubjectEntityId == "peer:self" && r.Metrics.Any(m => m.MetricId == DiscoverySignals.DevicesFound && m.Value!.UnsignedIntegerValue == 2));
    }

    [Fact]
    public void TailscaleDevicesAreListedWithTheirState()
    {
        const string json = """
            {"BackendState":"Running","Self":{"TailscaleIPs":["100.64.0.1"]},
             "Peer":{"nodekey:a":{"ID":"n1","HostName":"wilson-phone","DNSName":"wilson-phone.tail1234.ts.net.","OS":"android","TailscaleIPs":["100.64.0.2","fd7a::2"],"Online":true,"ExitNode":false},
                     "nodekey:b":{"ID":"bad id!","HostName":"x"}}}
            """;
        var device = Assert.Single(TailscaleStatusReader.ParseDevices(json));
        Assert.Equal(("n1", "wilson-phone", "android", "100.64.0.2", true), (device.Id, device.Name, device.Os, device.Address, device.Online));
    }

    [Fact]
    public void HardwareIsReadFromTheMachinesOwnFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "hw-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string path, string text)
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            Write("sys/class/dmi/id/sys_vendor", "LENOVO\n");
            Write("sys/class/dmi/id/product_name", "20XW0055US\n");
            Write("sys/class/dmi/id/product_version", "ThinkPad X1 Carbon Gen 9\n");
            Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: 11th Gen Intel(R) Core(TM) i7-1165G7\nphysical id\t: 0\ncore id\t: 0\n\nprocessor\t: 1\nphysical id\t: 0\ncore id\t: 0\n");
            Write("proc/meminfo", "MemTotal:       16000000 kB\n");
            Write("etc/os-release", "NAME=Fedora\nPRETTY_NAME=\"Fedora Linux 43 (Workstation Edition)\"\n");
            Write("sys/block/nvme0n1/size", "1000215216\n");
            Write("sys/block/nvme0n1/device/model", "Samsung SSD 980 1TB\n");
            Write("sys/block/loop0/size", "100\n");
            Directory.CreateDirectory(Path.Combine(root, "sys/firmware/efi/efivars"));
            File.WriteAllBytes(Path.Combine(root, "sys/firmware/efi/efivars/BootOrder-8be4df61-93ca-11d2-aa0d-00e098032b8c"), [7, 0, 0, 0, 1, 0, 0, 0, 2, 0]);
            File.WriteAllBytes(Path.Combine(root, "sys/firmware/efi/efivars/BootCurrent-8be4df61-93ca-11d2-aa0d-00e098032b8c"), [7, 0, 0, 0, 1, 0]);
            File.WriteAllBytes(Path.Combine(root, "sys/firmware/efi/efivars/Boot0001-8be4df61-93ca-11d2-aa0d-00e098032b8c"), LoadOption("Fedora"));
            File.WriteAllBytes(Path.Combine(root, "sys/firmware/efi/efivars/Boot0000-8be4df61-93ca-11d2-aa0d-00e098032b8c"), LoadOption("Windows Boot Manager"));
            File.WriteAllBytes(Path.Combine(root, "sys/firmware/efi/efivars/Boot0002-8be4df61-93ca-11d2-aa0d-00e098032b8c"), LoadOption("UEFI: PXE IPv4 Intel"));

            var facts = LinuxHardware.Read(root);

            Assert.Equal(("Lenovo", "ThinkPad X1 Carbon Gen 9"), (facts.Maker, facts.Model));
            Assert.Equal(("11th Gen Intel(R) Core(TM) i7-1165G7", 1, 2), (facts.Processor, facts.Cores, facts.Threads));
            Assert.Equal(16_000_000UL * 1024, facts.MemoryBytes);
            Assert.Equal("Fedora Linux 43 (Workstation Edition)", facts.OperatingSystem);
            var drive = Assert.Single(facts.Drives);
            Assert.Equal(("Samsung SSD 980 1TB", 1000215216UL * 512, "NVMe SSD"), (drive.Model, drive.SizeBytes, drive.Kind));
            Assert.Equal([new InstalledSystem("Fedora", true, true), new InstalledSystem("Windows Boot Manager", false, false)], facts.Systems);
            var metrics = HardwareSnapshotCollector.ToMetrics(facts);
            Assert.Contains(metrics, m => m.MetricId == HardwareSignals.Systems && m.Value!.StringValue == "Fedora (running now), Windows Boot Manager");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] LoadOption(string description)
    {
        var text = Encoding.Unicode.GetBytes(description + "\0");
        return [7, 0, 0, 0, 1, 0, 0, 0, 0, 0, .. text, 0x7f, 0xff, 0x04, 0x00];
    }

    private static void WriteRow(byte[] table, int index, byte[] mac, byte[] ip, int type)
    {
        var offset = 4 + (index * 24);
        BitConverter.GetBytes(6).CopyTo(table, offset + 4);
        mac.CopyTo(table, offset + 8);
        ip.CopyTo(table, offset + 16);
        BitConverter.GetBytes(type).CopyTo(table, offset + 20);
    }

    private static SelfInterface Nic(string name, string cidr) =>
        new(name, InterfaceMedium.Ethernet, true, 1000, "full", 1500, "70:85:c2:00:00:07", [cidr], null, null);

    private sealed class FixedInterfaces(params SelfInterface[] nics) : ISelfInterfaceCollector
    {
        public SelfInterfaceSnapshot Collect(DateTimeOffset now) =>
            new(SelfCollectionState.Collected, null, nics, [new SelfGateway("192.168.1.1", "wlp2s0")], now);
    }

    private sealed class FixedNeighbors(params Neighbor[] neighbors) : INeighborTable
    {
        public IReadOnlyList<Neighbor> Read() => neighbors;
    }

    private sealed class FakeProbes : ILocalNetworkProbes
    {
        public List<IPAddress> Pinged { get; } = [];

        public Task<IReadOnlySet<IPAddress>> PingAsync(IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken)
        {
            Pinged.AddRange(addresses);
            return Task.FromResult<IReadOnlySet<IPAddress>>(addresses.Where(a => a.ToString() is "192.168.1.1" or "192.168.1.40").ToHashSet());
        }

        public Task<IReadOnlyList<SelfDescription>> MdnsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SelfDescription>>([new SelfDescription(IPAddress.Parse("203.0.113.5"), "outside")]);

        public Task<IReadOnlyList<SelfDescription>> SsdpAsync(Func<IPAddress, bool> isLocal, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SelfDescription>>([]);

        public Task<string?> ReverseNameAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(address.ToString() == "192.168.1.40" ? "raspberrypi.lan" : null);
    }

    // Minimal DNS response builder for the mDNS test.
    private static byte[] Answer(params byte[][] records)
    {
        byte[] header = [0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0];
        return [.. header, .. records.SelectMany(r => r)];
    }

    private static byte[] Name(string name) =>
        [.. name.Split('.').SelectMany(label => (byte[])[(byte)label.Length, .. Encoding.UTF8.GetBytes(label)]), 0];

    private static byte[] Record(string name, ushort type, byte[] data) =>
        [.. Name(name), (byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] Ptr(string name, string target) => Record(name, 12, Name(target));

    private static byte[] A(string name, byte[] ip) => Record(name, 1, ip);

    private static byte[] Txt(string name, params string[] entries) =>
        Record(name, 16, [.. entries.SelectMany(e => (byte[])[(byte)Encoding.UTF8.GetByteCount(e), .. Encoding.UTF8.GetBytes(e)])]);
}
