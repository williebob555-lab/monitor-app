namespace MonitorApp.Peer.Persistence;

public class PeerPersistenceException : Exception
{
    public PeerPersistenceException(string message)
        : base(message)
    {
    }
}

public sealed class PeerPersistenceConflictException : PeerPersistenceException
{
    public PeerPersistenceConflictException(string message)
        : base(message)
    {
    }
}
