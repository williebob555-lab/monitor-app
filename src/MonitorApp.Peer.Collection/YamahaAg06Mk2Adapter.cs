using System.Diagnostics;
using System.Text.Json;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection;

public enum YamahaAg06Mk2ProbeStatus
{
    Available = 1,
    Unavailable = 2,
    PermissionDenied = 3,
    Error = 4,
}

public sealed record YamahaAg06Mk2Device(
    string Manufacturer,
    string Model,
    string HardwareId,
    string DriverProvider,
    string DriverVersion,
    string ContainerId,
    string AttachmentLocation);

public sealed record YamahaAg06Mk2ProbeResult(
    YamahaAg06Mk2ProbeStatus Status,
    YamahaAg06Mk2Device? Device,
    string? SafeReason);

public interface IYamahaAg06Mk2Probe
{
    Task<YamahaAg06Mk2ProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record YamahaAg06Mk2Observation(
    string ObservedEntityId,
    string CollectorPeerId,
    YamahaAg06Mk2ProbeStatus Status,
    YamahaAg06Mk2Device? Device,
    DateTimeOffset ObservedAt,
    ObservationQuality Quality,
    string? SafeReason);

public sealed class YamahaAg06Mk2Adapter
{
    private readonly string observedEntityId;
    private readonly string collectorPeerId;
    private readonly IYamahaAg06Mk2Probe probe;
    private readonly Func<DateTimeOffset> utcNow;

    public YamahaAg06Mk2Adapter(
        string observedEntityId,
        string collectorPeerId,
        IYamahaAg06Mk2Probe probe,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectorPeerId);
        this.observedEntityId = observedEntityId;
        this.collectorPeerId = collectorPeerId;
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<YamahaAg06Mk2Observation> CollectAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(result.Status) ||
            result.Status == YamahaAg06Mk2ProbeStatus.Available && result.Device is null ||
            result.Status != YamahaAg06Mk2ProbeStatus.Available && result.Device is not null)
        {
            throw new InvalidOperationException("Yamaha probe returned an inconsistent result.");
        }

        return new YamahaAg06Mk2Observation(
            observedEntityId,
            collectorPeerId,
            result.Status,
            result.Device,
            utcNow(),
            result.Status switch
            {
                YamahaAg06Mk2ProbeStatus.Available => ObservationQuality.Valid,
                YamahaAg06Mk2ProbeStatus.PermissionDenied => ObservationQuality.PermissionDenied,
                YamahaAg06Mk2ProbeStatus.Unavailable => ObservationQuality.Unavailable,
                _ => ObservationQuality.Error,
            },
            result.SafeReason);
    }
}

public sealed class PowerShellYamahaAg06Mk2Probe : IYamahaAg06Mk2Probe
{
    private const string Script = """
        $device = Get-PnpDevice -PresentOnly -ErrorAction Stop |
          Where-Object InstanceId -Like 'USB\VID_0499&PID_1757\*' |
          Select-Object -First 1
        if ($null -eq $device) { return }
        $properties = @{}
        Get-PnpDeviceProperty -InstanceId $device.InstanceId -ErrorAction Stop |
          ForEach-Object { $properties[$_.KeyName] = $_.Data }
        [pscustomobject]@{
          Manufacturer = [string]$properties['DEVPKEY_Device_Manufacturer']
          Model = [string]$properties['DEVPKEY_Device_BusReportedDeviceDesc']
          HardwareId = [string]($properties['DEVPKEY_Device_HardwareIds'] | Select-Object -First 1)
          DriverProvider = [string]$properties['DEVPKEY_Device_DriverProvider']
          DriverVersion = [string]$properties['DEVPKEY_Device_DriverVersion']
          ContainerId = [string]$properties['DEVPKEY_Device_ContainerId']
          AttachmentLocation = [string]$properties['DEVPKEY_Device_LocationInfo']
        } | ConvertTo-Json -Compress
        """;

    private readonly TimeSpan timeout;

    public PowerShellYamahaAg06Mk2Probe(TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<YamahaAg06Mk2ProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new YamahaAg06Mk2ProbeResult(
                YamahaAg06Mk2ProbeStatus.Unavailable,
                null,
                "windows-required");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(Script);
        try
        {
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = (await standardOutput.ConfigureAwait(false)).Trim();
            var error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var denied = error.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
                             error.Contains("permission", StringComparison.OrdinalIgnoreCase);
                return new YamahaAg06Mk2ProbeResult(
                    denied ? YamahaAg06Mk2ProbeStatus.PermissionDenied : YamahaAg06Mk2ProbeStatus.Error,
                    null,
                    denied ? "device-inventory-permission-denied" : "device-inventory-failed");
            }

            if (output.Length == 0)
            {
                return new YamahaAg06Mk2ProbeResult(
                    YamahaAg06Mk2ProbeStatus.Unavailable,
                    null,
                    "device-not-present");
            }

            var device = JsonSerializer.Deserialize<YamahaAg06Mk2Device>(output) ??
                         throw new JsonException("Device inventory result was empty.");
            if (!device.HardwareId.Contains("VID_0499&PID_1757", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("Device inventory did not identify the expected Yamaha model.");
            }

            return new YamahaAg06Mk2ProbeResult(YamahaAg06Mk2ProbeStatus.Available, device, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return new YamahaAg06Mk2ProbeResult(
                YamahaAg06Mk2ProbeStatus.Error,
                null,
                "device-inventory-timeout");
        }
        catch (JsonException)
        {
            return new YamahaAg06Mk2ProbeResult(
                YamahaAg06Mk2ProbeStatus.Error,
                null,
                "device-inventory-invalid-response");
        }
    }
}
