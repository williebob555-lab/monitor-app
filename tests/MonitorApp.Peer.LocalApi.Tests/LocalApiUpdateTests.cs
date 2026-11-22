using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiUpdateTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "monitor-local-api-update-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StatusIsReadableAndApproveRequiresAntiforgeryToken()
    {
        var controller = new FakeController(pending: true);
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, updates: controller);

        var status = await fixture.Client.GetFromJsonAsync<LocalUpdateStatus>("api/v1/update");
        Assert.Equal("approve", status!.Policy);
        Assert.True(status.ApprovalPending);

        using var noToken = new HttpRequestMessage(HttpMethod.Post, "api/v1/update/approve") { Content = JsonContent.Create(new { }) };
        using var rejected = await fixture.Client.SendAsync(noToken);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(0, controller.Approvals);

        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/update/approve") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var accepted = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, controller.Approvals);
    }

    [Fact]
    public async Task ApproveWithNothingStagedIsAConflictAndMissingControllerIsNotFound()
    {
        await using (var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, updates: new FakeController(pending: false)))
        {
            var csrf = await fixture.GetCsrfTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/update/approve") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await using var bare = await LocalApiHostTests.Fixture.CreateAsync(Path.Combine(directory, "bare"));
        using var missing = await bare.Client.GetAsync("api/v1/update");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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

    private sealed class FakeController(bool pending) : ILocalUpdateController
    {
        public int Approvals { get; private set; }

        public LocalUpdateStatus GetStatus() =>
            new("approve", "0.1.1", pending ? "Staged" : "Idle", pending ? "0.1.2" : null, null, null, null, pending);

        public Task<bool> ApproveAsync(CancellationToken cancellationToken)
        {
            if (!pending)
            {
                return Task.FromResult(false);
            }

            Approvals++;
            return Task.FromResult(true);
        }
    }
}
