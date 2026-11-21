namespace MonitorApp.Peer.Persistence;

public sealed record PeerStoreOptions(string DatabasePath, TimeSpan BusyTimeout)
{
    public static PeerStoreOptions Create(string databasePath) =>
        new(databasePath, TimeSpan.FromSeconds(5));
}
