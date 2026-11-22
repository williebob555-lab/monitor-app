namespace MonitorApp.Android.Core;

public sealed record NetworkTransportSummary(
    bool IsConnected,
    bool IsVpn,
    IReadOnlyList<string> Transports,
    DateTimeOffset SourceTime,
    bool? IsMetered = null,
    long? DownstreamKbps = null,
    long? UpstreamKbps = null)
{
    public static NetworkTransportSummary Create(
        bool isConnected,
        bool isVpn,
        bool isWifi,
        bool isCellular,
        bool isEthernet,
        DateTimeOffset sourceTime,
        bool? isMetered = null,
        long? downstreamKbps = null,
        long? upstreamKbps = null)
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
            sourceTime,
            isMetered,
            downstreamKbps is > 0 ? downstreamKbps : null,
            upstreamKbps is > 0 ? upstreamKbps : null);
    }

    public string DisplayText =>
        IsConnected
            ? string.Join(" + ", Transports)
            : "No active validated network";
}
