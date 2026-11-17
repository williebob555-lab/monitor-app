namespace MonitorApp.Android.Core;

public sealed record NetworkTransportSummary(
    bool IsConnected,
    bool IsVpn,
    IReadOnlyList<string> Transports,
    DateTimeOffset SourceTime)
{
    public static NetworkTransportSummary Create(
        bool isConnected,
        bool isVpn,
        bool isWifi,
        bool isCellular,
        bool isEthernet,
        DateTimeOffset sourceTime)
    {
        var transports = new List<string>();
        if (isWifi)
        {
            transports.Add("Wi-Fi");
        }

        if (isCellular)
        {
            transports.Add("Cellular");
        }

        if (isEthernet)
        {
            transports.Add("Ethernet");
        }

        if (isVpn)
        {
            transports.Add("VPN");
        }

        if (transports.Count == 0)
        {
            transports.Add(isConnected ? "Other" : "Unavailable");
        }

        return new NetworkTransportSummary(
            isConnected,
            isVpn,
            transports.AsReadOnly(),
            sourceTime);
    }

    public string DisplayText =>
        IsConnected
            ? string.Join(" + ", Transports)
            : "No active validated network";
}
