using System.Net;
using System.Net.Http.Json;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiAdminPageTests : IDisposable
{
    private const string Hostile = "<img src=x onerror=alert(1)>";
    private static readonly DateTimeOffset Now = LocalApiHostTests.Now;

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-admin-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PageRendersEveryOperatorSectionWithRealDataAndEscapesPeerProvidedStrings()
    {
        var updates = new FakeUpdates();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, updates: updates, withControl: true);

        var alert = new Alert(
            Alert.CreateId("peer-local", "rule.x", "subject-1", Now), 1, "rule.x", "subject-1", "peer-local",
            AlertSeverity.Critical, AlertState.Raised, "Disk " + Hostile, "Detail " + Hostile, Now.AddMinutes(-5), null);
        await fixture.Alerts.RaiseAsync(alert, [], TimeSpan.FromDays(1), Now);
        await fixture.PeerStore.RecordPeerStateAsync("peer-trusted", PeerConnectivityState.Online, Now.AddHours(-2), "reason " + Hostile);
        await fixture.PeerStore.PinIncidentAsync(new PinnedIncident("pin-1", "Outage " + Hostile, Now.AddHours(-3), Now.AddHours(-2), Now));
        await fixture.Control!.SetGrantAsync(new ControlGrant("peer-trusted", true, false, null), CancellationToken.None);
        var page = await fixture.Client.GetStringAsync("/");

        Assert.DoesNotContain(Hostile, page, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page, StringComparison.Ordinal);
        foreach (var heading in new[] { "Alerts", "Alert rules", "Remote control grants", "Recent activity", "Pinned incidents", "Updates" })
        {
            Assert.Contains($">{heading}</h2>", page, StringComparison.Ordinal);
        }

        Assert.Contains($"data-url=\"/api/v1/alerts/{Uri.EscapeDataString(alert.AlertId)}/acknowledge\"", page, StringComparison.Ordinal);
        Assert.Contains("data-url=\"/api/v1/alerts/rules/peer-local\"", page, StringComparison.Ordinal);
        Assert.Contains("data-url=\"/api/v1/alerts/rules/peer-trusted\"", page, StringComparison.Ordinal);
        Assert.Contains("data-rule=\"peer.silent\"", page, StringComparison.Ordinal);
        Assert.Contains("data-url=\"/api/v1/control/grants/peer-trusted\"", page, StringComparison.Ordinal);
        Assert.Contains("data-field=\"monitoringControls\" checked", page, StringComparison.Ordinal);
        Assert.Contains("Trusted workstation", page, StringComparison.Ordinal);
        Assert.Contains("data-url=\"/api/v1/incidents/pin-1/unpin\"", page, StringComparison.Ordinal);
        Assert.Contains("data-url=\"/api/v1/update/approve\"", page, StringComparison.Ordinal);
        Assert.Contains("0.1.2", page, StringComparison.Ordinal);
        Assert.Contains("0.1.0 " + "&lt;b&gt;", page, StringComparison.Ordinal);
        Assert.Contains("RolledBack", page, StringComparison.Ordinal);

        // The page's script posts with the antiforgery header and loads nothing from outside this host.
        Assert.Contains("'X-CSRF-TOKEN'", page, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"http", page, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryOperatorMutationTheSectionsUseIsRejectedWithoutAntiforgeryAndWorksWithIt()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, updates: new FakeUpdates(), withControl: true);
        var alert = new Alert(
            Alert.CreateId("peer-local", "rule.x", "s", Now), 1, "rule.x", "s", "peer-local",
            AlertSeverity.Warning, AlertState.Raised, "T", "D", Now, null);
        await fixture.Alerts.RaiseAsync(alert, [], TimeSpan.FromDays(1), Now);
        var window = new { label = "w", from = Now.AddHours(-2).ToString("O"), to = Now.ToString("O") };
        var rule = new { overrides = new[] { new { ruleId = "peer.silent", enabled = true, threshold = (double?)10 } } };
        var posts = new (string Url, object Body)[]
        {
            ($"api/v1/alerts/{alert.AlertId}/acknowledge", new { }),
            ("api/v1/alerts/rules/peer-trusted", rule),
            ("api/v1/control/grants/peer-trusted", new { monitoringControls = false, pairingApproval = false, updateApproval = false }),
            ("api/v1/incidents/pin", window),
            ("api/v1/update/approve", new { }),
        };

        foreach (var (url, body) in posts)
        {
            using var bare = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            using var rejected = await fixture.Client.SendAsync(bare);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        Assert.Null(await fixture.Alerts.ReadOperatorAcknowledgementAsync(alert.AlertId));
        Assert.Empty(await fixture.PeerStore.ReadPinnedIncidentsAsync());

        var csrf = await fixture.GetCsrfTokenAsync();
        foreach (var (url, body) in posts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using var accepted = await fixture.Client.SendAsync(request);
            Assert.True(accepted.IsSuccessStatusCode, $"{url} returned {accepted.StatusCode}");
        }

        // The editor surfaces the API's validation text instead of guessing at it.
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "api/v1/alerts/rules/peer-trusted")
        {
            Content = JsonContent.Create(new { overrides = new[] { new { ruleId = "peer.silent", enabled = true, threshold = (double?)0 } } }),
        };
        invalid.Headers.Add("X-CSRF-TOKEN", csrf);
        using var refused = await fixture.Client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("error", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeUpdates : ILocalUpdateController, ILocalUpdateHistory
    {
        public LocalUpdateStatus GetStatus() =>
            new("approve", "0.1.1", "Staged", "0.1.2", null, null, null, true);

        public Task<bool> ApproveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IReadOnlyList<LocalUpdateEvent> GetUpdateHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
            [new("0.1.0 <b>", "RolledBack", Now.AddDays(-1), "detail")];
    }
}
