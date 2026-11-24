using System.Diagnostics;
using System.Runtime.Versioning;
using MonitorApp.Client.Presence;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.LocalApi;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

/// <summary>
/// The desktop client's presence flow against the real loopback host: real LocalApiClient, real
/// SoftwareLocalPresenceProvider and key file, real LocalPresenceService. Only the platform "is the person here"
/// check (Windows Hello / fprintd, unavailable on a build host) is replaced by a stub verifier.
/// </summary>
public sealed class LocalPresenceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "monitor-client-presence-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnrollThenApprovalNeedsVerificationAndAUnenrolledClientIsRefused()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service);
        var verifier = new StubVerifier(LocalPresenceVerification.Verified);
        using var provider = NewProvider(verifier);
        using var client = node.NewClient(provider);

        var before = await client.GetLocalPresenceStatusAsync();
        Assert.False(before.Enrolled);
        Assert.False(before.Enforced);

        var enrolled = await client.EnrollLocalPresenceAsync();
        Assert.True(enrolled.Enrolled);
        Assert.True(enrolled.Enforced);
        Assert.Equal(
            LocalPresenceProtocol.Fingerprint((await provider.GetPublicKeySpkiAsync(default))!),
            enrolled.FingerprintSha256);
        Assert.Equal(["Set up fingerprint verification for Monitor App"], verifier.Reasons);

        // A client with no presence provider cannot approve once the node enforces it; nothing changes.
        using var bare = node.NewClient();
        await Assert.ThrowsAsync<LocalPresenceRequiredException>(() => bare.ApprovePairingAsync(node.PendingPairingId));
        Assert.Equal("PendingApproval", Assert.Single(await client.GetPairingsAsync()).ApprovalState);

        // With the provider, the same call transparently verifies the person and succeeds.
        await client.ApprovePairingAsync(node.PendingPairingId);
        Assert.Equal("Approve a pairing request on this computer", verifier.Reasons[^1]);
        Assert.Equal(2, verifier.Reasons.Count);
        Assert.DoesNotContain(await client.GetPairingsAsync(), pairing => pairing.ApprovalState == "PendingApproval");

        // Removing a device carries its own presence action, so the fingerprint is asked for instead of the node refusing.
        await Assert.ThrowsAsync<LocalPresenceRequiredException>(() => bare.RevokePeerAsync("peer-trusted"));
        await client.RevokePeerAsync("peer-trusted");
        Assert.Equal("Remove a device from all your devices", verifier.Reasons[^1]);
        Assert.Equal("Revoked", (await client.GetPeersAsync()).Single(peer => peer.PeerId == "peer-trusted").TrustState);
    }

    [Fact]
    public async Task CancelledVerificationLeavesThePairingPending()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service);
        var verifier = new StubVerifier(LocalPresenceVerification.Verified);
        using var provider = NewProvider(verifier);
        using var client = node.NewClient(provider);
        await client.EnrollLocalPresenceAsync();

        verifier.Next = LocalPresenceVerification.Cancelled;
        await Assert.ThrowsAsync<LocalPresenceCancelledException>(() => client.ApprovePairingAsync(node.PendingPairingId));
        Assert.Equal("PendingApproval", Assert.Single(await client.GetPairingsAsync()).ApprovalState);

        verifier.Next = LocalPresenceVerification.Unavailable;
        await Assert.ThrowsAsync<LocalPresenceRequiredException>(() => client.ApprovePairingAsync(node.PendingPairingId));
        Assert.Equal("PendingApproval", Assert.Single(await client.GetPairingsAsync()).ApprovalState);
    }

    [Fact]
    public async Task UnenrolledNodeNeedsNoVerificationAndUnavailableProviderCannotEnroll()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service);
        using var unavailable = node.NewClient(new UnavailableLocalPresenceProvider());

        await unavailable.ApprovePairingAsync(node.PendingPairingId);

        await Assert.ThrowsAsync<LocalPresenceRequiredException>(() => unavailable.EnrollLocalPresenceAsync());
        Assert.False((await unavailable.GetLocalPresenceStatusAsync()).Enrolled);
    }

    [Fact]
    public async Task UpdateGrantsAndPhoneEnrollmentApprovalsAreGatedThroughTheClient()
    {
        var update = new StagedUpdate();
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service, updates: update, withControl: true);
        var verifier = new StubVerifier(LocalPresenceVerification.Verified);
        using var provider = NewProvider(verifier);
        using var client = node.NewClient(provider);
        using var bare = node.NewClient();
        await client.EnrollLocalPresenceAsync();

        await Assert.ThrowsAsync<LocalPresenceRequiredException>(() => bare.ApproveUpdateAsync("0.1.2"));
        Assert.Equal(0, update.Approvals);
        await client.ApproveUpdateAsync("0.1.2");
        Assert.Equal(1, update.Approvals);

        await Assert.ThrowsAsync<LocalPresenceRequiredException>(
            () => bare.SetControlGrantAsync("peer-trusted", true, false));
        Assert.Empty(await node.Control!.Store.ReadGrantsAsync());
        await client.SetControlGrantAsync("peer-trusted", true, false);
        var grant = Assert.Single(await node.Control.Store.ReadGrantsAsync());
        Assert.True(grant.MonitoringControls);

        // Lowering rights never needs presence.
        await bare.SetControlGrantAsync("peer-trusted", false, false);
        Assert.False(Assert.Single(await node.Control.Store.ReadGrantsAsync()).AllowsAnything);

        var enrollment = await node.Control.Store.RequestEnrollmentAsync(
            "peer-trusted", System.Security.Cryptography.ECDsa.Create().ExportSubjectPublicKeyInfo(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<LocalPresenceRequiredException>(
            () => bare.ApprovePhonePresenceEnrollmentAsync(enrollment.EnrollmentId));
        await client.ApprovePhonePresenceEnrollmentAsync(enrollment.EnrollmentId);
        Assert.Equal(
            Peer.Control.PresenceEnrollmentState.Active,
            (await node.Control.Store.ReadEnrollmentAsync(enrollment.EnrollmentId))!.State);
    }

    [Fact]
    public async Task RemovalThroughTheClientRestoresUnenforcedBehavior()
    {
        var service = LocalPresenceService.Create(null, LocalPresencePolicy.RequiredWhenEnrolled, new SystemPeerClock());
        await using var node = await NodeFixture.CreateAsync(presence: service);
        using var provider = NewProvider(new StubVerifier(LocalPresenceVerification.Verified));
        using var client = node.NewClient(provider);
        await client.EnrollLocalPresenceAsync();

        var status = await client.RemoveLocalPresenceAsync();

        Assert.False(status.Enrolled);
        using var bare = node.NewClient();
        await bare.ApprovePairingAsync(node.PendingPairingId);
    }

    [Fact]
    public async Task SoftwareProviderKeepsOneStableKeyInAnOwnerOnlyFileAndSignsOnlyAfterVerification()
    {
        var path = Path.Combine(directory, "keys", "presence.pkcs8");
        var verifier = new StubVerifier(LocalPresenceVerification.Verified);
        byte[] spki;
        using (var first = new SoftwareLocalPresenceProvider(verifier, new FileLocalPresenceKeyStore(path), "test"))
        {
            spki = (await first.GetPublicKeySpkiAsync(default))!;
        }

        using var second = new SoftwareLocalPresenceProvider(verifier, new FileLocalPresenceKeyStore(path), "test");
        Assert.Equal(spki, await second.GetPublicKeySpkiAsync(default));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<InvalidOperationException>(() => new FileLocalPresenceKeyStore(path).Load());
        }

        verifier.Next = LocalPresenceVerification.Cancelled;
        var refused = await second.SignAsync([1, 2, 3], "reason", default);
        Assert.Equal(LocalPresenceVerification.Cancelled, refused.Outcome);
        Assert.Null(refused.Signature);
    }

    [Fact]
    [Trait("Category", "Linux")]
    [UnsupportedOSPlatform("windows")]
    public async Task FprintdVerifyMapsToolResultsAndNeverTrustsAnUnwritableOrGroupWritableTool()
    {
        var match = Script("match", "echo 'Using device /net/reactivated/Fprint/Device/0'\necho 'Verify result: verify-match (done)'\nexit 0");
        var noMatch = Script("nomatch", "echo 'Verify result: verify-no-match (done)'\nexit 1");
        var noDevices = Script("nodev", "echo 'No devices available' >&2\nexit 1");
        var matchTextButFailed = Script("liar", "echo 'Verify result: verify-match (done)'\nexit 1");

        Assert.Equal(LocalPresenceVerification.Verified, await new FprintdUserPresence(match).VerifyAsync("r", default));
        Assert.Equal(LocalPresenceVerification.Cancelled, await new FprintdUserPresence(noMatch).VerifyAsync("r", default));
        Assert.Equal(LocalPresenceVerification.Unavailable, await new FprintdUserPresence(noDevices).VerifyAsync("r", default));
        Assert.Equal(LocalPresenceVerification.Cancelled, await new FprintdUserPresence(matchTextButFailed).VerifyAsync("r", default));

        var writable = Script("writable", "exit 0");
        File.SetUnixFileMode(writable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        Assert.False(new FprintdUserPresence(writable).IsAvailable);
        Assert.False(new FprintdUserPresence(Path.Combine(directory, "missing")).IsAvailable);
        Assert.Equal(LocalPresenceVerification.Unavailable, await new FprintdUserPresence(Path.Combine(directory, "missing")).VerifyAsync("r", default));
    }

    [Fact]
    [Trait("Category", "Linux")]
    [UnsupportedOSPlatform("windows")]
    public async Task FprintdVerifyTimeoutKillsTheToolAndCancels()
    {
        var marker = Path.Combine(directory, "still-running");
        var slow = Script("slow", $"touch '{marker}'\nexec sleep 30");
        var presence = new FprintdUserPresence(slow, TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        var result = await presence.VerifyAsync("r", default);

        Assert.Equal(LocalPresenceVerification.Cancelled, result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(marker));
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

    private SoftwareLocalPresenceProvider NewProvider(StubVerifier verifier) =>
        new(verifier, new FileLocalPresenceKeyStore(Path.Combine(directory, Guid.NewGuid().ToString("N"), "key")), "test");

    [UnsupportedOSPlatform("windows")]
    private string Script(string name, string body)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Stands in only for the platform biometric prompt, which a build host cannot show.</summary>
    private sealed class StubVerifier(LocalPresenceVerification next) : ILocalUserPresence
    {
        public LocalPresenceVerification Next { get; set; } = next;

        public List<string> Reasons { get; } = [];

        public bool IsAvailable => Next != LocalPresenceVerification.Unavailable || Reasons.Count == 0;

        public Task<LocalPresenceVerification> VerifyAsync(string reason, CancellationToken cancellationToken)
        {
            Reasons.Add(reason);
            return Task.FromResult(Next);
        }
    }

    private sealed class StagedUpdate : ILocalUpdateController
    {
        public int Approvals { get; private set; }

        public LocalUpdateStatus GetStatus() => new("approve", "0.1.1", "Staged", "0.1.2", null, null, null, true);

        public Task<bool> ApproveAsync(CancellationToken cancellationToken)
        {
            Approvals++;
            return Task.FromResult(true);
        }
    }
}
