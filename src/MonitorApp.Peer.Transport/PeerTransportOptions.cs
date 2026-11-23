using System.Net;

namespace MonitorApp.Peer.Transport;

public enum PeerTransportSecurity { Tls = 1, TailscaleEncrypted = 2, LoopbackDevelopment = 3 }

public sealed record PeerTransportEndpoint(Uri Address, string ExpectedPeerId, PeerTransportSecurity Security);

public sealed record PeerTransportOptions(PeerTransportEndpoint Endpoint, TimeSpan ConnectTimeout, TimeSpan OperationTimeout,
    TimeSpan HeartbeatInterval, TimeSpan InitialReconnectDelay, TimeSpan MaximumReconnectDelay, int MaximumMessageBytes)
{
    /// <summary>How often a live session pulls other devices' readings that the remote forwards (T087).</summary>
    public TimeSpan RelayPullInterval { get; init; } = TimeSpan.FromSeconds(15);

    public static PeerTransportOptions Create(PeerTransportEndpoint endpoint) => new(endpoint, TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 1024 * 1024);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint); ArgumentNullException.ThrowIfNull(Endpoint.Address);
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint.ExpectedPeerId);
        if (!System.Enum.IsDefined(Endpoint.Security) || !Endpoint.Address.IsAbsoluteUri || !IPAddress.TryParse(Endpoint.Address.Host, out var address))
            throw new InvalidOperationException("Peer endpoint must use an explicit IP address and security policy.");
        if (Endpoint.Address.Port is < 1 or > 65_535 || Endpoint.Address.AbsolutePath != "/")
            throw new InvalidOperationException("Peer endpoint must contain only an explicit host and port.");
        if (!IPAddress.IsLoopback(address) && !IsPrivate(address)) throw new InvalidOperationException("Public peer endpoints are prohibited.");
        if (Endpoint.Security == PeerTransportSecurity.Tls && Endpoint.Address.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("TLS peer endpoints require https.");
        if (Endpoint.Security == PeerTransportSecurity.TailscaleEncrypted && IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Loopback cannot be declared as Tailscale-protected.");
        if (Endpoint.Security == PeerTransportSecurity.TailscaleEncrypted &&
            Endpoint.Address.Scheme != Uri.UriSchemeHttp && Endpoint.Address.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Tailscale endpoints require http or https.");
        if (Endpoint.Security == PeerTransportSecurity.LoopbackDevelopment && (Endpoint.Address.Scheme != Uri.UriSchemeHttp || !IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("Plaintext development transport is restricted to explicit loopback endpoints.");
        if (ConnectTimeout <= TimeSpan.Zero || OperationTimeout <= TimeSpan.Zero || HeartbeatInterval <= TimeSpan.Zero || RelayPullInterval <= TimeSpan.Zero || InitialReconnectDelay <= TimeSpan.Zero || MaximumReconnectDelay < InitialReconnectDelay)
            throw new ArgumentOutOfRangeException(nameof(PeerTransportOptions));
        if (MaximumMessageBytes is < 4096 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumMessageBytes));
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 || bytes[0] == 169 && bytes[1] == 254,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => (bytes[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal,
            _ => false,
        };
    }
}

public enum PeerTransportState { Stopped = 1, Connecting = 2, Authenticating = 3, Authenticated = 4, BackingOff = 5 }
public interface IPeerReconnectDelay { ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken); }
public sealed class PeerReconnectDelay : IPeerReconnectDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => new(Task.Delay(delay, cancellationToken));
}
public class PeerTransportException(string message) : Exception(message);
public sealed class PeerAuthenticationException(string message) : PeerTransportException(message);
public sealed class PeerCompatibilityException(string message) : PeerTransportException(message);
