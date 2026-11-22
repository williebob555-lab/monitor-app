using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MonitorApp.Peer.Persistence;

/// <summary>
/// In-process notification of origin records that were newly committed to one <see cref="PeerStore"/> (locally
/// collected or replicated). It is an optimisation signal only: durable state and replication cursors remain
/// authoritative, so a subscriber that misses an item must be able to re-read the store.
/// </summary>
public sealed class PeerLiveFeed
{
    private readonly object gate = new();
    private readonly List<Action<StoredOriginRecord>> handlers = [];

    /// <summary>
    /// Registers a handler invoked synchronously after each newly committed record. Handlers must be fast and must
    /// not throw (typically a channel TryWrite). Disposing the result unsubscribes.
    /// </summary>
    public IDisposable Subscribe(Action<StoredOriginRecord> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    /// <summary>
    /// Streams newly stored records until cancelled. The buffer is bounded; when a consumer falls behind the oldest
    /// buffered items are dropped (the store still has them).
    /// </summary>
    public async IAsyncEnumerable<StoredOriginRecord> StreamAsync(
        int capacity = 1024,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var channel = Channel.CreateBounded<StoredOriginRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var subscription = Subscribe(record => channel.Writer.TryWrite(record));
        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }

    internal void Publish(StoredOriginRecord record)
    {
        Action<StoredOriginRecord>[] snapshot;
        lock (gate)
        {
            if (handlers.Count == 0)
            {
                return;
            }

            snapshot = [.. handlers];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                handler(record);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // A disposed consumer must never fail the durable append that already committed.
            }
        }
    }

    private sealed class Subscription(PeerLiveFeed owner, Action<StoredOriginRecord> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.handlers.Remove(handler);
            }
        }
    }
}
