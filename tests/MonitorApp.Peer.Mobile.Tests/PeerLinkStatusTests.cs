using System.Net.Sockets;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>R4: a device's card says whether its link works, and why not, in words.</summary>
public sealed class PeerLinkStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheBoardFollowsALinkThroughConnectingFailingAndConnectingAgain()
    {
        var time = new ManualTime(Now);
        var board = new PeerLinkStatusBoard(time);
        board.Connecting("laptop", "100.64.0.2:55433");
        Assert.Equal("Connecting to Laptop (100.64.0.2:55433)… No readings from Laptop have arrived here yet.",
            PeerLinkStatusBoard.Describe(board.Get("laptop"), null, time.GetUtcNow(), "Laptop"));

        board.Failed("laptop", PeerLinkErrors.Plain(new SocketException((int)SocketError.TimedOut)), retrying: true);
        Assert.Equal(
            "Can't reach Laptop (100.64.0.2:55433): it did not answer in time (it may be off, asleep, or on another network). Trying again. " +
            "Last reading from Laptop: 2 hours ago.",
            PeerLinkStatusBoard.Describe(board.Get("laptop"), Now.AddHours(-2), time.GetUtcNow(), "Laptop"));

        board.Connected("laptop", "192.168.1.20:55433");
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Connected (192.168.1.20:55433) for 5 minutes. Last reading from Laptop: just now.",
            PeerLinkStatusBoard.Describe(board.Get("laptop"), time.GetUtcNow().AddSeconds(-10), time.GetUtcNow(), "Laptop"));

        // A phone dialed in and left: this computer can't call it, so it says it waits.
        board.Connected("phone");
        board.Disconnected("phone", canDial: false);
        Assert.Equal(PeerLinkState.Waiting, board.Get("phone")!.State);
        Assert.StartsWith("Not connected. This device can't call Phone; it waits for Phone to connect (last connected",
            PeerLinkStatusBoard.Describe(board.Get("phone"), null, time.GetUtcNow(), "Phone"), StringComparison.Ordinal);

        // A refusal stops retrying and says so.
        board.Failed("laptop", PeerLinkErrors.Plain(new PeerAuthenticationException("unknown peer")), retrying: false);
        Assert.Contains("Stopped trying to reach Laptop: it did not accept this device: unknown peer.",
            PeerLinkStatusBoard.Describe(board.Get("laptop"), null, time.GetUtcNow(), "Laptop"), StringComparison.Ordinal);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
