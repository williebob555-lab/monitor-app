using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Application;

/// <summary>Bounds on live interest so a peer cannot hold the origin in fast sampling indefinitely or flood it.</summary>
public sealed record LiveInterestPolicy(
    TimeSpan FastSamplingInterval,
    TimeSpan MaximumLease,
    TimeSpan OutgoingLease,
    int MaximumConcurrentInterests,
    int MaximumSignalsPerInterest)
{
    public static LiveInterestPolicy Default { get; } = new(
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), 16, 32);

    public void Validate()
    {
        if (FastSamplingInterval < TimeSpan.FromSeconds(1) || FastSamplingInterval > TimeSpan.FromMinutes(1) ||
            MaximumLease < TimeSpan.FromSeconds(1) || MaximumLease > TimeSpan.FromMinutes(10) ||
            OutgoingLease < TimeSpan.FromSeconds(1) || OutgoingLease > MaximumLease ||
            MaximumConcurrentInterests is < 1 or > 1024 || MaximumSignalsPerInterest is < 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(LiveInterestPolicy));
        }
    }
}

/// <summary>
/// Tracks which authenticated peers (or local UI code) are currently viewing this peer's own data, so the local
/// collection scheduler can sample fast while any unexpired interest exists, and which remote peers the local UI is
/// viewing so held-open sessions can declare and renew that interest.
/// </summary>
public sealed class LiveInterestRegistry
{
    public const string LocalRequester = "local";

    private readonly object gate = new();
    private readonly Dictionary<(string Requester, string Subject), (DateTimeOffset Expires, string[] Signals)> granted = [];
    private readonly Dictionary<string, List<RemoteWatch>> watches = new(StringComparer.Ordinal);
    private readonly List<Action> listeners = [];
    private readonly TimeProvider time;

    public LiveInterestRegistry(LiveInterestPolicy? policy = null, TimeProvider? timeProvider = null)
    {
        Policy = policy ?? LiveInterestPolicy.Default;
        Policy.Validate();
        time = timeProvider ?? TimeProvider.System;
    }

    public LiveInterestPolicy Policy { get; }

    /// <summary>Which held-open session pushes live data to each remote peer (de-duplicates mutual dialling).</summary>
    public LivePushChannels PushChannels { get; } = new();

    /// <summary>Number of unexpired interests currently granted to requesters.</summary>
    public int ActiveInterestCount
    {
        get
        {
            lock (gate)
            {
                Purge(time.GetUtcNow());
                return granted.Count;
            }
        }
    }

    /// <summary>
    /// Grants, renews or withdraws (lease 0) interest in <paramref name="localPeerId"/>'s own data. Interest in any
    /// other subject is refused: this peer only changes how it samples its own origin.
    /// </summary>
    public LiveInterestAck Grant(string requesterPeerId, string localPeerId, LiveInterest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.SubjectPeerId;
        if (!string.Equals(subject, localPeerId, StringComparison.Ordinal))
        {
            return Reject(subject, "Interest is only accepted for the receiving peer's own data.");
        }

        if (request.SignalIds.Count > Policy.MaximumSignalsPerInterest ||
            request.SignalIds.Any(signal => string.IsNullOrWhiteSpace(signal) || signal.Length > 512))
        {
            return Reject(subject, "Interest signal list is invalid or too long.");
        }

        var key = (requesterPeerId, subject);
        var now = time.GetUtcNow();
        bool changed;
        LiveInterestAck ack;
        lock (gate)
        {
            Purge(now);
            if (request.LeaseSeconds == 0)
            {
                changed = granted.Remove(key);
                ack = new LiveInterestAck { SubjectPeerId = subject, Accepted = true };
            }
            else if (!granted.ContainsKey(key) && granted.Count >= Policy.MaximumConcurrentInterests)
            {
                return Reject(subject, "Too many concurrent live interests.");
            }
            else
            {
                var lease = TimeSpan.FromSeconds(Math.Min(request.LeaseSeconds, Policy.MaximumLease.TotalSeconds));
                var signals = request.SignalIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                changed = !granted.TryGetValue(key, out var existing) || !existing.Signals.SequenceEqual(signals, StringComparer.Ordinal);
                granted[key] = (now + lease, signals);
                ack = new LiveInterestAck
                {
                    SubjectPeerId = subject,
                    Accepted = true,
                    GrantedLeaseSeconds = (uint)lease.TotalSeconds,
                    SamplingIntervalMilliseconds = (uint)Policy.FastSamplingInterval.TotalMilliseconds,
                };
            }
        }

        if (changed)
        {
            Notify();
        }

        return ack;
    }

    /// <summary>Withdraws every interest granted to one requester (for example when its trust is revoked).</summary>
    public void WithdrawRequester(string requesterPeerId)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var key in granted.Keys.Where(key => key.Requester == requesterPeerId).ToArray())
            {
                changed |= granted.Remove(key);
            }
        }

        if (changed)
        {
            Notify();
        }
    }

    /// <summary>The local UI views its own data: held fast sampling until the returned handle is disposed.</summary>
    public IDisposable WatchLocal(string localPeerId, IReadOnlyCollection<string>? signalIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        var ack = Grant(LocalRequester, localPeerId, Declaration(localPeerId, Policy.MaximumLease, signalIds));
        if (!ack.Accepted)
        {
            throw new InvalidOperationException(ack.SafeMessage);
        }

        var renew = new Timer(
            _ => Grant(LocalRequester, localPeerId, Declaration(localPeerId, Policy.MaximumLease, signalIds)),
            null,
            Policy.MaximumLease / 3,
            Policy.MaximumLease / 3);
        return new LocalWatch(this, localPeerId, renew);
    }

    /// <summary>True while any unexpired interest in <paramref name="subjectPeerId"/> exists.</summary>
    public bool HasActiveInterest(string subjectPeerId)
    {
        lock (gate)
        {
            Purge(time.GetUtcNow());
            return granted.Keys.Any(key => key.Subject == subjectPeerId);
        }
    }

    /// <summary>
    /// The signals to sample fast for <paramref name="subjectPeerId"/>: null when no interest exists or any viewer wants
    /// everything (an empty signal list means all), otherwise the union of the signals the viewers named.
    /// </summary>
    public IReadOnlySet<string>? FastSignalFilter(string subjectPeerId)
    {
        lock (gate)
        {
            Purge(time.GetUtcNow());
            HashSet<string>? union = null;
            foreach (var (key, value) in granted)
            {
                if (key.Subject != subjectPeerId)
                {
                    continue;
                }

                if (value.Signals.Length == 0)
                {
                    return null;
                }

                union ??= new HashSet<string>(StringComparer.Ordinal);
                union.UnionWith(value.Signals);
            }

            return union;
        }
    }

    /// <summary>The sampling interval to use now: the fast interval while interest is active, otherwise normal.</summary>
    public TimeSpan EffectiveInterval(string subjectPeerId, TimeSpan normal) =>
        HasActiveInterest(subjectPeerId) && Policy.FastSamplingInterval < normal ? Policy.FastSamplingInterval : normal;

    /// <summary>The local UI is viewing a remote peer; sessions to it declare and renew interest until disposed.</summary>
    public IDisposable WatchRemote(string remotePeerId, IReadOnlyCollection<string>? signalIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePeerId);
        var watch = new RemoteWatch(this, remotePeerId, signalIds?.Distinct(StringComparer.Ordinal).ToArray() ?? []);
        lock (gate)
        {
            if (!watches.TryGetValue(remotePeerId, out var list))
            {
                watches[remotePeerId] = list = [];
            }

            list.Add(watch);
        }

        Notify();
        return watch;
    }

    /// <summary>
    /// The signals to declare for the remote peer being viewed: the union over local viewers, or empty (all) when any
    /// viewer wants everything or the union exceeds the per-interest bound.
    /// </summary>
    public IReadOnlyList<string> RemoteSignals(string remotePeerId)
    {
        lock (gate)
        {
            if (!watches.TryGetValue(remotePeerId, out var list) || list.Any(item => item.Signals.Length == 0))
            {
                return [];
            }

            var union = list.SelectMany(item => item.Signals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return union.Length > Policy.MaximumSignalsPerInterest ? [] : union;
        }
    }

    public bool IsWatchingRemote(string remotePeerId)
    {
        lock (gate)
        {
            return watches.ContainsKey(remotePeerId);
        }
    }

    /// <summary>Registers a wake-up callback (must be fast and non-throwing) for any interest change.</summary>
    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate)
        {
            listeners.Add(listener);
        }

        return new Listener(this, listener);
    }

    private static LiveInterest Declaration(string subject, TimeSpan lease, IReadOnlyCollection<string>? signalIds = null)
    {
        var interest = new LiveInterest { SubjectPeerId = subject, LeaseSeconds = (uint)lease.TotalSeconds };
        if (signalIds is not null)
        {
            interest.SignalIds.AddRange(signalIds);
        }

        return interest;
    }

    private static LiveInterestAck Reject(string subject, string message) =>
        new() { SubjectPeerId = subject, Accepted = false, SafeMessage = message };

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in granted.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
        {
            granted.Remove(key);
        }
    }

    private void Notify()
    {
        Action[] snapshot;
        lock (gate)
        {
            snapshot = [.. listeners];
        }

        foreach (var listener in snapshot)
        {
            listener();
        }
    }

    private sealed class LocalWatch(LiveInterestRegistry owner, string localPeerId, Timer renew) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                renew.Dispose();
                owner.Grant(LocalRequester, localPeerId, Declaration(localPeerId, TimeSpan.Zero));
            }
        }
    }

    private sealed class RemoteWatch(LiveInterestRegistry owner, string remotePeerId, string[] signals) : IDisposable
    {
        private int disposed;

        public string[] Signals { get; } = signals;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (owner.gate)
            {
                if (owner.watches.TryGetValue(remotePeerId, out var list))
                {
                    list.Remove(this);
                    if (list.Count == 0)
                    {
                        owner.watches.Remove(remotePeerId);
                    }
                }
            }

            owner.Notify();
        }
    }

    private sealed class Listener(LiveInterestRegistry owner, Action action) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.listeners.Remove(action);
            }
        }
    }
}
