using Android.Content;
using Android.Net;
using Android.OS;
using MonitorApp.Android.Core;
using MonitorApp.Peer.Mobile;

namespace MonitorApp.Android;

/// <summary>Reads BatteryManager and ConnectivityManager and normalizes through Android.Core.</summary>
public sealed class AndroidDeviceReader(Context context) : IMobileDeviceReader
{
    public ValueTask<MobileDeviceSample> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceTime = DateTimeOffset.UtcNow;
        var batteryManager = (BatteryManager?)context.GetSystemService(Context.BatteryService);
        var capacity = batteryManager?.GetIntProperty((int)BatteryProperty.Capacity) ?? -1;
        var battery = BatteryReadingNormalizer.NormalizeCapacity(capacity, sourceTime);

        using var batteryIntent = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
        var rawStatus = batteryIntent?.GetIntExtra(BatteryManager.ExtraStatus, -1) ?? -1;
        var charging = rawStatus switch
        {
            (int)BatteryStatus.Charging or (int)BatteryStatus.Full =>
                new DeviceReading<bool>(true, true, sourceTime, "Charging or full."),
            (int)BatteryStatus.Discharging or (int)BatteryStatus.NotCharging =>
                new DeviceReading<bool>(true, false, sourceTime, "Not charging."),
            _ => new DeviceReading<bool>(false, null, sourceTime, "Android did not report a charging state."),
        };

        var connectivity = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        using var activeNetwork = connectivity?.ActiveNetwork;
        using var capabilities = activeNetwork is null ? null : connectivity?.GetNetworkCapabilities(activeNetwork);
        var network = NetworkTransportSummary.Create(
            capabilities?.HasCapability(NetCapability.Validated) == true,
            capabilities?.HasTransport(TransportType.Vpn) == true,
            capabilities?.HasTransport(TransportType.Wifi) == true,
            capabilities?.HasTransport(TransportType.Cellular) == true,
            capabilities?.HasTransport(TransportType.Ethernet) == true,
            sourceTime);

        return ValueTask.FromResult(new MobileDeviceSample(sourceTime, battery, charging, network));
    }
}
