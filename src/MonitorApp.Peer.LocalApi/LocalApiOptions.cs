using System.Net;

namespace MonitorApp.Peer.LocalApi;

public sealed record LocalApiOptions(
    string DatabasePath,
    IPAddress BindAddress,
    int Port,
    TimeSpan? StaleAfter = null)
{
    public static LocalApiOptions Loopback(string databasePath, int port = 0) =>
        new(databasePath, IPAddress.Loopback, port);

    public TimeSpan EffectiveStaleAfter => StaleAfter ?? TimeSpan.FromMinutes(5);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        ArgumentNullException.ThrowIfNull(BindAddress);
        if (!IPAddress.IsLoopback(BindAddress))
        {
            throw new InvalidOperationException(
                "The local API only supports loopback binding. No authenticated remote-access mode is implemented.");
        }

        if (EffectiveStaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StaleAfter));
        }

        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port));
        }
    }
}
