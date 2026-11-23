using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace MonitorApp.Peer.LocalApi.Tests;

public sealed class LocalApiPhoneReachTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-local-api-reach-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReachSwitchNeedsAntiforgeryTokenAndReportsTheChange()
    {
        var reach = new FakeReach();
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory, reach: reach);

        var status = await fixture.Client.GetFromJsonAsync<PeerReachStatus>("api/v1/network/reach");
        Assert.False(status!.ReachableByPhone);

        using var noToken = new HttpRequestMessage(HttpMethod.Post, "api/v1/network/reach") { Content = JsonContent.Create(new { reachableByPhone = true }) };
        using var rejected = await fixture.Client.SendAsync(noToken);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Empty(reach.Changes);

        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/network/reach") { Content = JsonContent.Create(new { reachableByPhone = true }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var accepted = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal([true], reach.Changes);
    }

    [Fact]
    public async Task PhonePairingWithoutAReachableAddressExplainsWhatToDo()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(directory);
        var view = await fixture.Client.GetFromJsonAsync<PhonePairingView>("api/v1/phone-pairing");
        Assert.Null(view!.Invitation);

        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/phone-pairing") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Let my phone reach this computer", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReachableComputerIssuesAnInvitationWithItsQrModules()
    {
        await using var fixture = await LocalApiHostTests.Fixture.CreateAsync(
            directory,
            phonePairing: new PhonePairingOptions([IPAddress.Parse("100.64.0.5")], 55431, new MonitorApp.Peer.Core.PairingShortCodeRegistry()));
        var csrf = await fixture.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/phone-pairing") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await response.Content.ReadFromJsonAsync<PhonePairingView>();
        var invitation = view!.Invitation!;
        Assert.Equal("100.64.0.5", invitation.Address);
        Assert.StartsWith("monitor-pair", invitation.Payload, StringComparison.Ordinal);
        Assert.All(invitation.QrRows, row => Assert.Equal(invitation.QrRows.Count, row.Length));
        Assert.All(invitation.QrRows, row => Assert.True(row.All(c => c is '0' or '1')));
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

    private sealed class FakeReach : IPeerReachController
    {
        public List<bool> Changes { get; } = [];

        public PeerReachStatus GetStatus() => new(false, "127.0.0.1", "100.64.0.9", "default", true, false, "Only this computer can connect.");

        public PeerReachChange Change(bool reachableByPhone)
        {
            Changes.Add(reachableByPhone);
            return new PeerReachChange(true, null, GetStatus() with { RestartPending = true });
        }
    }
}
