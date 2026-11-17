namespace MonitorApp.Android.Core;

public static class BatteryReadingNormalizer
{
    public static DeviceReading<int> NormalizeCapacity(
        int reportedPercent,
        DateTimeOffset sourceTime)
    {
        if (reportedPercent is < 0 or > 100)
        {
            return new DeviceReading<int>(
                false,
                null,
                sourceTime,
                "Android did not provide a valid battery percentage.");
        }

        return new DeviceReading<int>(
            true,
            reportedPercent,
            sourceTime,
            "Battery percentage reported by Android BatteryManager.");
    }
}
