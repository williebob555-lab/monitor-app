using Android.Content;
using Android.Net;
using Android.OS;
using Android.Provider;
using MonitorApp.Android.Core;

namespace MonitorApp.Android;

public sealed record AndroidDeviceSnapshot(
    string DeviceIdentity,
    string Platform,
    DeviceReading<int> BatteryPercent,
    string ChargingState,
    NetworkTransportSummary Network,
    DateTimeOffset SourceTime)
{
    public static AndroidDeviceSnapshot Capture(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceTime = DateTimeOffset.UtcNow;
        var batteryManager = (BatteryManager?)context.GetSystemService(Context.BatteryService);
        var capacity = batteryManager?.GetIntProperty((int)BatteryProperty.Capacity) ?? -1;
        var battery = BatteryReadingNormalizer.NormalizeCapacity(capacity, sourceTime);

        using var batteryIntent = context.RegisterReceiver(
            null,
            new IntentFilter(Intent.ActionBatteryChanged));
        var rawStatus = batteryIntent?.GetIntExtra(BatteryManager.ExtraStatus, -1) ?? -1;
        var chargingState = rawStatus switch
        {
            (int)BatteryStatus.Charging => "Charging",
            (int)BatteryStatus.Full => "Full",
            (int)BatteryStatus.Discharging => "Discharging",
            (int)BatteryStatus.NotCharging => "Not charging",
            _ => "Unavailable"
        };

        var connectivity = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        using var activeNetwork = connectivity?.ActiveNetwork;
        using var capabilities = activeNetwork is null
            ? null
            : connectivity?.GetNetworkCapabilities(activeNetwork);
        var isConnected = capabilities?.HasCapability(NetCapability.Validated) == true;
        var network = NetworkTransportSummary.Create(
            isConnected,
            capabilities?.HasTransport(TransportType.Vpn) == true,
            capabilities?.HasTransport(TransportType.Wifi) == true,
            capabilities?.HasTransport(TransportType.Cellular) == true,
            capabilities?.HasTransport(TransportType.Ethernet) == true,
            sourceTime);

        var androidId = Settings.Secure.GetString(
            context.ContentResolver,
            Settings.Secure.AndroidId) ?? "unavailable";
        return new AndroidDeviceSnapshot(
            $"{Build.Manufacturer} {Build.Model} / {androidId}",
            $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})",
            battery,
            chargingState,
            network,
            sourceTime);
    }
}
