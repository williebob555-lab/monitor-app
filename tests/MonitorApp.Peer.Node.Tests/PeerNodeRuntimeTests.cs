using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class PeerNodeRuntimeTests
{
    [Fact]
    public async Task IdentityIsRestartStableAndKeyFileIsNotRawKeyMaterial()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(root, "peer.db")));
            await store.InitializeAsync();
            string firstPeerId;
            byte[] firstFingerprint;
            await using (var first = new AsyncIdentity(await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
                Path.Combine(root, "identity.dpapi"), store)))
            {
                firstPeerId = first.Value.PublicIdentity.PeerId;
                firstFingerprint = first.Value.PublicIdentity.PublicKeyFingerprint;
            }

            await using (var second = new AsyncIdentity(await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
                Path.Combine(root, "identity.dpapi"), store)))
            {
                Assert.Equal(firstPeerId, second.Value.PublicIdentity.PeerId);
                Assert.Equal(firstFingerprint, second.Value.PublicIdentity.PublicKeyFingerprint);
                var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(root, "identity.dpapi"));
                Assert.False(ContainsSequence(protectedBytes, second.Value.PublicIdentity.PublicKeySpki));
                Assert.False(ContainsSequence(protectedBytes, firstFingerprint));
                using var parser = ECDsa.Create();
                Assert.ThrowsAny<CryptographicException>(() =>
                    parser.ImportPkcs8PrivateKey(protectedBytes, out _));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoNodesStartWithSeparateStoresAndExposeLocalPages()
    {
        if (!OperatingSystem.IsWindows()) return;
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var firstOptions = new NodeOptions(firstRoot, IPAddress.Loopback, FreePort(), 0);
            var secondOptions = new NodeOptions(secondRoot, IPAddress.Loopback, FreePort(), 0);
            await using var first = await PeerNodeRuntime.StartAsync(firstOptions);
            await using var second = await PeerNodeRuntime.StartAsync(secondOptions);
            Assert.NotEqual(first.Identity.PeerId, second.Identity.PeerId);
            Assert.NotNull(first.AdminAddress);
            Assert.NotNull(second.AdminAddress);
            Assert.NotEqual(first.AdminAddress, second.AdminAddress);

            using var client = new HttpClient();
            var firstPage = await client.GetStringAsync(first.AdminAddress);
            var secondPage = await client.GetStringAsync(second.AdminAddress);
            Assert.Contains("Monitor App local administration", firstPage, StringComparison.Ordinal);
            Assert.Contains("Monitor App local administration", secondPage, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(firstRoot, "peer.db")));
            Assert.True(File.Exists(Path.Combine(secondRoot, "peer.db")));
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-node-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return true;
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle)) return true;
        }

        return false;
    }

    private sealed class AsyncIdentity(WindowsDpapiIdentityProvider value) : IAsyncDisposable
    {
        public WindowsDpapiIdentityProvider Value { get; } = value;

        public ValueTask DisposeAsync()
        {
            Value.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
