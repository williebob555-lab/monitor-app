using System.Text.Json;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiLiveTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-live-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LiveEndpointStreamsNewlyStoredObservationsAsServerSentEventsWithTheSameSecurityHeaders()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/live");
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        // The connected comment is written only after the endpoint subscribed, so the append below cannot be missed.
        Assert.Equal(": connected", await reader.ReadLineAsync(timeout.Token));

        await fixture.PeerStore.AppendOriginRecordAsync(new OriginRecordWrite(
            "peer-local",
            "origin-local",
            2,
            "entity-one",
            "adapter-one",
            "temperature",
            [9, 9, 9],
            Enumerable.Repeat((byte)0x2b, 64).ToArray(),
            LocalApiHostTests.Now,
            LocalApiHostTests.Now,
            null,
            RecordProvenance.LocalOrigin,
            "Good"));

        string? line;
        string? eventName = null;
        string? data = null;
        while (data is null && (line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }

        Assert.Equal("observation", eventName);
        using var json = JsonDocument.Parse(data!);
        Assert.Equal("peer-local", json.RootElement.GetProperty("originPeerId").GetString());
        Assert.Equal(2UL, json.RootElement.GetProperty("sequence").GetUInt64());
        Assert.Equal("temperature", json.RootElement.GetProperty("signalId").GetString());
        Assert.Equal("LocalOrigin", json.RootElement.GetProperty("provenance").GetString());
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
