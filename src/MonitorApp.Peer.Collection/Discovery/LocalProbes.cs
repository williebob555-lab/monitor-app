using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace MonitorApp.Peer.Collection.Discovery;

/// <summary>What one device said about itself on the local network (mDNS or UPnP). Every field is optional.</summary>
public sealed record SelfDescription(
    IPAddress Address,
    string? HostName = null,
    string? FriendlyName = null,
    string? Maker = null,
    string? Model = null,
    string? DeviceType = null,
    IReadOnlyList<string>? Services = null);

/// <summary>
/// The questions discovery asks on the local network. Every probe stays on this computer's own subnet (link-local
/// multicast, or unicast to private addresses on it), is bounded in time and size, and never sends credentials.
/// </summary>
public interface ILocalNetworkProbes
{
    /// <summary>Pings the addresses, a few at a time; returns the ones that answered.</summary>
    Task<IReadOnlySet<IPAddress>> PingAsync(IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken);

    /// <summary>One mDNS round (DNS-SD service types devices announce): what answered and what it offers.</summary>
    Task<IReadOnlyList<SelfDescription>> MdnsAsync(CancellationToken cancellationToken);

    /// <summary>One SSDP M-SEARCH, then each device's own UPnP description (name, maker, model).</summary>
    Task<IReadOnlyList<SelfDescription>> SsdpAsync(Func<IPAddress, bool> isLocal, CancellationToken cancellationToken);

    /// <summary>The name the local DNS (usually the router) has for the address, or null.</summary>
    Task<string?> ReverseNameAsync(IPAddress address, CancellationToken cancellationToken);
}

public sealed class LocalNetworkProbes : ILocalNetworkProbes
{
    public const int PingParallelism = 16;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan ListenWindow = TimeSpan.FromSeconds(3);
    private const int MaximumDescriptionBytes = 64 * 1024;

    /// <summary>The DNS-SD types asked for, with the plain words shown for each.</summary>
    public static IReadOnlyDictionary<string, string> ServiceWords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["_ipp._tcp"] = "printing",
        ["_ipps._tcp"] = "printing",
        ["_printer._tcp"] = "printing",
        ["_pdl-datastream._tcp"] = "printing",
        ["_scanner._tcp"] = "scanning",
        ["_uscan._tcp"] = "scanning",
        ["_googlecast._tcp"] = "Chromecast",
        ["_airplay._tcp"] = "AirPlay",
        ["_raop._tcp"] = "AirPlay audio",
        ["_spotify-connect._tcp"] = "Spotify Connect",
        ["_sonos._tcp"] = "Sonos",
        ["_hap._tcp"] = "HomeKit",
        ["_matter._tcp"] = "Matter",
        ["_homeassistant._tcp"] = "Home Assistant",
        ["_smb._tcp"] = "file sharing",
        ["_afpovertls._tcp"] = "file sharing",
        ["_nfs._tcp"] = "file sharing",
        ["_ssh._tcp"] = "SSH",
        ["_sftp-ssh._tcp"] = "SSH",
        ["_workstation._tcp"] = "computer",
        ["_device-info._tcp"] = "computer",
        ["_companion-link._tcp"] = "Apple device",
        ["_http._tcp"] = "web page",
        ["_https._tcp"] = "web page",
        ["_syncthing._tcp"] = "Syncthing",
        ["_amzn-wplay._tcp"] = "Fire TV",
        ["_androidtvremote2._tcp"] = "Android TV",
        ["_nvstream._tcp"] = "game streaming",
        ["_esphomelib._tcp"] = "ESPHome",
        ["_mqtt._tcp"] = "MQTT",
    };

    public async Task<IReadOnlySet<IPAddress>> PingAsync(IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken)
    {
        var answered = new HashSet<IPAddress>();
        var gate = new object();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = PingParallelism, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(address, PingTimeout, null, null, token).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        lock (gate) answered.Add(address);
                    }
                }
                catch (Exception exception) when (exception is PingException or SocketException or InvalidOperationException)
                {
                    // Not answering is an answer.
                }
            }).ConfigureAwait(false);
        return answered;
    }

    public async Task<IReadOnlyList<SelfDescription>> MdnsAsync(CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var query = Mdns.BuildQuery(ServiceWords.Keys.Select(type => type + ".local").Prepend("_services._dns-sd._udp.local"));
        var target = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        await socket.SendAsync(query, target, cancellationToken).ConfigureAwait(false);
        var packets = await ReceiveAllAsync(socket, ListenWindow, cancellationToken).ConfigureAwait(false);
        return Mdns.Describe(packets.Select(p => (p.Source, p.Bytes)));
    }

    public async Task<IReadOnlyList<SelfDescription>> SsdpAsync(Func<IPAddress, bool> isLocal, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        var search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
        await socket.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), cancellationToken).ConfigureAwait(false);
        var packets = await ReceiveAllAsync(socket, ListenWindow, cancellationToken).ConfigureAwait(false);
        var locations = new Dictionary<IPAddress, Uri>();
        foreach (var (source, bytes) in packets)
        {
            if (!isLocal(source) || locations.ContainsKey(source)) continue;
            if (Ssdp.Location(Encoding.ASCII.GetString(bytes)) is { } location && IPAddress.TryParse(location.Host, out var host) && host.Equals(source))
                locations[source] = location; // only a description the responder serves itself, on this network
        }

        var result = new List<SelfDescription>();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
            MaxResponseContentBufferSize = MaximumDescriptionBytes,
        };
        foreach (var (address, location) in locations.Take(64))
        {
            try
            {
                var xml = await http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
                if (Ssdp.ParseDescription(address, xml) is { } description) result.Add(description);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidOperationException)
            {
                result.Add(new SelfDescription(address, Services: ["UPnP"]));
            }
        }

        return result;
    }

    public async Task<string?> ReverseNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            var name = entry.HostName;
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<List<(IPAddress Source, byte[] Bytes)>> ReceiveAllAsync(UdpClient socket, TimeSpan window, CancellationToken cancellationToken)
    {
        var packets = new List<(IPAddress, byte[])>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        try
        {
            while (packets.Count < 512)
            {
                var received = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                packets.Add((received.RemoteEndPoint.Address, received.Buffer));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listening window ended.
        }
        catch (SocketException)
        {
            // A closed or refused socket ends the round with what arrived.
        }

        return packets;
    }
}

/// <summary>Just enough DNS to ask mDNS questions and read the answers (RFC 6762/6763).</summary>
public static class Mdns
{
    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    /// <summary>A query with one PTR question per name, each asking for a unicast reply (sent from a normal port).</summary>
    public static byte[] BuildQuery(IEnumerable<string> names)
    {
        var questions = names.ToList();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)questions.Count);
        stream.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var name in questions)
        {
            foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes);
            }

            stream.WriteByte(0);
            BinaryPrimitives.WriteUInt16BigEndian(tail, TypePtr);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 0x8001); // QU bit + class IN
            stream.Write(tail);
        }

        return stream.ToArray();
    }

    private sealed record Record(string Name, ushort Type, string? Text, IPAddress? Address, IReadOnlyDictionary<string, string>? Txt);

    /// <summary>Turns mDNS answers into one description per responding address.</summary>
    public static IReadOnlyList<SelfDescription> Describe(IEnumerable<(IPAddress Source, byte[] Bytes)> packets)
    {
        var bySource = new Dictionary<IPAddress, List<Record>>();
        foreach (var (source, bytes) in packets)
        {
            if (source.AddressFamily != AddressFamily.InterNetwork) continue;
            List<Record> records;
            try
            {
                records = Parse(bytes);
            }
            catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException or InvalidDataException)
            {
                continue; // a malformed packet is ignored, never trusted
            }

            if (!bySource.TryGetValue(source, out var list)) bySource[source] = list = [];
            list.AddRange(records);
        }

        var result = new List<SelfDescription>();
        foreach (var (source, records) in bySource)
        {
            var host = records.FirstOrDefault(r => r.Type == TypeA && source.Equals(r.Address))?.Name
                ?? records.FirstOrDefault(r => r.Type == TypeSrv)?.Text;
            var services = new SortedSet<string>(StringComparer.Ordinal);
            string? friendly = null, model = null;
            foreach (var record in records.Where(r => r.Type is TypePtr or TypeSrv or TypeTxt))
            {
                var instance = record.Type == TypePtr ? record.Text ?? string.Empty : record.Name;
                foreach (var (type, word) in LocalNetworkProbes.ServiceWords)
                {
                    if (instance.Contains("." + type + ".", StringComparison.OrdinalIgnoreCase) || record.Name.StartsWith(type + ".", StringComparison.OrdinalIgnoreCase) && record.Type == TypePtr)
                    {
                        services.Add(word);
                        friendly ??= InstanceLabel(instance, type);
                    }
                }

                if (record.Txt is { } txt)
                {
                    friendly = txt.GetValueOrDefault("fn") ?? friendly;
                    model ??= txt.GetValueOrDefault("md") ?? txt.GetValueOrDefault("ty") ?? txt.GetValueOrDefault("model") ?? txt.GetValueOrDefault("product")?.Trim('(', ')');
                }
            }

            result.Add(new SelfDescription(source, Clean(host), Clean(friendly), null, Clean(model), null, [.. services]));
        }

        return result;
    }

    private static string? InstanceLabel(string instance, string type)
    {
        var at = instance.IndexOf("." + type + ".", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? instance[..at] : null;
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().TrimEnd('.');
        if (value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) value = value[..^6];
        value = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return value.Length == 0 ? null : value.Length > 64 ? value[..64] : value;
    }

    private static List<Record> Parse(byte[] packet)
    {
        if (packet.Length < 12) throw new InvalidDataException("short");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        if ((flags & 0x8000) == 0) return []; // a question, not an answer
        int qd = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        int total = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10));
        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            ReadName(packet, ref offset);
            offset += 4;
        }

        var records = new List<Record>();
        for (var i = 0; i < total && i < 256; i++)
        {
            var name = ReadName(packet, ref offset);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8));
            var data = offset + 10;
            if (data + length > packet.Length) throw new InvalidDataException("truncated");
            switch (type)
            {
                case TypeA when length == 4:
                    records.Add(new Record(name, type, null, new IPAddress(packet.AsSpan(data, 4)), null));
                    break;
                case TypePtr:
                    var ptrOffset = data;
                    records.Add(new Record(name, type, ReadName(packet, ref ptrOffset), null, null));
                    break;
                case TypeSrv when length > 6:
                    var srvOffset = data + 6;
                    records.Add(new Record(name, type, ReadName(packet, ref srvOffset), null, null));
                    break;
                case TypeTxt:
                    records.Add(new Record(name, type, null, null, ReadTxt(packet.AsSpan(data, length))));
                    break;
            }

            offset = data + length;
        }

        return records;
    }

    private static Dictionary<string, string> ReadTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            int length = data[i++];
            if (i + length > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(i, length));
            i += length;
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) result.TryAdd(entry[..eq], entry[(eq + 1)..]);
        }

        return result;
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            int length = packet[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(packet, position + 1, length));
            position += 1 + length;
        }

        throw new InvalidDataException("name loop");
    }
}

/// <summary>SSDP replies and UPnP device descriptions.</summary>
public static class Ssdp
{
    public static Uri? Location(string reply)
    {
        foreach (var line in reply.Split("\r\n"))
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
                return uri;
        }

        return null;
    }

    public static SelfDescription? ParseDescription(IPAddress address, string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var device = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null) return null;
        string? Field(string name) => Trim(device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);
        var type = Field("deviceType");
        var word = type switch
        {
            null => "UPnP",
            _ when type.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) => "router",
            _ when type.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase) => "media player",
            _ when type.Contains("MediaServer", StringComparison.OrdinalIgnoreCase) => "media server",
            _ when type.Contains("Printer", StringComparison.OrdinalIgnoreCase) => "printing",
            _ => "UPnP",
        };
        var model = string.Join(' ', new[] { Field("modelName"), Field("modelNumber") }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        return new SelfDescription(address, null, Field("friendlyName"), Field("manufacturer"), model.Length == 0 ? null : Trim(model), type, [word]);
    }

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return value.Length > 64 ? value[..64] : value;
    }
}
