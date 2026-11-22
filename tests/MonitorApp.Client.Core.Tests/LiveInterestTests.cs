using MonitorApp.Peer.Application;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>Live-interest leases through the real client against the real loopback host and registry.</summary>
public sealed class LiveInterestTests
{
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

    [Fact]
    public async Task LeaseHoldsFastSamplingUntilEnded()
    {
        var registry = new LiveInterestRegistry();
        await using var node = await NodeFixture.CreateAsync(interests: registry);
        using var client = node.NewClient();

        var lease = await client.StartLiveInterestAsync(null, ["cpu.load"]);
        Assert.False(string.IsNullOrEmpty(lease.LeaseId));
        Assert.True(lease.RenewAfterSeconds >= 1);
        Assert.True(registry.HasActiveInterest("peer-local"));
        Assert.Equal(["cpu.load"], registry.FastSignalFilter("peer-local")!.ToArray());

        Assert.NotNull(await client.RenewLiveInterestAsync(lease.LeaseId));
        await client.EndLiveInterestAsync(lease.LeaseId);
        Assert.False(registry.HasActiveInterest("peer-local"));
        Assert.Null(await client.RenewLiveInterestAsync(lease.LeaseId));
        await client.EndLiveInterestAsync(lease.LeaseId);
    }

    [Fact]
    public async Task RemoteInterestRequiresATrustedPeer()
    {
        var registry = new LiveInterestRegistry();
        await using var node = await NodeFixture.CreateAsync(interests: registry);
        using var client = node.NewClient();

        await Assert.ThrowsAnyAsync<Exception>(() => client.StartLiveInterestAsync("peer-stranger"));
        var lease = await client.StartLiveInterestAsync("peer-trusted", ["net.rx"]);
        Assert.True(registry.IsWatchingRemote("peer-trusted"));
        await client.EndLiveInterestAsync(lease.LeaseId);
        Assert.False(registry.IsWatchingRemote("peer-trusted"));
    }

    [Fact]
    public async Task BeginViewingCreatesAndEndsTheLeaseInTheBackground()
    {
        var registry = new LiveInterestRegistry();
        await using var node = await NodeFixture.CreateAsync(interests: registry);
        using var client = node.NewClient();

        var viewing = client.BeginViewing(null);
        Assert.True(await WaitForAsync(() => registry.HasActiveInterest("peer-local")));
        viewing.Dispose();
        Assert.True(await WaitForAsync(() => !registry.HasActiveInterest("peer-local")));
    }

    [Fact]
    public async Task BeginViewingNeverThrowsWhenTheNodeIsUnavailable()
    {
        var node = await NodeFixture.CreateAsync(interests: new LiveInterestRegistry());
        using var client = node.NewClient();
        await node.DisposeAsync();

        var viewing = client.BeginViewing(null);
        await Task.Delay(100);
        viewing.Dispose();
    }

    [Fact]
    public async Task InvalidSignalListsAreRejected()
    {
        var registry = new LiveInterestRegistry();
        await using var node = await NodeFixture.CreateAsync(interests: registry);
        using var client = node.NewClient();

        await Assert.ThrowsAnyAsync<Exception>(() => client.StartLiveInterestAsync(null, [new string('x', 200)]));
        Assert.False(registry.HasActiveInterest("peer-local"));
    }
}
