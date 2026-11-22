using System.Security.Cryptography;
using MonitorApp.Peer.Application;

namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// Leases for live interest declared by the local UI over the loopback API. Each lease holds a registry watch (this
/// node's own data, or a remote peer over its held-open session) and drops it when the lease is ended or lapses, so a
/// UI that vanishes without ending its lease never pins fast sampling.
/// </summary>
internal sealed class LiveInterestLeases : IDisposable
{
    public const int MaximumLeases = 32;
    public const int MaximumSignals = 64;
    public const int MaximumSignalLength = 128;

    private readonly LiveInterestRegistry registry;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private bool disposed;

    public LiveInterestLeases(LiveInterestRegistry registry, TimeProvider? time = null)
    {
        this.registry = registry;
        this.time = time ?? TimeProvider.System;
    }

    public TimeSpan LeaseDuration => registry.Policy.OutgoingLease;

    public int RenewAfterSeconds => Math.Max(1, (int)(LeaseDuration.TotalSeconds / 3));

    public static bool AreValidSignals(string[]? signalIds) =>
        signalIds is null ||
        (signalIds.Length <= MaximumSignals &&
         signalIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumSignalLength));

    /// <summary>Starts a lease, or returns null when the concurrent lease cap is reached.</summary>
    public (string LeaseId, DateTimeOffset ExpiresAt)? TryBegin(Func<IDisposable> watch)
    {
        lock (gate)
        {
            if (disposed || leases.Count >= MaximumLeases)
            {
                return null;
            }

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var lease = new Lease(watch());
            lease.Timer = time.CreateTimer(_ => Expire(id, lease), null, LeaseDuration, Timeout.InfiniteTimeSpan);
            leases[id] = lease;
            return (id, time.GetUtcNow() + LeaseDuration);
        }
    }

    public DateTimeOffset? Renew(string leaseId)
    {
        lock (gate)
        {
            if (disposed || !leases.TryGetValue(leaseId, out var lease))
            {
                return null;
            }

            lease.Timer!.Change(LeaseDuration, Timeout.InfiniteTimeSpan);
            return time.GetUtcNow() + LeaseDuration;
        }
    }

    public bool End(string leaseId)
    {
        Lease? lease;
        lock (gate)
        {
            if (!leases.Remove(leaseId, out lease))
            {
                return false;
            }
        }

        lease.Dispose();
        return true;
    }

    public void Dispose()
    {
        List<Lease> all;
        lock (gate)
        {
            disposed = true;
            all = [.. leases.Values];
            leases.Clear();
        }

        foreach (var lease in all)
        {
            lease.Dispose();
        }
    }

    private void Expire(string id, Lease lease)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(id, out var current) || !ReferenceEquals(current, lease))
            {
                return;
            }

            leases.Remove(id);
        }

        lease.Dispose();
    }

    private sealed class Lease(IDisposable watch) : IDisposable
    {
        public ITimer? Timer { get; set; }

        public void Dispose()
        {
            Timer?.Dispose();
            watch.Dispose();
        }
    }
}
