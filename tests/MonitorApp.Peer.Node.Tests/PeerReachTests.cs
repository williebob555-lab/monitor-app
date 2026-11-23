using System.Net;
using MonitorApp.Peer.Node;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class PeerReachTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-peer-reach-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    public void OnlyTailscaleAddressesCount(string address, bool expected) =>
        Assert.Equal(expected, PeerReachSetting.IsTailscale(IPAddress.Parse(address)));

    [Fact]
    public void SavedChoiceBindsTheTailscaleAddressOrFallsBackToThisComputer()
    {
        Assert.Equal(IPAddress.Loopback, PeerReachSetting.ResolveSaved(directory, TimeSpan.Zero, () => [IPAddress.Parse("100.100.1.2")]));

        PeerReachSetting.Write(directory, true);
        Assert.Equal(IPAddress.Parse("100.100.1.2"),
            PeerReachSetting.ResolveSaved(directory, TimeSpan.Zero, () => [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("100.100.1.2")]));
        // Tailscale not up: never a LAN or wildcard address, always this computer only.
        Assert.Equal(IPAddress.Loopback, PeerReachSetting.ResolveSaved(directory, TimeSpan.Zero, () => [IPAddress.Parse("192.168.1.5")]));

        PeerReachSetting.Write(directory, false);
        Assert.False(PeerReachSetting.ReadWantsReach(directory));
    }

    [Fact]
    public void ArgumentsAndEnvironmentWinOverTheSavedChoice()
    {
        PeerReachSetting.Write(directory, true);
        var fromSetting = NodeOptions.Parse(["--data-dir", directory], environment: _ => null);
        Assert.Equal("setting", fromSetting.PeerBindSource);

        var fromEnvironment = NodeOptions.Parse(["--data-dir", directory],
            environment: name => name == NodeOptions.PeerBindVariable ? "127.0.0.1" : null);
        Assert.Equal("environment", fromEnvironment.PeerBindSource);
        Assert.False(new PeerReachController(fromEnvironment, _ => { }).GetStatus().CanChange);

        var fromArgument = NodeOptions.Parse(["--data-dir", directory, "--peer-bind", "127.0.0.1"], environment: _ => null);
        Assert.Equal("argument", fromArgument.PeerBindSource);
        Assert.False(new PeerReachController(fromArgument, _ => { }).Change(false).Accepted);
    }

    [Fact]
    public async Task TurningOffSavesAndRestartsTheNode()
    {
        PeerReachSetting.Write(directory, true);
        var options = NodeOptions.Parse(["--data-dir", directory], environment: _ => null);
        var exited = new TaskCompletionSource<int>();
        var controller = new PeerReachController(options, code => exited.TrySetResult(code));

        var change = controller.Change(false);

        Assert.True(change.Accepted);
        Assert.True(change.Status.RestartPending);
        Assert.False(PeerReachSetting.ReadWantsReach(directory));
        Assert.Equal(PeerReachController.RestartExitCode, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
