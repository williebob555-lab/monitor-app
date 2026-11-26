using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Peer.Application;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>AppVisibility is process-wide, so these tests must not run beside other live-loop tests.</summary>
[CollectionDefinition(nameof(AppVisibilityTests), DisableParallelization = true)]
public sealed class AppVisibilityTestGroup;

/// <summary>
/// A hidden app (an Android phone with its screen off) must not refresh screens or keep a device on fast sampling:
/// before this, live loops stopped only when their view detached, which a stopped activity never does.
/// </summary>
[Collection(nameof(AppVisibilityTests))]
public sealed class AppVisibilityTests
{
    [Fact]
    public async Task HidingPausesRefreshesAndEndsFastSamplingAndShowingResumesBoth()
    {
        var registry = new LiveInterestRegistry();
        await using var node = await NodeFixture.CreateAsync(interests: registry);
        using var connection = new NodeConnection(node.NewClient());
        var section = new CountingSection(connection);
        using var stop = new CancellationTokenSource();
        try
        {
            var run = section.RunLiveAsync(TimeSpan.FromMilliseconds(50), stop.Token);
            Assert.True(await WaitForAsync(() => section.Refreshes >= 3 && registry.HasActiveInterest("peer-local")));

            AppVisibility.Set(false);
            Assert.True(await WaitForAsync(() => !registry.HasActiveInterest("peer-local")));
            var whileHidden = section.Refreshes;
            await Task.Delay(500);
            Assert.InRange(section.Refreshes, whileHidden, whileHidden + 1);

            AppVisibility.Set(true);
            Assert.True(await WaitForAsync(() => section.Refreshes >= whileHidden + 3 && registry.HasActiveInterest("peer-local")));

            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await WaitForAsync(() => !registry.HasActiveInterest("peer-local")));
        }
        finally
        {
            AppVisibility.Set(true);
        }
    }

    [Fact]
    public async Task ALoopStartedWhileHiddenWaitsAndCancelsCleanly()
    {
        await using var node = await NodeFixture.CreateAsync(interests: new LiveInterestRegistry());
        using var connection = new NodeConnection(node.NewClient());
        var section = new CountingSection(connection);
        using var stop = new CancellationTokenSource();
        AppVisibility.Set(false);
        try
        {
            var run = section.RunLiveAsync(TimeSpan.FromMilliseconds(50), stop.Token);
            await Task.Delay(300);
            Assert.Equal(0, section.Refreshes);
            await stop.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, section.Refreshes);
        }
        finally
        {
            AppVisibility.Set(true);
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    private sealed class CountingSection(NodeConnection connection) : LiveSectionViewModel(connection, "Counting")
    {
        private int refreshes;

        public int Refreshes => Volatile.Read(ref refreshes);

        public override Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref refreshes);
            return Task.CompletedTask;
        }
    }
}
