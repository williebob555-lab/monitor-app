using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiIncidentTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-incident-tests",
        Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset From = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task PinAndUnpinRequireTheAntiforgeryTokenLikeEveryOtherAdminMutation()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);

        using var pinWithout = new HttpRequestMessage(HttpMethod.Post, "api/v1/incidents/pin") { Content = JsonContent.Create(Window("outage")) };
        using var pinRejected = await fixture.Client.SendAsync(pinWithout);
        using var unpinWithout = new HttpRequestMessage(HttpMethod.Post, "api/v1/incidents/anything/unpin") { Content = JsonContent.Create(new { }) };
        using var unpinRejected = await fixture.Client.SendAsync(unpinWithout);

        Assert.Equal(HttpStatusCode.Forbidden, pinRejected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unpinRejected.StatusCode);
        Assert.Empty(await fixture.PeerStore.ReadPinnedIncidentsAsync());
    }

    [Fact]
    public async Task PinListUnpinRoundTripThroughTheStoreAndUnknownIdsAreNotFound()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var csrf = await fixture.GetCsrfTokenAsync();

        using var pin = Post("api/v1/incidents/pin", csrf, Window("  Router reboot  "));
        using var pinned = await fixture.Client.SendAsync(pin);
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        using var body = JsonDocument.Parse(await pinned.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("incidentId").GetString()!;
        Assert.Equal("Router reboot", body.RootElement.GetProperty("label").GetString());

        var stored = Assert.Single(await fixture.PeerStore.ReadPinnedIncidentsAsync());
        Assert.Equal((id, "Router reboot", From, From.AddHours(2)), (stored.IncidentId, stored.Label, stored.From, stored.To));
        using var listed = JsonDocument.Parse(await fixture.Client.GetStringAsync("api/v1/incidents"));
        Assert.Equal(id, Assert.Single(listed.RootElement.EnumerateArray()).GetProperty("incidentId").GetString());

        using var unpin = Post($"api/v1/incidents/{id}/unpin", csrf, new { });
        using var unpinned = await fixture.Client.SendAsync(unpin);
        Assert.Equal(HttpStatusCode.OK, unpinned.StatusCode);
        Assert.Empty(await fixture.PeerStore.ReadPinnedIncidentsAsync());
        using var again = Post($"api/v1/incidents/{id}/unpin", csrf, new { });
        using var missing = await fixture.Client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("", "2026-11-10T03:00:00Z", "2026-11-10T05:00:00Z")]
    [InlineData("x", "not a time", "2026-11-10T05:00:00Z")]
    [InlineData("x", "2026-11-10T05:00:00Z", "2026-11-10T03:00:00Z")]
    [InlineData("x", "2026-11-01T00:00:00Z", "2026-12-15T00:00:00Z")]
    public async Task InvalidWindowsAreRejectedWithoutPinning(string label, string from, string to)
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var csrf = await fixture.GetCsrfTokenAsync();

        using var request = Post("api/v1/incidents/pin", csrf, new { label, from, to });
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await fixture.PeerStore.ReadPinnedIncidentsAsync());
    }

    [Fact]
    public async Task APinnedWindowSurvivesTheRetentionPassUntilItIsUnpinned()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var local = (await fixture.PeerStore.ReadLocalIdentityAsync())!;
        var csrf = await fixture.GetCsrfTokenAsync();
        var observed = From.AddMinutes(30); // inside the pinned window, past raw retention and the 30-day replication hold
        var sequence = local.NextSequence;
        await fixture.PeerStore.AppendOriginRecordAsync(new OriginRecordWrite(
            local.PeerId, local.OriginStreamId, sequence, "host-a", "adapter", "cpu", [1, 2, 3], new byte[64],
            observed, observed.AddMilliseconds(5), null, RecordProvenance.LocalOrigin, "Valid"));
        var policy = new PeerRetentionPolicy(TimeSpan.FromDays(7), TimeSpan.FromDays(365), TimeSpan.FromDays(7), TimeSpan.FromDays(30));
        var retention = new PeerRetentionService(fixture.PeerStore, new FixedClock(From.AddDays(40)), policy);

        using var pin = Post("api/v1/incidents/pin", csrf, Window("keep this"));
        using var pinned = await fixture.Client.SendAsync(pin);
        using var pinnedBody = JsonDocument.Parse(await pinned.Content.ReadAsStringAsync());
        var id = pinnedBody.RootElement.GetProperty("incidentId").GetString()!;

        var kept = await retention.RunAsync();
        Assert.Equal((0, 1), (kept.RawDeleted, kept.RawRetainedPinned));
        Assert.Single(await fixture.PeerStore.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, sequence, 10));

        using var unpin = Post($"api/v1/incidents/{id}/unpin", csrf, new { });
        (await fixture.Client.SendAsync(unpin)).EnsureSuccessStatusCode();
        var pruned = await retention.RunAsync();
        Assert.Equal(1, pruned.RawDeleted);
        Assert.Empty(await fixture.PeerStore.ReadOriginRangeAsync(local.PeerId, local.OriginStreamId, sequence, 10));
    }

    private static object Window(string label) => new { label, from = From.ToString("O"), to = From.AddHours(2).ToString("O") };

    private static HttpRequestMessage Post(string path, string csrf, object content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(content) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private sealed class FixedClock(DateTimeOffset now) : IPeerClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
