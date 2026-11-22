using MonitorApp.Peer.Application;
using Xunit;

namespace MonitorApp.Peer.Application.Tests;

public sealed class MonitoringControlStateTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    [Fact]
    public void PausesAreScopedExpireOnTheirOwnAndResumeClearsThem()
    {
        var time = new ManualTime();
        var state = new MonitoringControlState(time);
        var changes = 0;
        using var subscription = state.Subscribe(() => changes++);

        state.Pause("monitor.system", TimeSpan.FromMinutes(10));
        Assert.True(state.IsPaused("monitor.system"));
        Assert.False(state.IsPaused("other"));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(state.IsPaused("monitor.system")); // explicit expiry
        Assert.Empty(state.Read().Pauses);

        state.Pause(null, null); // until resumed
        time.Advance(TimeSpan.FromDays(30));
        Assert.True(state.IsPaused("anything"));
        state.Resume(null);
        Assert.False(state.IsPaused("anything"));
        Assert.Equal(3, changes);
    }

    [Fact]
    public void IntervalOverrideExpiresOrPersistsUntilCleared()
    {
        var time = new ManualTime();
        var state = new MonitoringControlState(time);
        state.SetInterval(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(30), state.IntervalOverride);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(state.IntervalOverride);

        state.SetInterval(TimeSpan.FromSeconds(20), null);
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(TimeSpan.FromSeconds(20), state.IntervalOverride);
        state.SetInterval(null, null);
        Assert.Null(state.IntervalOverride);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Pause("x", TimeSpan.Zero));
    }
}
