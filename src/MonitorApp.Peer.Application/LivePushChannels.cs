namespace MonitorApp.Peer.Application;

/// <summary>
/// Chooses which held-open session pushes this peer's live origin data to each remote peer. When two desktops dial
/// each other both hold a session to the other and both would push; only the active channel pushes, so each live
/// observation reaches a remote peer once.
/// Rule: the session initiated by the peer with the lower peer id (ordinal comparison) is the channel; if only the
/// other session exists it is used, and among several sessions of the same rank (stale reconnects) the most recently
/// joined wins. When the active session ends the other takes over from the persisted replication cursor.
/// </summary>
public sealed class LivePushChannels
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<Channel>> byRemote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> pushed = new(StringComparer.Ordinal);
    private long order;

    /// <summary>Raised (outside the lock) when a remote peer's first session joins (true) or its last session ends (false).</summary>
    public event Action<string, bool>? ConnectivityChanged;

    /// <summary>Registers a session; dispose the result when the session ends.</summary>
    public Channel Join(string remotePeerId, string localPeerId, bool locallyInitiated, Action onChange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentNullException.ThrowIfNull(onChange);
        var initiator = locallyInitiated ? localPeerId : remotePeerId;
        var other = locallyInitiated ? remotePeerId : localPeerId;
        var channel = new Channel(this, remotePeerId, string.CompareOrdinal(initiator, other) < 0, onChange);
        List<Channel> peers;
        lock (gate)
        {
            channel.Order = ++order;
            if (!byRemote.TryGetValue(remotePeerId, out var list))
            {
                byRemote[remotePeerId] = list = [];
            }

            list.Add(channel);
            peers = [.. list];
        }

        if (peers.Count == 1)
        {
            ConnectivityChanged?.Invoke(remotePeerId, true);
        }

        NotifyOthers(peers, channel);
        return channel;
    }

    /// <summary>Number of sessions currently registered for a remote peer (diagnostics).</summary>
    public int SessionCount(string remotePeerId)
    {
        lock (gate)
        {
            return byRemote.TryGetValue(remotePeerId, out var list) ? list.Count : 0;
        }
    }

    /// <summary>Live records pushed to a remote peer over any session since start (diagnostics and tests).</summary>
    public long RecordsPushed(string remotePeerId)
    {
        lock (gate)
        {
            return pushed.GetValueOrDefault(remotePeerId);
        }
    }

    private void CountPushed(string remotePeerId, int records)
    {
        lock (gate)
        {
            pushed[remotePeerId] = pushed.GetValueOrDefault(remotePeerId) + records;
        }
    }

    private static void NotifyOthers(List<Channel> peers, Channel except)
    {
        foreach (var peer in peers.Where(item => !ReferenceEquals(item, except)))
        {
            peer.OnChange();
        }
    }

    public sealed class Channel : IDisposable
    {
        private readonly LivePushChannels owner;
        private readonly string remotePeerId;
        private int disposed;

        internal Channel(LivePushChannels owner, string remotePeerId, bool preferred, Action onChange)
        {
            this.owner = owner;
            this.remotePeerId = remotePeerId;
            Preferred = preferred;
            OnChange = onChange;
        }

        internal bool Preferred { get; }

        internal long Order { get; set; }

        internal Action OnChange { get; }

        public void RecordsSent(int records) => owner.CountPushed(remotePeerId, records);

        public bool IsActive
        {
            get
            {
                lock (owner.gate)
                {
                    if (!owner.byRemote.TryGetValue(remotePeerId, out var list) || !list.Contains(this))
                    {
                        return false;
                    }

                    var best = list.OrderByDescending(item => item.Preferred).ThenByDescending(item => item.Order).First();
                    return ReferenceEquals(best, this);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            List<Channel> peers;
            lock (owner.gate)
            {
                if (!owner.byRemote.TryGetValue(remotePeerId, out var list))
                {
                    return;
                }

                list.Remove(this);
                peers = [.. list];
                if (list.Count == 0)
                {
                    owner.byRemote.Remove(remotePeerId);
                }
            }

            if (peers.Count == 0)
            {
                owner.ConnectivityChanged?.Invoke(remotePeerId, false);
            }

            NotifyOthers(peers, this);
        }
    }
}
