using System.Net;
using System.Net.Sockets;
using System.Text;
using MonitorApp.Client.Core;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

public sealed class LocalApiClientTests
{
    private static readonly DateTimeOffset Now = NodeFixture.Now;

    [Fact]
    public async Task RuntimePeersPairingsCatalogStateAndHistoryRoundTrip()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddMinutes(-10), 0.25);
        await node.AddNumberAsync(Now.AddMinutes(-2), 0.75);
        await node.AddNumberAsync(Now.AddMinutes(-1), 1024, "host-b", "memory.used");
        using var client = node.NewClient();

        var runtime = await client.GetRuntimeAsync();
        var peers = await client.GetPeersAsync();
        var pairings = await client.GetPairingsAsync();
        var catalog = await client.GetCatalogAsync();
        var state = await client.GetStateAsync();
        var history = await client.GetHistoryAsync(new HistoryQuery(Now.AddHours(-1), Now, "host-a", "cpu.load"));

        Assert.Equal("peer-local", runtime.LocalPeer!.PeerId);
        Assert.Equal(node.LocalFingerprint, runtime.LocalPeer.FingerprintSha256);
        Assert.Contains(peers, peer => peer.PeerId == "peer-trusted" && peer.TrustState == "Active");
        Assert.Equal("peer-pending", Assert.Single(pairings).RemotePeerId);
        Assert.Equal(["host-a", "host-b"], catalog.Subjects.Select(subject => subject.SubjectEntityId));
        Assert.Equal(["cpu.load"], catalog.Subjects[0].Signals.Select(signal => signal.SignalId));
        Assert.Equal(2, state.Points.Count);
        Assert.Equal(0.75, state.Points.Single(point => point.SubjectEntityId == "host-a").Value!.Number);
        Assert.Equal([0.25, 0.75], history.Points.Select(point => point.Value!.Number!.Value));
        Assert.False(history.MoreAvailable);
    }

    [Fact]
    public async Task HistoryFollowsCursorAcrossPagesWithoutLossOrDuplicates()
    {
        await using var node = await NodeFixture.CreateAsync();
        for (var index = 0; index < 5; index++)
        {
            await node.AddNumberAsync(Now.AddMinutes(-30 + index), index);
        }

        using var client = node.NewClient();
        var result = await client.GetHistoryAsync(new HistoryQuery(Now.AddHours(-1), Now), pageSize: 2);

        Assert.Equal(3, result.PagesFetched);
        Assert.False(result.MoreAvailable);
        Assert.Equal([0.0, 1, 2, 3, 4], result.Points.Select(point => point.Value!.Number!.Value));
        Assert.Equal(5, result.Points.Select(point => point.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task HistoryPagingStopsAtTheCapAndSaysMoreExists()
    {
        await using var node = await NodeFixture.CreateAsync();
        for (var index = 0; index < 5; index++)
        {
            await node.AddNumberAsync(Now.AddMinutes(-30 + index), index);
        }

        using var client = node.NewClient();
        var result = await client.GetHistoryAsync(new HistoryQuery(Now.AddHours(-1), Now), maxPages: 2, pageSize: 2);

        Assert.Equal(2, result.PagesFetched);
        Assert.True(result.MoreAvailable);
        Assert.Equal(4, result.Points.Count);
    }

    [Fact]
    public async Task NodeValidationErrorsAreSurfacedWithTheNodesMessage()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var client = node.NewClient();

        var exception = await Assert.ThrowsAsync<ApiValidationException>(
            () => client.GetHistoryAsync(new HistoryQuery(Now, Now.AddHours(-1))));
        var window = await Assert.ThrowsAsync<ApiValidationException>(
            () => client.GetHistoryAsync(new HistoryQuery(Now.AddDays(-40), Now)));

        Assert.Contains("later than from", exception.Message, StringComparison.Ordinal);
        Assert.Contains("31 days", window.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApprovingAPairingUsesTheAntiforgeryHandshake()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var client = node.NewClient();

        await client.ApprovePairingAsync(node.PendingPairingId);

        Assert.Equal("Approved", Assert.Single(await client.GetPairingsAsync()).ApprovalState);
    }

    [Fact]
    public async Task MutationWithoutAValidAntiforgeryTokenIsRefusedByTheNode()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var raw = new HttpClient { BaseAddress = node.Address };

        using var response = await raw.PostAsync($"api/pairings/{node.PendingPairingId}/approve", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var client = node.NewClient();
        Assert.Single(await client.GetPairingsAsync());
    }

    [Fact]
    public async Task UnreachableNodeIsReportedDistinctlyFromOtherFailures()
    {
        var port = FreePort();
        using var client = LocalApiClient.Create(new Uri($"http://127.0.0.1:{port}/"), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<NodeUnreachableException>(() => client.GetRuntimeAsync());
        await Assert.ThrowsAsync<NodeUnreachableException>(() => client.ApprovePairingAsync("x"));
    }

    [Fact]
    public async Task UnknownResponseShapesAndMissingEndpointsAreReportedAsIncompatible()
    {
        using var garbage = new LocalApiClient(
            new HttpClient(new StubHandler(HttpStatusCode.OK, "<html>not json</html>")) { BaseAddress = new Uri("http://stub/") });
        using var missing = new LocalApiClient(
            new HttpClient(new StubHandler(HttpStatusCode.NotFound, string.Empty)) { BaseAddress = new Uri("http://stub/") });

        await Assert.ThrowsAsync<IncompatibleApiException>(() => garbage.GetRuntimeAsync());
        var notFound = await Assert.ThrowsAsync<IncompatibleApiException>(() => missing.GetCatalogAsync());
        Assert.Contains("older or different version", notFound.Message, StringComparison.Ordinal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
