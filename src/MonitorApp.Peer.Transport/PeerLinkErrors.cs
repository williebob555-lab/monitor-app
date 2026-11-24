using System.Net.Http;
using System.Net.Sockets;
using Grpc.Core;

namespace MonitorApp.Peer.Transport;

/// <summary>Turns a connection failure into words a person can act on, for the device cards on the pairing screen.</summary>
public static class PeerLinkErrors
{
    public static string Plain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var socket = Find<SocketException>(exception);
        if (socket is not null)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "the connection was refused (Monitor App may not be running there, or a firewall blocks it)",
                SocketError.TimedOut => "it did not answer in time (it may be off, asleep, or on another network)",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown =>
                    "there is no route to it from this network (check Wi-Fi or Tailscale on both devices)",
                SocketError.ConnectionReset or SocketError.ConnectionAborted => "the connection was cut off",
                _ => "network error: " + socket.SocketErrorCode,
            };
        }

        return exception switch
        {
            PeerAuthenticationException auth => "it did not accept this device: " + auth.Message,
            PeerCompatibilityException compatibility => "the two versions of Monitor App can't talk to each other; update both. " + compatibility.Message,
            RpcException { StatusCode: StatusCode.Unavailable } => "it did not answer (it may be off, asleep, or on another network)",
            RpcException { StatusCode: StatusCode.DeadlineExceeded } => "it did not answer in time",
            RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unauthenticated } rpc => "it refused this device: " + rpc.Status.Detail,
            RpcException rpc => rpc.Status.Detail is { Length: > 0 } detail ? detail : rpc.StatusCode.ToString(),
            HttpRequestException http => http.Message,
            TimeoutException => "it did not answer in time",
            _ => exception.Message,
        };
    }

    private static T? Find<T>(Exception exception)
        where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
