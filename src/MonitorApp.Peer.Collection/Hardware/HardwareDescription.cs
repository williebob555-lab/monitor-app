using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Hardware;

/// <summary>A drive as the computer reports it.</summary>
public sealed record DriveInfo(string Model, ulong? SizeBytes, string? Kind);

/// <summary>An operating system the firmware can start on this machine.</summary>
public sealed record InstalledSystem(string Name, bool IsCurrent, bool IsDefault);

/// <summary>What a computer is made of, read from the computer itself. Null means "not reported here", never a guess.</summary>
public sealed record HardwareFacts(
    string? Maker,
    string? Model,
    string? Processor,
    int? Cores,
    int? Threads,
    ulong? MemoryBytes,
    IReadOnlyList<string> Graphics,
    IReadOnlyList<DriveInfo> Drives,
    string? OperatingSystem,
    IReadOnlyList<InstalledSystem> Systems);

/// <summary>Signal ids for the self-description. All are measured readings on the computer's own subject.</summary>
public static class HardwareSignals
{
    public const string CollectorId = "monitor.hardware";
    public const string Maker = "hardware.maker";
    public const string Model = "hardware.model";
    public const string Processor = "hardware.cpu.model";
    public const string Cores = "hardware.cpu.cores";
    public const string Threads = "hardware.cpu.threads";
    public const string Memory = "hardware.memory.installed";
    public const string OperatingSystem = "host.os.name";
    public const string Systems = "host.systems";
    public const string SystemsCount = "host.systems.count";
    public const string BootDefault = "host.boot.default";
    public const string BootCurrent = "host.boot.current";

    public static string Graphics(int index) => $"hardware.gpu.{index}.model";

    public static string DriveModel(int index) => $"hardware.drive.{index}.model";

    public static string DriveSize(int index) => $"hardware.drive.{index}.size";

    public static string DriveKind(int index) => $"hardware.drive.{index}.kind";
}

/// <summary>
/// Hardware self-description (D4): maker, model, processor, memory, graphics, drives and the operating systems the
/// machine can start. Read-only, from files and fixed system queries; nothing typed by a person is ever run.
/// </summary>
public sealed class HardwareSnapshotCollector(Func<CancellationToken, Task<HardwareFacts>>? read = null, TimeProvider? time = null) : ISystemSnapshotCollector
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public async ValueTask<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        HardwareFacts facts;
        try
        {
            facts = await (read ?? ReadAsync)(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return new SystemSnapshot(Timestamp.FromDateTimeOffset(now), [], [new CollectorHealth("hardware", CapabilityState.Error, "hardware.read_failed")]);
        }

        return new SystemSnapshot(Timestamp.FromDateTimeOffset(now), ToMetrics(facts), [new CollectorHealth("hardware", CapabilityState.Available, "hardware.collected")]);
    }

    public static IReadOnlyList<CollectedMetric> ToMetrics(HardwareFacts facts)
    {
        var metrics = new List<CollectedMetric>();
        void Text(string id, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) metrics.Add(new(id, MetricUnit.Unspecified, ObservationQuality.Valid, new MetricValue { StringValue = Clean(value) }));
        }

        void Count(string id, ulong? value)
        {
            if (value is { } v) metrics.Add(new(id, MetricUnit.Count, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = v }));
        }

        Text(HardwareSignals.Maker, facts.Maker);
        Text(HardwareSignals.Model, facts.Model);
        Text(HardwareSignals.Processor, facts.Processor);
        Count(HardwareSignals.Cores, (ulong?)facts.Cores);
        Count(HardwareSignals.Threads, (ulong?)facts.Threads);
        if (facts.MemoryBytes is { } memory)
            metrics.Add(new(HardwareSignals.Memory, MetricUnit.Bytes, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = memory }));
        for (var i = 0; i < facts.Graphics.Count && i < 8; i++) Text(HardwareSignals.Graphics(i), facts.Graphics[i]);
        for (var i = 0; i < facts.Drives.Count && i < 16; i++)
        {
            Text(HardwareSignals.DriveModel(i), facts.Drives[i].Model);
            Text(HardwareSignals.DriveKind(i), facts.Drives[i].Kind);
            if (facts.Drives[i].SizeBytes is { } size)
                metrics.Add(new(HardwareSignals.DriveSize(i), MetricUnit.Bytes, ObservationQuality.Valid, new MetricValue { UnsignedIntegerValue = size }));
        }

        Text(HardwareSignals.OperatingSystem, facts.OperatingSystem);
        if (facts.Systems.Count > 0)
        {
            Text(HardwareSignals.Systems, string.Join(", ", facts.Systems.Select(s => s.Name + (s.IsCurrent ? " (running now)" : string.Empty))));
            Count(HardwareSignals.SystemsCount, (ulong)facts.Systems.Count);
            Text(HardwareSignals.BootDefault, facts.Systems.FirstOrDefault(s => s.IsDefault)?.Name);
            Text(HardwareSignals.BootCurrent, facts.Systems.FirstOrDefault(s => s.IsCurrent)?.Name);
        }

        return metrics;
    }

    private static string Clean(string value)
    {
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length > 96 ? text[..96] : text;
    }

    public static Task<HardwareFacts> ReadAsync(CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows() ? WindowsHardware.ReadAsync(cancellationToken)
        : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? Task.FromResult(LinuxHardware.Read())
        : Task.FromResult(new HardwareFacts(null, null, null, null, null, null, [], [], null, []));
}

/// <summary>Linux: DMI, /proc, /sys/block, PCI class 03 with hwdata names, os-release and the UEFI boot entries.</summary>
public static class LinuxHardware
{
    private const string EfiGlobal = "8be4df61-93ca-11d2-aa0d-00e098032b8c";

    public static HardwareFacts Read(string root = "/")
    {
        string? File(string path)
        {
            try
            {
                var full = Path.Combine(root, path.TrimStart('/'));
                return System.IO.File.Exists(full) ? System.IO.File.ReadAllText(full).Trim() : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var vendor = Placeholder(File("/sys/class/dmi/id/sys_vendor"));
        var product = Placeholder(File("/sys/class/dmi/id/product_name"));
        var version = Placeholder(File("/sys/class/dmi/id/product_version"));
        // Lenovo puts the marketing name ("ThinkPad X1 Carbon Gen 9") in product_version and a type number in product_name.
        var model = vendor is not null && vendor.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && version is not null ? version : product;
        var (cpu, cores, threads) = ParseCpuInfo(File("/proc/cpuinfo") ?? string.Empty);
        var memory = ParseMemTotal(File("/proc/meminfo") ?? string.Empty);
        var os = ParseOsRelease(File("/etc/os-release") ?? string.Empty);
        return new HardwareFacts(Title(vendor), model, cpu, cores, threads, memory, Graphics(root), Drives(root), os, Systems(root, os));
    }

    public static (string? Model, int? Cores, int? Threads) ParseCpuInfo(string text)
    {
        string? model = null;
        int threads = 0;
        var cores = new HashSet<(string, string)>();
        string physical = "0";
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "processor": threads++; break;
                case "model name": model ??= value; break;
                case "physical id": physical = value; break;
                case "core id": cores.Add((physical, value)); break;
            }
        }

        return (model, cores.Count > 0 ? cores.Count : null, threads > 0 ? threads : null);
    }

    public static ulong? ParseMemTotal(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        var digits = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }

    public static string? ParseOsRelease(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
        return line?["PRETTY_NAME=".Length..].Trim().Trim('"');
    }

    private static List<string> Graphics(string root)
    {
        var result = new List<string>();
        var devices = Path.Combine(root, "sys/bus/pci/devices");
        if (!Directory.Exists(devices)) return result;
        Dictionary<string, (string Vendor, Dictionary<string, string> Devices)>? names = null;
        foreach (var device in Directory.GetDirectories(devices).Order(StringComparer.Ordinal))
        {
            try
            {
                var cls = System.IO.File.ReadAllText(Path.Combine(device, "class")).Trim();
                if (!cls.StartsWith("0x03", StringComparison.Ordinal)) continue;
                var vendorId = System.IO.File.ReadAllText(Path.Combine(device, "vendor")).Trim()[2..].ToLowerInvariant();
                var deviceId = System.IO.File.ReadAllText(Path.Combine(device, "device")).Trim()[2..].ToLowerInvariant();
                names ??= PciNames(root);
                result.Add(names.TryGetValue(vendorId, out var v)
                    ? v.Devices.TryGetValue(deviceId, out var d) ? $"{ShortVendor(v.Vendor)} {d}" : $"{ShortVendor(v.Vendor)} graphics ({deviceId})"
                    : $"Graphics {vendorId}:{deviceId}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // A device that vanished or cannot be read is left out.
            }
        }

        return result;
    }

    private static string ShortVendor(string vendor) =>
        vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
        : vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || vendor.Contains("ATI", StringComparison.Ordinal) ? "AMD"
        : vendor;

    /// <summary>Vendor and device names from hwdata's pci.ids, when installed.</summary>
    private static Dictionary<string, (string, Dictionary<string, string>)> PciNames(string root)
    {
        var result = new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.Ordinal);
        var path = PciIdPaths.Select(p => Path.Combine(root, p)).FirstOrDefault(System.IO.File.Exists);
        if (path is null) return result;
        string? current = null;
        foreach (var line in System.IO.File.ReadLines(path))
        {
            if (line.Length < 6 || line[0] == '#') continue;
            if (line[0] != '\t' && line[4] == ' ')
            {
                if (line.StartsWith("C ", StringComparison.Ordinal)) break; // device classes follow; no more vendors
                current = line[..4];
                result[current] = (line[4..].Trim(), new Dictionary<string, string>(StringComparer.Ordinal));
            }
            else if (current is not null && line[0] == '\t' && line.Length > 7 && line[1] != '\t' && line[5] == ' ')
            {
                result[current].Item2[line[1..5]] = line[5..].Trim();
            }
        }

        return result;
    }

    private static List<DriveInfo> Drives(string root)
    {
        var result = new List<DriveInfo>();
        var block = Path.Combine(root, "sys/block");
        if (!Directory.Exists(block)) return result;
        foreach (var dir in Directory.GetDirectories(block).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal) ||
                name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("dm-", StringComparison.Ordinal) ||
                name.StartsWith("sr", StringComparison.Ordinal) || name.StartsWith("md", StringComparison.Ordinal)) continue;
            try
            {
                var model = ReadText(Path.Combine(dir, "device", "model")) ?? name;
                var sectors = ulong.TryParse(ReadText(Path.Combine(dir, "size")), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
                if (sectors == 0) continue;
                var rotational = ReadText(Path.Combine(dir, "queue", "rotational")) == "1";
                var removable = ReadText(Path.Combine(dir, "removable")) == "1";
                var kind = name.StartsWith("nvme", StringComparison.Ordinal) ? "NVMe SSD" : rotational ? "hard drive" : removable ? "removable" : "SSD";
                result.Add(new DriveInfo(model, sectors * 512, kind));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skipped.
            }
        }

        return result;
    }

    private static string? ReadText(string path) => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;

    /// <summary>
    /// The systems the firmware can start (UEFI Boot#### entries in BootOrder, generic ones such as network or USB left out),
    /// which one is running now (BootCurrent) and which starts by default (first in BootOrder). When this machine has no
    /// UEFI variables to read, only the running system is listed.
    /// </summary>
    private static List<InstalledSystem> Systems(string root, string? runningOs)
    {
        var vars = Path.Combine(root, "sys/firmware/efi/efivars");
        var result = new List<InstalledSystem>();
        try
        {
            if (Directory.Exists(vars))
            {
                var order = ParseBootOrder(ReadVar(vars, "BootOrder"));
                var current = ParseBootCurrent(ReadVar(vars, "BootCurrent"));
                foreach (var number in order)
                {
                    var description = ParseLoadOptionDescription(ReadVar(vars, $"Boot{number:X4}"));
                    if (description is null || IsGeneric(description) || result.Any(r => r.Name == description)) continue;
                    result.Add(new InstalledSystem(description, number == current, result.Count == 0));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Clear();
        }

        if (result.Count == 0 && runningOs is not null) result.Add(new InstalledSystem(runningOs, true, false));
        return result;
    }

    private static byte[]? ReadVar(string vars, string name)
    {
        var path = Path.Combine(vars, $"{name}-{EfiGlobal}");
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
    }

    /// <summary>efivarfs content is four attribute bytes followed by the value.</summary>
    public static IReadOnlyList<ushort> ParseBootOrder(byte[]? content)
    {
        if (content is null || content.Length < 6) return [];
        var result = new List<ushort>();
        for (var i = 4; i + 1 < content.Length; i += 2) result.Add(BitConverter.ToUInt16(content, i));
        return result;
    }

    public static ushort? ParseBootCurrent(byte[]? content) =>
        content is { Length: >= 6 } ? BitConverter.ToUInt16(content, 4) : null;

    /// <summary>EFI_LOAD_OPTION after the attribute bytes: UINT32 attributes, UINT16 path length, then a UTF-16 description.</summary>
    public static string? ParseLoadOptionDescription(byte[]? content)
    {
        if (content is null || content.Length < 4 + 6 + 2) return null;
        var attributes = BitConverter.ToUInt32(content, 4);
        if ((attributes & 0x1) == 0) return null; // not active
        var start = 4 + 6;
        var end = start;
        while (end + 1 < content.Length && (content[end] != 0 || content[end + 1] != 0)) end += 2;
        var text = Encoding.Unicode.GetString(content, start, end - start).Trim();
        return text.Length is 0 or > 96 ? null : text;
    }

    private static bool IsGeneric(string description) =>
        GenericBootWords.Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PciIdPaths = ["usr/share/hwdata/pci.ids", "usr/share/misc/pci.ids"];

    private static readonly string[] GenericBootWords = ["UEFI:", "USB", "PXE", "IPv4", "IPv6", "Network", "Diskette", "CD/DVD", "Onboard NIC", "EFI Shell", "Setup", "Diagnostic", "Boot Menu", "Recovery", "Firmware", "Lenovo Recovery", "Startup Interrupt"];

    private static string? Placeholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("System Product Name", StringComparison.OrdinalIgnoreCase) || value.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static string? Title(string? vendor) =>
        vendor is null ? null : vendor.All(c => !char.IsLetter(c) || char.IsUpper(c)) && vendor.Length > 3
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(vendor.ToLowerInvariant())
            : vendor;
}

/// <summary>Windows: one fixed, read-only PowerShell CIM query (no user input), returned as JSON.</summary>
public static class WindowsHardware
{
    private const string Script =
        "$ErrorActionPreference='SilentlyContinue';" +
        "$cs=Get-CimInstance Win32_ComputerSystem;$csp=Get-CimInstance Win32_ComputerSystemProduct;" +
        "$p=@(Get-CimInstance Win32_Processor);$os=Get-CimInstance Win32_OperatingSystem;" +
        "$mem=(Get-CimInstance Win32_PhysicalMemory|Measure-Object Capacity -Sum).Sum;" +
        "$pd=@(Get-PhysicalDisk|Select-Object FriendlyName,Size,MediaType,BusType);" +
        "[pscustomobject]@{Maker=$cs.Manufacturer;Model=$cs.Model;Family=$cs.SystemFamily;Version=$csp.Version;" +
        "Cpu=$p[0].Name;Cores=($p|Measure-Object NumberOfCores -Sum).Sum;Threads=($p|Measure-Object NumberOfLogicalProcessors -Sum).Sum;" +
        "Memory=$mem;Gpus=@(Get-CimInstance Win32_VideoController|ForEach-Object Name);" +
        "Disks=$pd;Os=$os.Caption}|ConvertTo-Json -Depth 4 -Compress";

    public static async Task<HardwareFacts> ReadAsync(CancellationToken cancellationToken)
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The hardware query did not finish within 60 seconds.");
        }
    }

    public static HardwareFacts Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
        ulong? Num(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var n) ? n : null;
        IEnumerable<JsonElement> Items(string name) =>
            !root.TryGetProperty(name, out var value) ? []
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList()
            : value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.String ? [value] : [];

        var maker = Str(root, "Maker");
        var model = Str(root, "Model");
        var version = Str(root, "Version");
        if (maker is not null && maker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && version is not null) model = version;
        var gpus = Items("Gpus").Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).Where(g => !string.IsNullOrWhiteSpace(g) && !g.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && !g.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)).ToList();
        var drives = Items("Disks").Where(d => d.ValueKind == JsonValueKind.Object).Select(d =>
        {
            var bus = Str(d, "BusType");
            var media = Str(d, "MediaType");
            var kind = string.Equals(bus, "NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe SSD"
                : string.Equals(media, "HDD", StringComparison.OrdinalIgnoreCase) ? "hard drive"
                : string.Equals(media, "SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                : string.Equals(bus, "USB", StringComparison.OrdinalIgnoreCase) ? "removable" : null;
            return new DriveInfo(Str(d, "FriendlyName") ?? "Drive", Num(d, "Size"), kind);
        }).ToList();
        var os = Str(root, "Os");
        return new HardwareFacts(maker, model, Str(root, "Cpu"), (int?)Num(root, "Cores"), (int?)Num(root, "Threads"), Num(root, "Memory"), gpus, drives, os,
            os is null ? [] : [new InstalledSystem(os, true, false)]);
    }
}
