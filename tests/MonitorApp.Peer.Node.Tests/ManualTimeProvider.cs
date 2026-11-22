namespace MonitorApp.Peer.Node.Tests;

/// <summary>A clock tests advance by hand; timers (and so <see cref="PeriodicTimer"/>) fire only on <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.NextFire is { } due && due <= target).OrderBy(timer => timer.NextFire).FirstOrDefault();
                if (next is null)
                {
                    now = target;
                    return;
                }

                now = next.NextFire!.Value;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? NextFire { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            lock (owner.gate)
            {
                period = newPeriod;
                NextFire = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner.gate)
            {
                NextFire = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? null : owner.now + period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                NextFire = null;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
