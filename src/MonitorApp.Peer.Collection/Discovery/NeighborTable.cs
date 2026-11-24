using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace MonitorApp.Peer.Collection.Discovery;

/// <summary>A device this computer has recently talked to on its own network: its address and MAC.</summary>
public sealed record Neighbor(IPAddress Address, string Mac);

/// <summary>Reads the operating system's neighbor (ARP) table. Read-only; never sends anything.</summary>
public interface INeighborTable
{
    IReadOnlyList<Neighbor> Read();
}

public static class NeighborTables
{
    public static INeighborTable Create() =>
        OperatingSystem.IsWindows() ? new WindowsNeighborTable()
        : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? new LinuxNeighborTable()
        : new EmptyNeighborTable();

    /// <summary>Parses /proc/net/arp: complete IPv4 entries (flag 0x2) with a unicast MAC.</summary>
    public static IReadOnlyList<Neighbor> ParseProcNetArp(string text)
    {
        var result = new List<Neighbor>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || !IPAddress.TryParse(f[0], out var ip)) continue;
            if (!f[2].StartsWith("0x", StringComparison.Ordinal) ||
                !int.TryParse(f[2].AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var flags) ||
                (flags & 0x2) == 0) continue;
            if (MacVendors.Normalize(f[3]) is { } mac) result.Add(new Neighbor(ip, mac));
        }

        return result;
    }
}

public sealed class EmptyNeighborTable : INeighborTable
{
    public IReadOnlyList<Neighbor> Read() => [];
}

public sealed class LinuxNeighborTable(string path = "/proc/net/arp") : INeighborTable
{
    public IReadOnlyList<Neighbor> Read()
    {
        try
        {
            return NeighborTables.ParseProcNetArp(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>GetIpNetTable from iphlpapi (IPv4 neighbors). Invalid entries are skipped.</summary>
public sealed partial class WindowsNeighborTable : INeighborTable
{
    private const int RowSize = 24;

    public IReadOnlyList<Neighbor> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        uint size = 0;
        _ = GetIpNetTable(null, ref size, false);
        if (size is 0 or > 4 * 1024 * 1024) return [];
        var buffer = new byte[size];
        if (GetIpNetTable(buffer, ref size, false) != 0) return [];
        return Parse(buffer);
    }

    /// <summary>MIB_IPNETTABLE: a DWORD count, then rows of index, MAC length, 8 MAC bytes, IPv4 address, type.</summary>
    public static IReadOnlyList<Neighbor> Parse(ReadOnlySpan<byte> buffer)
    {
        var result = new List<Neighbor>();
        if (buffer.Length < 4) return result;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        for (var i = 0; i < count && 4 + ((i + 1) * RowSize) <= buffer.Length; i++)
        {
            var row = buffer.Slice(4 + (i * RowSize), RowSize);
            var macLength = BinaryPrimitives.ReadInt32LittleEndian(row[4..]);
            var type = BinaryPrimitives.ReadInt32LittleEndian(row[20..]);
            if (macLength != 6 || type is not (3 or 4)) continue; // 3 dynamic, 4 static; 2 invalid
            var ip = new IPAddress(row.Slice(16, 4));
            if (MacVendors.Normalize(Convert.ToHexString(row.Slice(8, 6))) is { } mac) result.Add(new Neighbor(ip, mac));
        }

        return result;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpNetTable(byte[]? table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);
}
