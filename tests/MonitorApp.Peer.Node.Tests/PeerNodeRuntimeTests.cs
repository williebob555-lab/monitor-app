using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task TwoFreshNodesPairThroughLocalWebApproval()
    {
        if (!OperatingSystem.IsWindows()) return;
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var firstPeerPort = FreePort();
            var secondPeerPort = FreePort();
            await using var first = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0));
            await using var second = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0));
            using var firstClient = CreateCookieClient();
            using var secondClient = CreateCookieClient();
            var firstCsrf = await ReadCsrfAsync(firstClient, first.AdminAddress!);
            var invitation = JsonSerializer.Serialize(new
            {
                expectedPeerId = second.Identity.PeerId,
                address = "127.0.0.1",
                port = secondPeerPort,
                expectedFingerprintSha256 = Convert.ToHexString(second.Identity.PublicKeyFingerprint),
                securityPolicy = "LoopbackPrototype",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(first.AdminAddress!, "/api/invitations"))
            {
                Content = new StringContent(invitation, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-CSRF-TOKEN", firstCsrf);
            var invitationTask = firstClient.SendAsync(request);

            string? pairingId = null;
            for (var attempt = 0; attempt < 50 && pairingId is null; attempt++)
            {
                await Task.Delay(100);
                var json = await secondClient.GetStringAsync(new Uri(second.AdminAddress!, "/api/pairings"));
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.GetArrayLength() > 0)
                {
                    pairingId = document.RootElement[0].GetProperty("pairingId").GetString();
                }
            }

            Assert.False(string.IsNullOrWhiteSpace(pairingId));
            var secondCsrf = await ReadCsrfAsync(secondClient, second.AdminAddress!);
            using var approval = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(second.AdminAddress!, $"/api/pairings/{Uri.EscapeDataString(pairingId!)}/approve"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            approval.Headers.Add("X-CSRF-TOKEN", secondCsrf);
            using var approvalResponse = await secondClient.SendAsync(approval);
            approvalResponse.EnsureSuccessStatusCode();
            using var invitationResponse = await invitationTask.WaitAsync(TimeSpan.FromSeconds(15));
            invitationResponse.EnsureSuccessStatusCode();
            var result = await invitationResponse.Content.ReadAsStringAsync();
            using (var resultDocument = JsonDocument.Parse(result))
            {
                Assert.Equal(1, resultDocument.RootElement.GetProperty("status").GetInt32());
                Assert.Contains("Pairing completed", resultDocument.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var secondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));
            var firstTrust = await firstStore.ReadTrustedPeerAsync(second.Identity.PeerId);
            var secondTrust = await secondStore.ReadTrustedPeerAsync(first.Identity.PeerId);
            Assert.Equal(PeerTrustState.Active, firstTrust?.TrustState);
            Assert.Equal(PeerTrustState.Active, secondTrust?.TrustState);
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

    private static HttpClient CreateCookieClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    });

    private static async Task<string> ReadCsrfAsync(HttpClient client, Uri baseAddress)
    {
        var json = await client.GetStringAsync(new Uri(baseAddress, "/api/csrf"));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("requestToken").GetString() ??
            throw new InvalidOperationException("CSRF endpoint returned no request token.");
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
