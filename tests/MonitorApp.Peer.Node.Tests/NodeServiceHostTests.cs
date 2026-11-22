using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Node;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class NodeServiceHostTests
{
    [Fact]
    public void ServiceModeUsesStateDirectoryWhenSetOtherwiseVarLib()
    {
        if (OperatingSystem.IsWindows()) return;
        var withState = NodeOptions.Parse([], serviceMode: true, name => name == "STATE_DIRECTORY" ? "/var/lib/monitor-app:/other" : null);
        Assert.Equal("/var/lib/monitor-app", withState.DataDirectory);
        var custom = NodeOptions.Parse([], serviceMode: true, name => name == "STATE_DIRECTORY" ? "/srv/state/mon" : null);
        Assert.Equal("/srv/state/mon", custom.DataDirectory);
        var fallback = NodeOptions.Parse([], serviceMode: true, _ => null);
        Assert.Equal("/var/lib/monitor-app", fallback.DataDirectory);
    }

    [Fact]
    public void LocalPresencePolicyDefaultsToRequiredWhenEnrolledAndRejectsUnknownValues()
    {
        Assert.Equal(LocalPresencePolicy.RequiredWhenEnrolled, NodeOptions.Parse([], false, _ => null).LocalPresence);
        Assert.Equal(
            LocalPresencePolicy.Required,
            NodeOptions.Parse([], false, name => name == NodeOptions.LocalPresenceVariable ? "required" : null).LocalPresence);
        Assert.Equal(LocalPresencePolicy.Off, NodeOptions.Parse(["--local-presence", "off"], false, name => name == NodeOptions.LocalPresenceVariable ? "required" : null).LocalPresence);
        Assert.Throws<ArgumentException>(() => NodeOptions.Parse(["--local-presence", "maybe"], false, _ => null));
    }

    [Fact]
    public void ServiceModeNeverWidensListenersWithoutExplicitConfiguration()
    {
        var options = NodeOptions.Parse([], serviceMode: true, _ => null);
        Assert.Equal(IPAddress.Loopback, options.PeerBindAddress);
        var configured = NodeOptions.Parse(
            [],
            serviceMode: true,
            name => name switch
            {
                NodeOptions.PeerBindVariable => "192.0.2.10",
                NodeOptions.PeerPortVariable => "6000",
                NodeOptions.AdminPortVariable => "6001",
                _ => null,
            });
        Assert.Equal(IPAddress.Parse("192.0.2.10"), configured.PeerBindAddress);
        Assert.Equal(6000, configured.PeerPort);
        Assert.Equal(6001, configured.AdminPort);
    }

    [Fact]
    public void ArgumentsOverrideEnvironmentAndConsoleModeKeepsUserDirectory()
    {
        var dataDir = Path.GetFullPath("arg-dir");
        var options = NodeOptions.Parse(
            ["--data-dir", dataDir, "--admin-port", "0"],
            serviceMode: false,
            name => name == NodeOptions.DataDirectoryVariable ? "env-dir" : name == NodeOptions.AdminPortVariable ? "9" : null);
        Assert.Equal(dataDir, options.DataDirectory);
        Assert.Equal(0, options.AdminPort);
        var fromEnvironment = NodeOptions.Parse([], false, name => name == NodeOptions.DataDirectoryVariable ? "env-dir" : null);
        Assert.Equal(Path.GetFullPath("env-dir"), fromEnvironment.DataDirectory);
        Assert.DoesNotContain("/var/lib", NodeOptions.Parse([], false, _ => null).DataDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericHostStartsServesLoopbackAdminStopsAndRestartKeepsIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "monitor-app-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new NodeOptions(root, IPAddress.Loopback, GetFreePort(), 0);
            string? firstPeerId;
            using (var first = NodeHost.Build(options))
            {
                await first.StartAsync();
                var node = first.Services.GetRequiredService<PeerNodeHostedService>();
                Assert.NotNull(node.AdminAddress);
                Assert.True(IPAddress.IsLoopback(IPAddress.Parse(node.AdminAddress!.Host)));
                using var client = new HttpClient();
                Assert.False(string.IsNullOrWhiteSpace(await client.GetStringAsync(node.AdminAddress)));
                firstPeerId = node.PeerId;
                Assert.NotNull(firstPeerId);
                await first.StopAsync();
                Assert.Null(node.AdminAddress);
            }

            using var second = NodeHost.Build(options with { PeerPort = GetFreePort() });
            await second.StartAsync();
            Assert.Equal(firstPeerId, second.Services.GetRequiredService<PeerNodeHostedService>().PeerId);
            await second.StopAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupFailureSurfacesFromHost()
    {
        var file = Path.GetTempFileName();
        try
        {
            // A data directory beneath a regular file cannot be created; the host must fail to start.
            using var host = NodeHost.Build(new NodeOptions(Path.Combine(file, "sub"), IPAddress.Loopback, GetFreePort(), 0));
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
