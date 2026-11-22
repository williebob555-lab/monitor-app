using System.Net;

namespace MonitorApp.Update.Tests;

public sealed class HttpTransportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PlainHttpSourcesAreRejected()
    {
        using var client = new HttpClient();
        Assert.Throws<UpdateVerificationException>(() => new HttpManifestFetcher(client, new Uri("http://example.test/m")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FetcherReturnsBodyAndRejectsOversizeAsync()
    {
        using var ok = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        Assert.Equal(new byte[] { 1, 2, 3 }, await new HttpManifestFetcher(ok, new Uri("https://example.test/m")).FetchAsync(default));

        using var big = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[UpdateManifestCodec.MaximumEnvelopeSize + 1]),
        }));
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            new HttpManifestFetcher(big, new Uri("https://example.test/m")).FetchAsync(default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloaderEnforcesSignedSizeCapAsync()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }));
        var downloader = new HttpArtifactDownloader(client);
        using var sink = new MemoryStream();
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            downloader.DownloadAsync(new Uri("https://example.test/a"), sink, 10, default));
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            downloader.DownloadAsync(new Uri("http://example.test/a"), sink, 1000, default));
    }
}
