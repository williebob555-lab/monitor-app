using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace MonitorApp.Peer.Collection.Switches;

/// <summary>
/// Read-only SNMPv3 authPriv transport for the SG250 spike. Net-SNMP supplies the authenticated,
/// encrypted protocol; this process never receives or passes a password on its command line.
/// A provisioned, owner-only Net-SNMP config directory holds credentials on the collector peer.
/// </summary>
public sealed class NetSnmpV3Sg250Reader : ISg250SnmpReader
{
    private const int MaxOutputChars = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    private readonly IPAddress address;
    private readonly string securityName;
    private readonly string configDirectory;
    private readonly string walkExecutable;
    private readonly string getExecutable;

    public NetSnmpV3Sg250Reader(
        IPAddress address,
        string securityName,
        string configDirectory,
        string walkExecutable,
        string getExecutable)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsPrivateManagementAddress(address))
            throw new ArgumentException("The managed switch must have an explicitly configured private IPv4 address.", nameof(address));
        ArgumentException.ThrowIfNullOrWhiteSpace(securityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(walkExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(getExecutable);
        if (!Path.IsPathFullyQualified(configDirectory) ||
            !Path.IsPathFullyQualified(walkExecutable) ||
            !Path.IsPathFullyQualified(getExecutable))
            throw new ArgumentException("SNMP configuration and executable paths must be absolute.");
        this.address = address;
        this.securityName = securityName;
        this.configDirectory = configDirectory;
        this.walkExecutable = walkExecutable;
        this.getExecutable = getExecutable;
    }

    public async Task<IReadOnlyList<Sg250SnmpValue>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<Sg250SnmpValue>();
        foreach (var root in Sg250Oids.WalkRoots)
            results.AddRange(await QueryAsync(walkExecutable, root, bulk: true, cancellationToken).ConfigureAwait(false));

        // The bridge base MAC is a scalar and requires GET, not GETNEXT/BULKWALK. An absent scalar
        // must never invent a strong switch identity or erase otherwise valid interface facts.
        try
        {
            results.AddRange(await QueryAsync(getExecutable, Sg250Oids.BridgeAddress, bulk: false, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Sg250ReadException)
        {
            // Some switch firmware/configurations do not expose BRIDGE-MIB::dot1dBaseBridgeAddress.
        }

        return results;
    }

    private async Task<IReadOnlyList<Sg250SnmpValue>> QueryAsync(
        string executable, string oid, bool bulk, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Net-SNMP uses the provisioned user-local snmp.conf for defAuthPassphrase/defPrivPassphrase.
        // No -A/-X secrets, shell, or arbitrary caller-supplied OIDs.
        start.Environment["SNMPCONFPATH"] = configDirectory;
        foreach (var arg in new[] { "-v3", "-l", "authPriv", "-u", securityName, "-On", "-t", "2", "-r", "0" })
            start.ArgumentList.Add(arg);
        if (bulk) start.ArgumentList.Add("-Cr10");
        start.ArgumentList.Add(address.ToString());
        start.ArgumentList.Add(oid);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new Sg250ReadException(Sg250CollectionStatus.Unavailable);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new Sg250ReadException(Sg250CollectionStatus.Unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        string stdout;
        string stderr;
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaxOutputChars, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 8192, timeout.Token);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            stdout = await outputTask.ConfigureAwait(false);
            stderr = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillIfRunning(process);
            throw new Sg250ReadException(Sg250CollectionStatus.Unavailable);
        }
        catch (OperationCanceledException)
        {
            KillIfRunning(process);
            throw;
        }
        catch (InvalidDataException)
        {
            KillIfRunning(process);
            throw new Sg250ReadException(Sg250CollectionStatus.Error);
        }

        if (process.ExitCode != 0)
        {
            // Never report the raw process output. It can contain endpoint, account or local config data.
            var denied = stderr.Contains("authentication failure", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("authorizationerror", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("unknown user name", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("usmstats", StringComparison.OrdinalIgnoreCase);
            throw new Sg250ReadException(denied ? Sg250CollectionStatus.PermissionDenied : Sg250CollectionStatus.Unavailable);
        }

        var values = new List<Sg250SnmpValue>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseLine(line, out var item)) values.Add(item!);
        }
        return values;
    }

    /// <summary>Parse numeric-OID output from Net-SNMP's -On mode, including raw STRING and Hex-STRING values.</summary>
    public static bool TryParseLine(string line, out Sg250SnmpValue? result)
    {
        result = null;
        var eq = line.IndexOf(" = ", StringComparison.Ordinal);
        if (eq <= 0) return false;
        var oid = line[..eq].Trim();
        var value = line[(eq + 3)..].Trim();
        if (oid.Length < 3 || oid[0] != '.' || !oid[1..].All(c => char.IsAsciiDigit(c) || c == '.') || value.Length == 0)
            return false;
        result = new Sg250SnmpValue(oid, value);
        return true;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, int maxChars, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + count > maxChars) throw new InvalidDataException("SNMP output limit exceeded.");
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }

    private static void KillIfRunning(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public static bool IsPrivateManagementAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168 ||
               b[0] == 169 && b[1] == 254;
    }
}
