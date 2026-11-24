using System.Globalization;
using System.IO.Compression;

namespace MonitorApp.Peer.Collection.Discovery;

/// <summary>
/// The maker of a network device from the first three bytes of its MAC address (the IEEE registry, bundled with the app
/// as oui.tsv.gz; nothing is looked up online). A "locally administered" MAC is a private address a phone, tablet or
/// laptop makes up for privacy, so it has no maker.
/// </summary>
public static class MacVendors
{
    public const string PrivateAddress = "Private address";
    private static readonly Lazy<Dictionary<int, string>> Table = new(Load);

    /// <summary>Lowercase colon form ("aa:bb:cc:dd:ee:ff") of a MAC in any common spelling, or null when it is not a unicast MAC.</summary>
    public static string? Normalize(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var bytes = Convert.FromHexString(hex);
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xFF) || (bytes[0] & 0x01) != 0) return null; // empty, broadcast, multicast
        return string.Join(':', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    /// <summary>True for a MAC the device invented (randomized private address).</summary>
    public static bool IsPrivate(string normalizedMac) =>
        (byte.Parse(normalizedMac.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x02) != 0;

    /// <summary>The maker, <see cref="PrivateAddress"/>, or null when the prefix is not registered.</summary>
    public static string? Lookup(string normalizedMac)
    {
        if (IsPrivate(normalizedMac)) return PrivateAddress;
        var key = int.Parse(normalizedMac.Replace(":", string.Empty, StringComparison.Ordinal)[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Table.Value.GetValueOrDefault(key);
    }

    private static Dictionary<int, string> Load()
    {
        var result = new Dictionary<int, string>();
        using var resource = typeof(MacVendors).Assembly.GetManifestResourceStream("MonitorApp.Peer.Collection.Discovery.oui.tsv.gz");
        if (resource is null) return result;
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab == 6 && int.TryParse(line.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var prefix))
                result[prefix] = line[(tab + 1)..];
        }

        return result;
    }
}
