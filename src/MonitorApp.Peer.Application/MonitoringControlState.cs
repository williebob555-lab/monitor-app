namespace MonitorApp.Peer.Application;

/// <summary>
/// Manual monitoring overrides applied by an authorised operator (a trusted phone's signed control command or the
/// local administrator): collector pauses and a sampling-interval override. State is deliberately in memory only, so a
/// restart of the peer always returns to normal monitoring (fail towards collecting, never towards silent blindness).
/// A pause or interval override either carries an explicit expiry or lasts until it is cleared or the process restarts.
/// Live viewing interest keeps working underneath an interval override (the faster of the two wins) but never overrides
/// a pause: an operator who paused collection has said so explicitly.
/// </summary>
public sealed class MonitoringControlState(TimeProvider? timeProvider = null)
{
    /// <summary>Scope that pauses every collector.</summary>
    public const string AllScope = "*";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset?> pauses = new(StringComparer.Ordinal);
    private readonly List<Action> listeners = [];
    private TimeSpan? interval;
    private DateTimeOffset? intervalUntil;

    public sealed record Snapshot(
        IReadOnlyDictionary<string, DateTimeOffset?> Pauses,
        TimeSpan? IntervalOverride,
        DateTimeOffset? IntervalOverrideUntil);

    /// <summary>True while the collector (or every collector) is paused and the pause has not expired.</summary>
    public bool IsPaused(string collectorId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            return IsActive(AllScope, now) || IsActive(collectorId, now);
        }
    }

    /// <summary>The manual sampling interval in force, or null when none (or it has expired).</summary>
    public TimeSpan? IntervalOverride
    {
        get
        {
            lock (gate)
            {
                return interval is not null && (intervalUntil is null || clock.GetUtcNow() < intervalUntil) ? interval : null;
            }
        }
    }

    public Snapshot Read()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var active = pauses.Where(pair => pair.Value is null || pair.Value > now)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var live = interval is not null && (intervalUntil is null || now < intervalUntil);
            return new Snapshot(active, live ? interval : null, live ? intervalUntil : null);
        }
    }

    /// <summary>Pauses one collector (or all when empty). A null duration pauses until resumed.</summary>
    public void Pause(string? collectorId, TimeSpan? duration)
    {
        if (duration is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var scope = string.IsNullOrWhiteSpace(collectorId) ? AllScope : collectorId;
        lock (gate)
        {
            pauses[scope] = duration is null ? null : clock.GetUtcNow() + duration;
        }

        Notify();
    }

    /// <summary>Resumes one collector, or everything (all pauses cleared) when empty.</summary>
    public void Resume(string? collectorId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(collectorId))
            {
                pauses.Clear();
            }
            else
            {
                pauses.Remove(collectorId);
            }
        }

        Notify();
    }

    /// <summary>Sets (or, with null, clears) the manual normal sampling interval, optionally expiring on its own.</summary>
    public void SetInterval(TimeSpan? value, TimeSpan? duration)
    {
        if (value is { } v && v <= TimeSpan.Zero || duration is { } d && d <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        lock (gate)
        {
            interval = value;
            intervalUntil = value is null || duration is null ? null : clock.GetUtcNow() + duration;
        }

        Notify();
    }

    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate)
        {
            listeners.Add(listener);
        }

        return new Subscription(this, listener);
    }

    private bool IsActive(string scope, DateTimeOffset now) =>
        pauses.TryGetValue(scope, out var until) && (until is null || until > now);

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

    private sealed class Subscription(MonitoringControlState owner, Action listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.listeners.Remove(listener);
            }
        }
    }
}
