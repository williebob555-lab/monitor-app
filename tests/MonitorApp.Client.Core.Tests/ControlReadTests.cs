using System.Security.Cryptography;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.LocalApi;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>The desktop UI's read side of remote control, through the real client against the real loopback host.</summary>
public sealed class ControlReadTests
{
    [Fact]
    public async Task GrantsAndPendingEnrollmentsAreReadableAndFollowDecisions()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.Off, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service, withControl: true);
        using var client = node.NewClient();

        // Known peer without a stored grant is default-deny; an unknown peer is unknown.
        Assert.Empty(await client.GetControlGrantsAsync());
        var denied = await client.GetControlGrantAsync("peer-trusted");
        Assert.NotNull(denied);
        Assert.False(denied.AllowsAnything);
        Assert.Null(await client.GetControlGrantAsync("peer-stranger"));

        await client.SetControlGrantAsync("peer-trusted", true, false, updateApproval: true);
        var grant = Assert.Single(await client.GetControlGrantsAsync());
        Assert.Equal("peer-trusted", grant.PeerId);
        Assert.True(grant.MonitoringControls);
        Assert.False(grant.PairingApproval);
        Assert.True(grant.UpdateApproval);
        Assert.NotNull(grant.UpdatedAt);
        Assert.Equal(grant, await client.GetControlGrantAsync("peer-trusted"));

        Assert.Empty(await client.ListPendingPresenceEnrollmentsAsync());
        var spki = ECDsa.Create().ExportSubjectPublicKeyInfo();
        var enrollment = await node.Control!.Store.RequestEnrollmentAsync("peer-trusted", spki, DateTimeOffset.UtcNow);
        var pending = Assert.Single(await client.ListPendingPresenceEnrollmentsAsync());
        Assert.Equal(enrollment.EnrollmentId, pending.EnrollmentId);
        Assert.Equal("peer-trusted", pending.PeerId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(spki)), pending.FingerprintSha256);

        await client.ApprovePhonePresenceEnrollmentAsync(enrollment.EnrollmentId);
        Assert.Empty(await client.ListPendingPresenceEnrollmentsAsync());
    }

    [Fact]
    public async Task AlertPreferencesRoundTripThroughTheLocalApi()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.Off, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service, withControl: true);
        using var client = node.NewClient();

        Assert.Empty(await client.GetAlertPreferencesAsync());
        await client.SetAlertPreferencesAsync("phone-b", "warning", ["threshold.cpu_high", "peer.silent", "peer.silent"]);
        await client.SetAlertPreferencesAsync("phone-a", null, []);
        var all = await client.GetAlertPreferencesAsync();
        Assert.Equal(["phone-a", "phone-b"], all.Select(item => item.PeerId).ToArray());
        Assert.Equal("All", all[0].MinSeverity);
        Assert.Empty(all[0].MutedRuleKinds);
        Assert.Equal("Warning", all[1].MinSeverity);
        Assert.Equal(["peer.silent", "threshold.cpu_high"], all[1].MutedRuleKinds);
        Assert.NotNull(all[1].UpdatedAt);

        await Assert.ThrowsAnyAsync<Exception>(() => client.SetAlertPreferencesAsync("phone-a", "loud", []));
    }
}
