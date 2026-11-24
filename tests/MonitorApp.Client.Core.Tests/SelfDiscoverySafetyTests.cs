using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>The promises around discovery and the Syncthing key: off until turned on, key only on this computer.</summary>
public sealed class SelfDiscoverySafetyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoveryIsOffUntilTurnedOnAndTheChoiceIsKept()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var key = EcdsaPeerIdentityKey.Create();
        var identity = new Identity(key);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        try
        {
            await using (var first = new NodeSelfDiscovery(directory, store, identity, new UnsupportedSelfInterfaceCollector()))
            {
                Assert.False(first.GetStatus().Enabled);
                Assert.True(first.SetEnabled(true).Enabled);
                first.Save(8384, false, "abcdefghijklmnop1234");
            }

            await using var second = new NodeSelfDiscovery(directory, store, identity, new UnsupportedSelfInterfaceCollector());
            Assert.True(second.GetStatus().Enabled);
            Assert.False(second.SetEnabled(false).Enabled);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, NodeSelfDiscovery.SyncthingFileName)));
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TheSyncthingKeyIsOnlyHandedToANodeOnThisComputer()
    {
        Assert.True(SyncthingHandover.IsLoopback(new Uri("http://127.0.0.1:55432/")));
        Assert.True(SyncthingHandover.IsLoopback(new Uri("http://localhost:55432/")));
        Assert.False(SyncthingHandover.IsLoopback(new Uri("http://100.101.102.103:55432/")));
        Assert.False(SyncthingHandover.IsLoopback(new Uri("http://192.168.1.20:55432/")));

        Assert.Equal(new SyncthingHandover.LocalSyncthing(8384, true, "KEY1234567890abcd"),
            SyncthingHandover.Parse("<configuration><gui tls=\"true\"><address>127.0.0.1:8384</address><apikey>KEY1234567890abcd</apikey></gui></configuration>"));
        // A GUI bound to another machine's address is not this computer's Syncthing.
        Assert.Null(SyncthingHandover.Parse("<configuration><gui><address>192.168.1.5:8384</address><apikey>KEY1234567890abcd</apikey></gui></configuration>"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RescanOnlyEverAsksSyncthingOnThisComputer()
    {
        var handler = new Recording();
        Assert.False(await MonitorApp.Peer.Collection.Services.SyncthingStatusReader.RescanAllAsync(
            new MonitorApp.Peer.Collection.Services.SyncthingEndpoint(new Uri("http://192.168.1.5:8384/"), "KEY1234567890abcd"), handler));
        Assert.Null(handler.Last);

        Assert.True(await MonitorApp.Peer.Collection.Services.SyncthingStatusReader.RescanAllAsync(
            new MonitorApp.Peer.Collection.Services.SyncthingEndpoint(new Uri("http://127.0.0.1:8384/"), "KEY1234567890abcd"), handler));
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("http://127.0.0.1:8384/rest/db/scan", handler.Last.RequestUri!.ToString());
        Assert.Equal("KEY1234567890abcd", Assert.Single(handler.Last.Headers.GetValues("X-API-Key")));
    }

    private sealed class Recording : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class Identity(IPeerIdentityKey key) : ILocalPeerIdentityProvider
    {
        public PeerPublicIdentity PublicIdentity { get; } = PeerPublicIdentity.Create("peer-local", key);

        public IPeerIdentityKey IdentityKey { get; } = key;
    }
}
