using System.Net;
using Microsoft.Extensions.Configuration;

namespace MonitorApp.Peer.Service;

public sealed record PeerHostOptions(
    IPAddress BindAddress,
    int Port,
    bool AllowLoopback,
    int MaximumMessageBytes,
    int MaximumMessagesPerSession,
    TimeSpan CoordinatorTimeout)
{
    /// <summary>A live (held-open) session with no inbound message for this long is considered dead and closed.</summary>
    public TimeSpan LiveIdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public const int DefaultMaximumMessageBytes = 1024 * 1024;

    public const int DefaultMaximumMessagesPerSession = 4096;

    public static PeerHostOptions FromConfiguration(IConfiguration configuration)
    {
        var text = configuration["MonitorApp:Peer:BindAddress"];
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out var address))
        {
            throw new InvalidOperationException(
                "MonitorApp:Peer:BindAddress must be an explicit IP address.");
        }

        var allowLoopback = configuration.GetValue("MonitorApp:Peer:AllowLoopback", false);
        PeerBindPolicy.Validate(address, allowLoopback);

        var port = configuration.GetValue<int?>("MonitorApp:Peer:Port") ?? 0;
        if (port is < 1 or > 65_535)
        {
            throw new InvalidOperationException("MonitorApp:Peer:Port must be between 1 and 65535.");
        }

        var maximumMessageBytes = configuration.GetValue<int?>(
            "MonitorApp:Peer:MaximumMessageBytes") ?? DefaultMaximumMessageBytes;
        if (maximumMessageBytes is < 4096 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "MonitorApp:Peer:MaximumMessageBytes must be between 4096 and 16777216.");
        }

        var maximumMessages = configuration.GetValue<int?>(
            "MonitorApp:Peer:MaximumMessagesPerSession") ?? DefaultMaximumMessagesPerSession;
        if (maximumMessages is < 1 or > 100_000)
        {
            throw new InvalidOperationException(
                "MonitorApp:Peer:MaximumMessagesPerSession must be between 1 and 100000.");
        }

        var timeoutSeconds = configuration.GetValue<int?>(
            "MonitorApp:Peer:CoordinatorTimeoutSeconds") ?? 15;
        if (timeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException(
                "MonitorApp:Peer:CoordinatorTimeoutSeconds must be between 1 and 300.");
        }

        return new PeerHostOptions(
            address,
            port,
            allowLoopback,
            maximumMessageBytes,
            maximumMessages,
            TimeSpan.FromSeconds(timeoutSeconds));
    }
}

public static class PeerBindPolicy
{
    public static void Validate(IPAddress address, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
        {
            throw new InvalidOperationException("Wildcard and unspecified peer listeners are prohibited.");
        }

        if (IPAddress.IsLoopback(address))
        {
            if (!allowLoopback)
            {
                throw new InvalidOperationException(
                    "Loopback binding requires explicit MonitorApp:Peer:AllowLoopback=true.");
            }

            return;
        }

        if (!IsDeliberatelyPrivate(address))
        {
            throw new InvalidOperationException(
                "Peer listeners must bind to an explicit private, Tailscale, or local-link address.");
        }
    }

    public static bool IsDeliberatelyPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return false;
    }
}
