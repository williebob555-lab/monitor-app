using System.Collections.Concurrent;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Application;

/// <summary>
/// Records transitions (never heartbeats) of each trusted peer's connectivity state into the durable history that backs
/// the UI time-travel timeline. Session up/down events are timestamped when they happen and applied in order;
/// <see cref="ReconcileAsync"/> adds what no event announces: pairing, revocation, offline-to-stale escalation (silent for
/// at least the stale threshold, the same point the "peer not seen" alert uses) and missed connects or disconnects.
/// </summary>
// The semaphore never exposes a wait handle, so it holds no unmanaged resource and needs no disposal.
#pragma warning disable CA1001
public sealed class PeerStateTracker
{
    private readonly PeerStore store;
    private readonly IPeerClock clock;
    private readonly TimeSpan staleAfter;
    private readonly Func<string, bool> isConnected;
    private readonly ConcurrentQueue<(string PeerId, bool Connected, string? Reason, DateTimeOffset At)> pending = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private int suspended;

    /// <summary>
    /// Stops recording session events (the host is shutting down: sessions ending then say nothing about the peers) and
    /// waits for a flush already writing to the store, so nothing touches the store after the caller disposes it.
    /// </summary>
    public async Task SuspendAsync()
    {
        Volatile.Write(ref suspended, 1);
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
    }

    private bool IsSuspended => Volatile.Read(ref suspended) != 0;

    public PeerStateTracker(PeerStore store, IPeerClock clock, TimeSpan staleAfter, Func<string, bool>? isConnected = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

        this.staleAfter = staleAfter;
        this.isConnected = isConnected ?? (_ => false);
    }

    /// <summary>Queues a connectivity change observed now; applied in order by <see cref="FlushAsync"/> (also started in the background).</summary>
    public void NotifySession(string peerId, bool connected, string? reason = null)
    {
        if (IsSuspended)
        {
            return;
        }

        pending.Enqueue((peerId, connected, reason, clock.UtcNow));
        _ = Task.Run(() => FlushAsync()).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    /// <summary>Applies every queued session event.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
            if (IsSuspended)
            {
                return;
            }

            var now = clock.UtcNow;
            var local = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
            foreach (var peer in await store.ReadTrustedPeersAsync(cancellationToken).ConfigureAwait(false))
            {
                if (peer.PeerId == local?.PeerId)
                {
                    continue;
                }

                var last = await store.ReadLastPeerStateAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
                if (peer.TrustState != PeerTrustState.Active)
                {
                    await store.RecordPeerStateAsync(peer.PeerId, PeerConnectivityState.Revoked, peer.RevokedAt ?? now,
                        "trust revoked", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (last is null)
                {
                    await store.RecordPeerStateAsync(peer.PeerId, PeerConnectivityState.Paired, peer.PairedAt, null,
                        cancellationToken).ConfigureAwait(false);
                    last = await store.ReadLastPeerStateAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
                }

                var connected = isConnected(peer.PeerId);
                if (connected && last!.State != PeerConnectivityState.Online)
                {
                    await store.RecordPeerStateAsync(peer.PeerId, PeerConnectivityState.Online, now, "session established",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (!connected && last!.State == PeerConnectivityState.Online)
                {
                    await store.RecordPeerStateAsync(peer.PeerId, PeerConnectivityState.Offline, now, "session ended",
                        cancellationToken).ConfigureAwait(false);
                }
                else if (!connected && last!.State == PeerConnectivityState.Offline && now - last.At >= staleAfter)
                {
                    await store.RecordPeerStateAsync(peer.PeerId, PeerConnectivityState.Stale, now,
                        $"not seen for {(int)staleAfter.TotalMinutes} minutes", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        // A background flush queued just before shutdown may only get the gate after SuspendAsync returned.
        while (!IsSuspended && pending.TryDequeue(out var item))
        {
            var last = await store.ReadLastPeerStateAsync(item.PeerId, cancellationToken).ConfigureAwait(false);
            if (last?.State == PeerConnectivityState.Revoked)
            {
                continue;
            }

            if (last is null)
            {
                var trusted = await store.ReadTrustedPeerAsync(item.PeerId, cancellationToken).ConfigureAwait(false);
                await store.RecordPeerStateAsync(
                    item.PeerId, PeerConnectivityState.Paired, trusted is null || trusted.PairedAt > item.At ? item.At : trusted.PairedAt,
                    null, cancellationToken).ConfigureAwait(false);
            }

            await store.RecordPeerStateAsync(
                item.PeerId,
                item.Connected ? PeerConnectivityState.Online : PeerConnectivityState.Offline,
                item.At,
                item.Reason ?? (item.Connected ? "session established" : "session ended"),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
#pragma warning restore CA1001
