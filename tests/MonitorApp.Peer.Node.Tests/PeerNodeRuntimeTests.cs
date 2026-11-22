using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

public sealed class PeerNodeRuntimeTests
{
    [Fact]
    [Trait("Category", "Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var capability = Assert.Single(await firstStore.ReadCapabilitiesAsync());
            var declaration = CapabilityModuleDeclaration.Parser.ParseFrom(capability.Descriptor);
            Assert.Equal("monitor.system", declaration.ModuleId);
            Assert.True(CapabilityContractValidator.Validate(declaration).IsValid);
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
            await PairAsync(first, second, secondPeerPort);

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

    [Fact]
    public async Task SeparateStoresCollectOfflineThenReconnectAndConvergeWithoutDuplicates()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        var firstPeerPort = FreePort();
        var secondPeerPort = FreePort();
        try
        {
            string firstPeerId;
            string secondPeerId;
            string firstOriginStreamId;
            string secondOriginStreamId;
            int firstInitialCollection;
            int secondInitialCollection;
            await using (var first = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0)))
            await using (var second = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0)))
            {
                firstPeerId = first.Identity.PeerId;
                secondPeerId = second.Identity.PeerId;
                firstInitialCollection = (await first.CollectOnceAsync()).AppendedRecords;
                secondInitialCollection = (await second.CollectOnceAsync()).AppendedRecords;
                Assert.True(firstInitialCollection > 0);
                Assert.True(secondInitialCollection > 0);
                await PairAsync(first, second, secondPeerPort);
                var initialFirstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
                var initialSecondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));
                firstOriginStreamId = (await initialFirstStore.ReadLocalIdentityAsync())!.OriginStreamId;
                secondOriginStreamId = (await initialSecondStore.ReadLocalIdentityAsync())!.OriginStreamId;
                await WaitUntilAsync(async () =>
                {
                    var onFirst = await initialFirstStore.ReadOriginRangeAsync(
                        secondPeerId,
                        secondOriginStreamId,
                        1,
                        10_000);
                    var onSecond = await initialSecondStore.ReadOriginRangeAsync(
                        firstPeerId,
                        firstOriginStreamId,
                        1,
                        10_000);
                    return onFirst.Count == secondInitialCollection &&
                           onSecond.Count == firstInitialCollection;
                }, TimeSpan.FromSeconds(20));
            }

            int firstCollected;
            await using (var firstOffline = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0)))
            {
                firstCollected = (await firstOffline.CollectOnceAsync()).AppendedRecords;
                Assert.True(firstCollected > 0);
            }

            int secondCollected;
            await using (var secondOffline = await PeerNodeRuntime.StartAsync(
                             new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0)))
            {
                secondCollected = (await secondOffline.CollectOnceAsync()).AppendedRecords;
                Assert.True(secondCollected > 0);
            }

            await using var firstRestarted = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, firstPeerPort, 0));
            await using var secondRestarted = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0));
            var firstStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(firstRoot, "peer.db")));
            var secondStore = new PeerStore(PeerStoreOptions.Create(Path.Combine(secondRoot, "peer.db")));

            await WaitUntilAsync(async () =>
            {
                var onFirst = await firstStore.ReadOriginRangeAsync(
                    secondPeerId,
                    secondOriginStreamId,
                    1,
                    10_000);
                var onSecond = await secondStore.ReadOriginRangeAsync(
                    firstPeerId,
                    firstOriginStreamId,
                    1,
                    10_000);
                return onFirst.Count == secondInitialCollection + secondCollected &&
                       onSecond.Count == firstInitialCollection + firstCollected;
            }, TimeSpan.FromSeconds(20));

            await AssertOriginMatchesAsync(
                firstStore,
                secondStore,
                firstPeerId,
                firstOriginStreamId,
                firstInitialCollection + firstCollected);
            await AssertOriginMatchesAsync(
                firstStore,
                secondStore,
                secondPeerId,
                secondOriginStreamId,
                secondInitialCollection + secondCollected);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HeldOpenNodeSessionStreamsNewlyCollectedRecordsBothWaysWithoutReconnecting()
    {
        var firstRoot = CreateTemporaryDirectory();
        var secondRoot = CreateTemporaryDirectory();
        try
        {
            var secondPeerPort = FreePort();
            await using var first = await PeerNodeRuntime.StartAsync(
                new NodeOptions(firstRoot, IPAddress.Loopback, FreePort(), 0));
            await using var second = await PeerNodeRuntime.StartAsync(
                new NodeOptions(secondRoot, IPAddress.Loopback, secondPeerPort, 0));
            await PairAsync(first, second, secondPeerPort);
            var firstStream = (await first.Store.ReadLocalIdentityAsync())!.OriginStreamId;
            var secondStream = (await second.Store.ReadLocalIdentityAsync())!.OriginStreamId;
            // The first records may ride the session's initial catch-up; once they arrived the session is held open
            // and everything measured next can only arrive live (the first node dials once; the heartbeat is 30 s).
            await CollectAndMeasureAsync(first, second);
            await CollectAndMeasureAsync(second, first);

            var toSecond = await CollectAndMeasureAsync(first, second);
            var toFirst = await CollectAndMeasureAsync(second, first);
            Console.WriteLine($"node live latency: A->B {toSecond.TotalMilliseconds:F1} ms, B->A {toFirst.TotalMilliseconds:F1} ms");

            await AssertOriginMatchesAsync(
                first.Store, second.Store, first.Identity.PeerId, firstStream,
                (int)((await first.Store.ReadLocalIdentityAsync())!.NextSequence - 1));
            await AssertOriginMatchesAsync(
                first.Store, second.Store, second.Identity.PeerId, secondStream,
                (int)((await second.Store.ReadLocalIdentityAsync())!.NextSequence - 1));
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    private static async Task<TimeSpan> CollectAndMeasureAsync(PeerNodeRuntime origin, PeerNodeRuntime receiver)
    {
        var originId = origin.Identity.PeerId;
        var committed = new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        var arrived = new System.Collections.Concurrent.ConcurrentDictionary<ulong, long>();
        using var localSubscription = origin.Store.Live.Subscribe(record =>
        {
            if (record.OriginPeerId == originId) committed.TryAdd(record.Sequence, System.Diagnostics.Stopwatch.GetTimestamp());
        });
        using var remoteSubscription = receiver.Store.Live.Subscribe(record =>
        {
            if (record.OriginPeerId == originId) arrived.TryAdd(record.Sequence, System.Diagnostics.Stopwatch.GetTimestamp());
        });
        var result = await origin.CollectOnceAsync();
        Assert.True(result.AppendedRecords > 0);
        var last = result.LastSequence!.Value;
        await WaitUntilAsync(() => Task.FromResult(arrived.ContainsKey(last)), TimeSpan.FromSeconds(30));
        var latency = System.Diagnostics.Stopwatch.GetElapsedTime(committed[last], arrived[last]);
        Assert.True(latency < TimeSpan.FromSeconds(10), $"Live delivery took {latency}.");
        return latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
    }

    private static async Task PairAsync(
        PeerNodeRuntime first,
        PeerNodeRuntime second,
        int secondPeerPort)
    {
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
        using var resultDocument = JsonDocument.Parse(result);
        Assert.Equal(1, resultDocument.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(
            "Pairing completed",
            resultDocument.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    private static async Task AssertOriginMatchesAsync(
        PeerStore first,
        PeerStore second,
        string originPeerId,
        string originStreamId,
        int expectedCount)
    {
        var firstRecords = await first.ReadOriginRangeAsync(originPeerId, originStreamId, 1, 10_000);
        var secondRecords = await second.ReadOriginRangeAsync(originPeerId, originStreamId, 1, 10_000);
        Assert.Equal(expectedCount, firstRecords.Count);
        Assert.Equal(expectedCount, secondRecords.Count);
        Assert.Equal(
            Enumerable.Range(1, expectedCount).Select(value => (ulong)value),
            firstRecords.Select(record => record.Sequence));
        Assert.Equal(
            firstRecords.Select(record => Convert.ToHexString(record.ContentHash)),
            secondRecords.Select(record => Convert.ToHexString(record.ContentHash)));
        Assert.Equal(
            firstRecords.Select(record => Convert.ToHexString(record.Signature)),
            secondRecords.Select(record => Convert.ToHexString(record.Signature)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            await Task.Delay(100, cancellation.Token);
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

    private sealed class AsyncIdentity(INodeIdentityProvider value) : IAsyncDisposable
    {
        public INodeIdentityProvider Value { get; } = value;

        public ValueTask DisposeAsync()
        {
            Value.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
