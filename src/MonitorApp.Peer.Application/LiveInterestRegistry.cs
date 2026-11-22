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

/// <summary>One interest a session declares to its remote peer (<see cref="RelayHops"/> 1 asks it to relay).</summary>
public sealed record OutgoingInterest(string Subject, string[] Signals, TimeSpan Lease, uint RelayHops);

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
    private readonly Dictionary<(string Requester, string Subject), (DateTimeOffset Expires, string[] Signals)> relayed = [];
    private readonly Dictionary<string, List<RemoteWatch>> watches = new(StringComparer.Ordinal);
    private readonly List<Action> listeners = [];
    private readonly TimeProvider time;

    public LiveInterestRegistry(LiveInterestPolicy? policy = null, TimeProvider? timeProvider = null)
    {
        Policy = policy ?? LiveInterestPolicy.Default;
        Policy.Validate();
        time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The most extra hops a relayed interest may ask for; relays always forward with zero remaining.</summary>
    public const uint MaximumRelayHops = 1;

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

    /// <summary>Number of unexpired interests held only to be passed on to the subject peer.</summary>
    public int RelayedInterestCount
    {
        get
        {
            lock (gate)
            {
                Purge(time.GetUtcNow());
                return relayed.Count;
            }
        }
    }

    /// <summary>
    /// Grants, renews or withdraws (lease 0) interest in <paramref name="localPeerId"/>'s own data. Interest in any
    /// other subject is refused unless the requester asked for one relay hop: then it is only remembered, to be passed
    /// to the subject over this peer's own session with it (see <see cref="OutgoingInterestsFor"/>); this peer never
    /// changes how it samples for it.
    /// </summary>
    public LiveInterestAck Grant(string requesterPeerId, string localPeerId, LiveInterest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.SubjectPeerId;
        if (!string.Equals(subject, localPeerId, StringComparison.Ordinal))
        {
            return request.RelayHopsRemaining is > 0 and <= MaximumRelayHops
                ? Relay(requesterPeerId, subject, request)
                : Reject(subject, "Interest is only accepted for the receiving peer's own data.");
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

    private LiveInterestAck Relay(string requesterPeerId, string subject, LiveInterest request)
    {
        if (string.Equals(subject, requesterPeerId, StringComparison.Ordinal) ||
            request.SignalIds.Count > Policy.MaximumSignalsPerInterest ||
            request.SignalIds.Any(signal => string.IsNullOrWhiteSpace(signal) || signal.Length > 512))
        {
            return Reject(subject, "Relayed interest is invalid.");
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
                changed = relayed.Remove(key);
                ack = new LiveInterestAck { SubjectPeerId = subject, Accepted = true };
            }
            else if (!relayed.ContainsKey(key) && relayed.Count >= Policy.MaximumConcurrentInterests)
            {
                return Reject(subject, "Too many concurrent relayed interests.");
            }
            else
            {
                var lease = TimeSpan.FromSeconds(Math.Min(request.LeaseSeconds, Policy.MaximumLease.TotalSeconds));
                var signals = request.SignalIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                changed = !relayed.TryGetValue(key, out var existing) || !existing.Signals.SequenceEqual(signals, StringComparer.Ordinal);
                relayed[key] = (now + lease, signals);
                ack = new LiveInterestAck { SubjectPeerId = subject, Accepted = true, GrantedLeaseSeconds = (uint)lease.TotalSeconds };
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
            foreach (var key in relayed.Keys.Where(key => key.Requester == requesterPeerId).ToArray())
            {
                changed |= relayed.Remove(key);
            }

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
            return RemoteSignalsLocked(remotePeerId);
        }
    }

    private string[] RemoteSignalsLocked(string remotePeerId)
    {
        if (!watches.TryGetValue(remotePeerId, out var list) || list.Any(item => item.Signals.Length == 0))
        {
            return [];
        }

        var union = list.SelectMany(item => item.Signals).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return union.Length > Policy.MaximumSignalsPerInterest ? [] : union;
    }

    /// <summary>True while an unexpired relayed interest names <paramref name="subjectPeerId"/>.</summary>
    public bool HasRelayFor(string subjectPeerId)
    {
        lock (gate)
        {
            Purge(time.GetUtcNow());
            return relayed.Keys.Any(key => key.Subject == subjectPeerId);
        }
    }

    /// <summary>
    /// What a held-open session to <paramref name="remotePeerId"/> must currently declare: the local UI's own interest in
    /// that peer (hops 0), interest relayed to it by other trusted peers while their leases live (hops 0 when it
    /// arrives, so the subject never relays it again; lease capped by the originating lease), and, for peers watched
    /// locally that have no held-open session here, a request for this peer to relay them (hops 1). Relayed interest is
    /// never re-relayed: only local watches produce hop-1 declarations.
    /// </summary>
    public IReadOnlyList<OutgoingInterest> OutgoingInterestsFor(string remotePeerId)
    {
        var result = new List<OutgoingInterest>();
        var now = time.GetUtcNow();
        lock (gate)
        {
            Purge(now);
            var direct = watches.ContainsKey(remotePeerId);
            var forwarded = relayed.Where(item => item.Key.Subject == remotePeerId).ToArray();
            if (direct || forwarded.Length > 0)
            {
                var parts = new List<string[]>();
                var lease = TimeSpan.Zero;
                if (direct)
                {
                    parts.Add(RemoteSignalsLocked(remotePeerId));
                    lease = Policy.OutgoingLease;
                }

                foreach (var (_, value) in forwarded)
                {
                    parts.Add(value.Signals);
                    var remaining = value.Expires - now;
                    var capped = remaining < Policy.OutgoingLease ? remaining : Policy.OutgoingLease;
                    lease = capped > lease ? capped : lease;
                }

                var all = parts.Any(part => part.Length == 0);
                var union = all ? [] : parts.SelectMany(part => part).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                result.Add(new OutgoingInterest(
                    remotePeerId,
                    union.Length > Policy.MaximumSignalsPerInterest ? [] : union,
                    TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(lease.TotalSeconds))),
                    0));
            }

            foreach (var subject in watches.Keys.Where(subject => subject != remotePeerId && PushChannels.SessionCount(subject) == 0))
            {
                result.Add(new OutgoingInterest(subject, RemoteSignalsLocked(subject), Policy.OutgoingLease, MaximumRelayHops));
            }
        }

        return result;
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

        foreach (var key in relayed.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
        {
            relayed.Remove(key);
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
