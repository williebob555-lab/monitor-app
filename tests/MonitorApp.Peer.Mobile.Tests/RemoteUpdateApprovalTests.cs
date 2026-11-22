using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Node.Update;
using MonitorApp.Update;
using MonitorApp.Update.Apply;
using MonitorApp.Peer.Replication;
using MonitorApp.Peer.Sync;
using MonitorApp.Peer.Transport;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

/// <summary>
/// A phone sees the update a desktop has staged and approves its installation over the real authenticated loopback
/// PeerSession gRPC stream. The desktop side is the real ControlService over a real NodeUpdateAgent (real update state
/// store, manifest verification and staging); only the manifest fetch, the artifact download and the OS-level applier
/// are faked, and the biometric prompt is scripted.
/// </summary>
public sealed class RemoteUpdateApprovalTests
{
    private static readonly byte[] Package = "node-package-0.1.2"u8.ToArray();

    private static MobilePeerOptions Options(string dir) => new(
        dir, "update-phone", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

    private sealed class Rig(string root, DesktopPeer desktop, MobilePeerRuntime phone, SoftwareIdentity identity, SoftwarePresenceSigner presence,
        NodeUpdateAgent agent, CountingApplier applier, ECDsa signing) : IAsyncDisposable
    {
        public DesktopPeer Desktop => desktop;

        public MobilePeerRuntime Phone => phone;

        public SoftwarePresenceSigner Presence => presence;

        public SoftwareIdentity Identity => identity;

        public NodeUpdateAgent Agent => agent;

        public CountingApplier Applier => applier;

        public string PhoneId => phone.Identity.PeerId;

        public string DesktopId => desktop.Identity.PeerId;

        public async ValueTask DisposeAsync()
        {
            await phone.DisposeAsync();
            await desktop.DisposeAsync();
            agent.Dispose();
            identity.Dispose();
            presence.Dispose();
            signing.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        public Task GrantAsync(bool update, bool monitoring = false) =>
            desktop.ControlStore.SetGrantAsync(
                new ControlGrant(PhoneId, monitoring, PairingApproval: false, null, UpdateApproval: update), DateTimeOffset.UtcNow);

        public async Task EnrollPresenceAsync()
        {
            var enrolled = await phone.SendControlAsync(DesktopId, ControlRequest.EnrollPresenceKey());
            Assert.Equal(ControlStatus.PendingLocalApproval, enrolled.Status);
            var enrollment = Assert.Single(
                await desktop.ControlStore.ReadEnrollmentsAsync(),
                item => item.PeerId == PhoneId && item.State == PresenceEnrollmentState.Pending);
            Assert.NotNull(await desktop.Control.DecideEnrollmentAsync(enrollment.EnrollmentId, approve: true, default));
        }
    }

    private static async Task<Rig> StartAsync(bool stage = true)
    {
        var root = TestDirectories.Create();
        var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var applier = new CountingApplier();
        var options = new NodeUpdateOptions(
            NodeUpdatePolicy.Approve, "owner/repo", new Uri("https://api.github.com/"), "preview", TimeSpan.FromHours(6),
            Path.Combine(root, "update"), Path.Combine(root, "update-key.json"), "deb", ReleaseVersion.Parse("0.1.1"),
            TimeSpan.FromSeconds(1));
        var agent = new NodeUpdateAgent(
            options,
            new TrustedUpdateKey("preview-key", signing.ExportSubjectPublicKeyInfo()),
            new FakeFetcher(Envelope(signing, "0.1.2", Package)),
            new FakeDownloader(Package),
            applier,
            NullLogger<NodeUpdateAgent>.Instance);
        if (stage)
        {
            await agent.RunCycleAsync(default);
            Assert.Equal("Staged", agent.GetStatus().Stage);
        }

        var desktop = await DesktopPeer.StartAsync(Path.Combine(root, "desktop"), updates: new RemoteUpdateGateway(agent));
        var identity = new SoftwareIdentity();
        var presence = new SoftwarePresenceSigner();
        var phone = await MobilePeerRuntime.CreateAsync(Options(Path.Combine(root, "phone")), identity, new ScriptedDeviceReader(), presence);
        var pairing = phone.PairAsync(desktop.Invitation(), "update-pairing");
        await desktop.ApproveWhenPendingAsync("update-pairing");
        await pairing.WaitAsync(TimeSpan.FromSeconds(30));
        return new Rig(root, desktop, phone, identity, presence, agent, applier, signing);
    }

    private static byte[] Envelope(ECDsa signer, string version, byte[] artifact) =>
        UpdateManifestCodec.CreateSignedEnvelope(
            new UpdateManifest(
                UpdateManifestCodec.CurrentSchemaVersion,
                "preview",
                ReleaseVersion.Parse(version),
                ReleaseVersion.Parse("0.1.0"),
                new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero),
                [
                    new UpdateArtifact(
                        "node", "ubuntu", "x64",
                        new Uri($"https://github.com/o/r/releases/download/preview-{version}/monitorapp-node-ubuntu-x64-{version}.deb"),
                        artifact.Length,
                        Convert.ToHexStringLower(SHA256.HashData(artifact))),
                ]),
            "preview-key",
            signer);

    [Fact]
    public async Task WithoutTheGrantTheStatusIsHiddenAndApprovalIsRefusedWithoutApplying()
    {
        await using var rig = await StartAsync();
        var digest = SHA256.HashData(Package);

        var status = await rig.Phone.ReadUpdateStatusAsync(rig.DesktopId);
        Assert.False(status.Permitted);
        Assert.Empty(status.StagedVersion);
        Assert.Empty(status.InstalledVersion);

        // Even a phone that knows the exact version and digest, holds monitoring control, and has biometrics enrolled is refused.
        await rig.GrantAsync(update: false, monitoring: true);
        await rig.EnrollPresenceAsync();
        var refused = await rig.Phone.SendControlAsync(rig.DesktopId, ControlRequest.ApproveUpdate("0.1.2", digest));
        Assert.Equal(ControlStatus.Refused, refused.Status);
        Assert.Equal("not_permitted", refused.ReasonCode);
        await rig.Agent.WhenIdleAsync();
        Assert.Equal(0, rig.Applier.Calls);
        Assert.Equal("Staged", rig.Agent.GetStatus().Stage);

        var audit = await rig.Desktop.ControlStore.ReadAuditAsync(100);
        Assert.Contains(audit, entry => entry is { Action: "approve_update", Outcome: "refused", ReasonCode: "not_permitted", Tier: "High" });
        Assert.Contains(audit, entry => entry is { Action: "read_update_status", Outcome: "refused", ReasonCode: "not_permitted" });
    }

    [Fact]
    public async Task WithTheGrantButNoPresenceSignatureApprovalIsRefused()
    {
        await using var rig = await StartAsync();
        await rig.GrantAsync(update: true);
        var status = await rig.Phone.ReadUpdateStatusAsync(rig.DesktopId);
        Assert.True(status.Permitted);

        // No biometric key enrolled on the desktop yet.
        var notEnrolled = await rig.Phone.ApproveUpdateAsync(rig.DesktopId, status);
        Assert.Equal("presence_not_enrolled", notEnrolled.ReasonCode);

        await rig.EnrollPresenceAsync();

        // Enrolled, but the command arrives without the biometric signature (a compromised app without the key).
        var stripped = await SendRawAsync(rig, status, async command =>
        {
            command.PresenceSignature = ByteString.Empty;
            await Task.CompletedTask;
        });
        Assert.Equal("presence_required", stripped.ReasonCode);

        // Signed by some other key, not the enrolled one.
        using var otherKey = new SoftwarePresenceSigner();
        var wrongKey = await SendRawAsync(rig, status, async command =>
        {
            command.PresenceSignature = ByteString.CopyFrom(await otherKey.SignAsync(
                ControlCanonical.PresenceSigningInput(command), new UserPresenceChallenge("t", "m"), default));
        });
        Assert.Equal("presence_invalid", wrongKey.ReasonCode);

        // The person declining the prompt sends nothing at all.
        rig.Presence.Deny = true;
        await Assert.ThrowsAsync<UserPresenceDeniedException>(() => rig.Phone.ApproveUpdateAsync(rig.DesktopId, status));
        rig.Presence.Deny = false;

        await rig.Agent.WhenIdleAsync();
        Assert.Equal(0, rig.Applier.Calls);
        Assert.Equal("Staged", rig.Agent.GetStatus().Stage);
    }

    [Fact]
    public async Task ApprovalWithPresenceAppliesExactlyOnceAndAReplayIsIdempotent()
    {
        await using var rig = await StartAsync();
        await rig.GrantAsync(update: true);
        await rig.EnrollPresenceAsync();

        var status = await rig.Phone.ReadUpdateStatusAsync(rig.DesktopId);
        Assert.True(status.Permitted);
        Assert.True(status.Available);
        Assert.True(status.ApprovalPending);
        Assert.Equal("0.1.1", status.InstalledVersion);
        Assert.Equal("0.1.2", status.StagedVersion);
        Assert.Equal("Staged", status.State);
        Assert.Equal(SHA256.HashData(Package), status.StagedArtifactSha256.ToByteArray());
        Assert.Empty(status.LastError);

        var prompts = rig.Presence.Prompts;
        var result = await rig.Phone.ApproveUpdateAsync(rig.DesktopId, status, commandId: "approve-update-1");
        Assert.Equal(ControlStatus.Applied, result.Status);
        Assert.Equal("applied", result.ReasonCode);
        Assert.False(result.Replayed);
        Assert.Equal(prompts + 1, rig.Presence.Prompts);
        await rig.Agent.WhenIdleAsync();
        Assert.Equal(1, rig.Applier.Calls);
        Assert.Equal("Applied", rig.Agent.GetStatus().Stage);

        // A resend of the same command (lost response) returns the stored result and does not apply again.
        var replay = await rig.Phone.ApproveUpdateAsync(rig.DesktopId, status, commandId: "approve-update-1");
        Assert.Equal(ControlStatus.Applied, replay.Status);
        Assert.True(replay.Replayed);

        // A fresh command for the same, now-installed update finds nothing staged.
        var again = await rig.Phone.ApproveUpdateAsync(rig.DesktopId, status);
        Assert.Equal("no_staged_update", again.ReasonCode);
        await rig.Agent.WhenIdleAsync();
        Assert.Equal(1, rig.Applier.Calls);

        var audit = await rig.Desktop.ControlStore.ReadAuditAsync(100);
        Assert.Contains(audit, entry => entry is { Action: "approve_update", Outcome: "applied", Tier: "High", PresenceVerified: true } &&
            entry.Detail.Contains("version=0.1.2", StringComparison.Ordinal) &&
            entry.Detail.Contains(Convert.ToHexStringLower(SHA256.HashData(Package)), StringComparison.Ordinal));
        Assert.Contains(audit, entry => entry is { Action: "approve_update", Outcome: "replayed" });
        Assert.Contains(audit, entry => entry is { Action: "read_update_status", Outcome: "applied" });
    }

    [Fact]
    public async Task ApprovalBoundToADifferentDigestOrVersionIsRefusedAndNothingIsApplied()
    {
        await using var rig = await StartAsync();
        await rig.GrantAsync(update: true);
        await rig.EnrollPresenceAsync();
        var status = await rig.Phone.ReadUpdateStatusAsync(rig.DesktopId);

        // The phone read one artifact, but a different digest (a swapped artifact) is what it signs.
        var otherDigest = SHA256.HashData("a different artifact"u8);
        var digestMismatch = await rig.Phone.SendControlAsync(rig.DesktopId, ControlRequest.ApproveUpdate("0.1.2", otherDigest));
        Assert.Equal(ControlStatus.Refused, digestMismatch.Status);
        Assert.Equal("staged_update_changed", digestMismatch.ReasonCode);

        var versionMismatch = await rig.Phone.SendControlAsync(
            rig.DesktopId, ControlRequest.ApproveUpdate("0.1.3", status.StagedArtifactSha256.ToByteArray()));
        Assert.Equal("staged_update_changed", versionMismatch.ReasonCode);

        // Malformed digests never reach the update agent.
        var malformed = await rig.Phone.SendControlAsync(rig.DesktopId, ControlRequest.ApproveUpdate("0.1.2", [1, 2, 3]));
        Assert.Equal("invalid_payload", malformed.ReasonCode);

        await rig.Agent.WhenIdleAsync();
        Assert.Equal(0, rig.Applier.Calls);
        Assert.Equal("Staged", rig.Agent.GetStatus().Stage);

        // The exact version and digest still work afterwards.
        var good = await rig.Phone.ApproveUpdateAsync(rig.DesktopId, status);
        Assert.Equal(ControlStatus.Applied, good.Status);
        await rig.Agent.WhenIdleAsync();
        Assert.Equal(1, rig.Applier.Calls);
    }

    [Fact]
    public async Task NothingStagedMeansNothingToApproveAndTheStatusSaysSo()
    {
        await using var rig = await StartAsync(stage: false);
        await rig.GrantAsync(update: true);
        await rig.EnrollPresenceAsync();

        var status = await rig.Phone.ReadUpdateStatusAsync(rig.DesktopId);
        Assert.True(status.Permitted);
        Assert.Equal("0.1.1", status.InstalledVersion);
        Assert.Empty(status.StagedVersion);
        Assert.False(status.ApprovalPending);
        Assert.Throws<MobileControlFailedException>(() => { _ = rig.Phone.ApproveUpdateAsync(rig.DesktopId, status); });

        var forced = await rig.Phone.SendControlAsync(rig.DesktopId, ControlRequest.ApproveUpdate("0.1.2", SHA256.HashData(Package)));
        Assert.Equal("no_staged_update", forced.ReasonCode);
        Assert.Equal(0, rig.Applier.Calls);
    }

    /// <summary>Sends a hand-built approval whose presence signature is replaced; the identity signature is valid.</summary>
    private static async Task<ControlResult> SendRawAsync(
        Rig rig, UpdateStatusResponse status, Func<ControlCommand, Task> tamper)
    {
        var endpoint = rig.Desktop.Invitation().ToEndpoint();
        await using var connector = new PeerGrpcConnector(
            PeerTransportOptions.Create(endpoint),
            new P256PeerSessionAuthenticationProvider(rig.Identity.PublicIdentity, rig.Desktop.Identity, rig.Identity.IdentityKey),
            new PeerSyncApplication(rig.Phone.Store, new OriginReplicationCoordinator(rig.Phone.Store)));
        return await connector.SendControlAsync(async (session, token) =>
        {
            var signer = new ControlCommandSigner(rig.Identity.PublicIdentity, rig.Identity.IdentityKey, rig.Presence);
            var command = await signer.CreateAsync(
                rig.DesktopId, session,
                ControlRequest.ApproveUpdate(status.StagedVersion, status.StagedArtifactSha256.ToByteArray()), null, token);
            await tamper(command);
            command.IssuerSignature = ByteString.CopyFrom(rig.Identity.IdentityKey.Sign(ControlCanonical.IdentitySigningInput(command)));
            return command;
        }, default);
    }

    private sealed class FakeFetcher(byte[] envelope) : IManifestFetcher
    {
        public Task<byte[]> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(envelope);
    }

    private sealed class FakeDownloader(byte[] content) : IArtifactDownloader
    {
        public Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken) =>
            destination.WriteAsync(content, cancellationToken).AsTask();
    }

    internal sealed class CountingApplier : IUpdateApplier
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ApplyOutcome(ApplyOutcomeKind.Applied, "applied"));
        }
    }
}
