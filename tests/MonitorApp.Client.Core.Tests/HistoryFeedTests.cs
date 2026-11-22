using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Node;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>The history feed end to end: two real nodes, a peer that drops and returns, read through LocalApiClient.</summary>
public sealed class HistoryFeedTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task PeerGoingOfflineAndBackAppearsInOrderInTheHistoryFeedAndUpdateOutcomesAreServed()
    {
        var rootA = Path.Combine(Path.GetTempPath(), "monitor-history-tests", Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "monitor-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        try
        {
            var portA = FreePort();
            var portB = FreePort();
            var recorded = new DateTimeOffset(2026, 11, 20, 21, 14, 0, TimeSpan.Zero);
            var updates = new FakeUpdates(
            [
                new LocalUpdateEvent("1.2.0", "Applied", recorded, "ok"),
                new LocalUpdateEvent("1.3.0", "RolledBack", recorded.AddDays(1), "health check failed"),
                new LocalUpdateEvent("1.4.0", "Failed", recorded.AddDays(2), null),
            ]);
            var fast = new NodeLiveOptions(PeerStateInterval: TimeSpan.FromSeconds(1), CollectSelfLan: false);
            var before = DateTimeOffset.UtcNow.AddSeconds(-5);
            await using var a = await PeerNodeRuntime.StartAsync(
                new NodeOptions(rootA, IPAddress.Loopback, portA, 0), fast, updates);
            using var apiClient = LocalApiClient.Create(a.AdminAddress!);
            var feed = apiClient;
            string peerBId;
            var b = await PeerNodeRuntime.StartAsync(new NodeOptions(rootB, IPAddress.Loopback, portB, 0), fast);
            try
            {
                peerBId = b.Identity.PeerId;
                await PairAsync(a, b, portB);
                await WaitAsync(async () => (await Transitions(feed, before)).Any(t => t.State == "Online"));
            }
            finally
            {
                await b.DisposeAsync();
            }

            await WaitAsync(async () => (await Transitions(feed, before)).Any(t => t.State == "Offline"));

            await using var bAgain = await PeerNodeRuntime.StartAsync(new NodeOptions(rootB, IPAddress.Loopback, portB, 0), fast);
            await WaitAsync(async () => (await Transitions(feed, before)).Count(t => t.State == "Online") >= 2);

            var transitions = await Transitions(feed, before);
            Assert.All(transitions, item => Assert.Equal(peerBId, item.PeerId));
            Assert.Equal(["Paired", "Online", "Offline", "Online"], transitions.Select(item => item.State).Take(4));
            Assert.Equal(transitions.OrderBy(item => item.At).Select(item => item.At), transitions.Select(item => item.At));

            // Repeated reconciliation adds nothing: only transitions are recorded.
            await a.PeerStates.ReconcileAsync();
            Assert.Equal(transitions.Count, (await Transitions(feed, before)).Count);

            // A window with no transitions is empty.
            Assert.Empty(await feed.GetPeerStateHistoryAsync(before.AddDays(-10), before.AddDays(-9)));

            var installs = await feed.GetUpdateHistoryAsync(recorded.AddHours(-1), recorded.AddDays(1).AddHours(1));
            Assert.Equal(["1.2.0", "1.3.0"], installs.Select(item => item.Version));
            Assert.Equal(["Applied", "RolledBack"], installs.Select(item => item.Outcome));
            Assert.Equal("health check failed", installs[1].Detail);

            // Range validation: from after to, and unparsable instants, are refused.
            using var http = new HttpClient { BaseAddress = a.AdminAddress };
            using var inverted = await http.GetAsync(
                "/api/v1/history/peer-states?from=2026-11-21T00:00:00Z&to=2026-11-20T00:00:00Z");
            Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
            using var garbage = await http.GetAsync("/api/v1/history/updates?from=yesterday");
            Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
            using var wire = await http.GetAsync("/api/v1/history/peer-states?from=" + Uri.EscapeDataString(before.ToString("O")));
            wire.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await wire.Content.ReadAsStringAsync());
            Assert.Equal("Paired", document.RootElement[0].GetProperty("state").GetString());
        }
        finally
        {
            DeleteQuietly(rootA);
            DeleteQuietly(rootB);
        }
    }

    private static async Task<IReadOnlyList<PeerStateTransitionDto>> Transitions(LocalApiClient source, DateTimeOffset from) =>
        await source.GetPeerStateHistoryAsync(from, DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var cancellation = new CancellationTokenSource(Deadline);
        while (!await condition())
        {
            await Task.Delay(100, cancellation.Token);
        }
    }

    private sealed class FakeUpdates(IReadOnlyList<LocalUpdateEvent> events) : ILocalUpdateController, ILocalUpdateHistory
    {
        public LocalUpdateStatus GetStatus() => new("manual", "1.0.0", "Idle", null, null, null, null, false);

        public Task<bool> ApproveAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public IReadOnlyList<LocalUpdateEvent> GetUpdateHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
            events.Where(item => item.At >= fromUtc && item.At <= toUtc).ToArray();
    }

    private static async Task PairAsync(PeerNodeRuntime first, PeerNodeRuntime second, int secondPeerPort)
    {
        using var firstClient = CookieClient();
        using var secondClient = CookieClient();
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
        request.Headers.Add("X-CSRF-TOKEN", await CsrfAsync(firstClient, first.AdminAddress!));
        var invitationTask = firstClient.SendAsync(request);

        string? pairingId = null;
        for (var attempt = 0; attempt < 100 && pairingId is null; attempt++)
        {
            await Task.Delay(100);
            using var document = JsonDocument.Parse(
                await secondClient.GetStringAsync(new Uri(second.AdminAddress!, "/api/pairings")));
            if (document.RootElement.GetArrayLength() > 0)
            {
                pairingId = document.RootElement[0].GetProperty("pairingId").GetString();
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(pairingId));
        using var approval = new HttpRequestMessage(
            HttpMethod.Post, new Uri(second.AdminAddress!, $"/api/pairings/{Uri.EscapeDataString(pairingId!)}/approve"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        approval.Headers.Add("X-CSRF-TOKEN", await CsrfAsync(secondClient, second.AdminAddress!));
        using var approved = await secondClient.SendAsync(approval);
        approved.EnsureSuccessStatusCode();
        using var done = await invitationTask.WaitAsync(TimeSpan.FromSeconds(15));
        done.EnsureSuccessStatusCode();
    }

    private static async Task<string> CsrfAsync(HttpClient client, Uri baseAddress)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri(baseAddress, "/api/csrf")));
        return document.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static HttpClient CookieClient() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true });

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
