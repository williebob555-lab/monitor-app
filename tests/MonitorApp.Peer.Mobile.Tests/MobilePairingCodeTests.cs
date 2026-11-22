using System.Net;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

public sealed class MobilePairingCodeTests
{
    private static MobilePeerOptions Options(string dir) => new(
        dir, "test-phone", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task TypedCodeAndAddressPairOverRealGrpcOnlyAfterDesktopApprovalAndTheCodeWorksOnce()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var shown = desktop.Codes.Issue(desktop.Identity.PeerId, desktop.Identity.PublicKeyFingerprint, TimeSpan.FromMinutes(10)).Code;
            var typed = shown.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();

            var pairTask = phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, typed, "pairing-code-1");

            // Nothing is trusted yet: the desktop sees a pending request naming the phone and its fingerprint.
            await WaitForPendingAsync(desktop, "pairing-code-1");
            Assert.Null(await desktop.Store.ReadTrustedPeerAsync(phone.Identity.PeerId));
            var pending = await desktop.PairingStore.ReadPairingAsync("pairing-code-1");
            Assert.Equal("test-phone", pending!.DisplayName);
            Assert.Equal(phone.Identity.PublicKeyFingerprint, pending.RemoteIdentity.PublicKeyFingerprint);

            await desktop.ApproveWhenPendingAsync("pairing-code-1");
            Assert.Equal(desktop.Identity.PeerId, await pairTask.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(desktop.Identity.PeerId))?.TrustState);
            Assert.Equal(PeerTrustState.Active, (await desktop.Store.ReadTrustedPeerAsync(phone.Identity.PeerId))?.TrustState);
            Assert.Equal(desktop.Identity.PublicKeyFingerprint,
                (await phone.Store.ReadTrustedPeerAsync(desktop.Identity.PeerId))!.PublicKeyFingerprint);

            var reuse = await Assert.ThrowsAsync<MobilePairingFailedException>(() =>
                phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, shown, "pairing-code-2"));
            Assert.DoesNotContain(desktop.Identity.PeerId, reuse.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task WrongCodeIsRefusedWithoutRevealingTheDesktopOrStartingAPairing()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var real = desktop.Codes.Issue(desktop.Identity.PeerId, desktop.Identity.PublicKeyFingerprint, TimeSpan.FromMinutes(10)).Code;
            var wrong = real[0] == '0' ? "11111-11111" : "00000-00000";

            var failure = await Assert.ThrowsAsync<MobilePairingFailedException>(() =>
                phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, wrong, "pairing-wrong"));
            var malformed = await Assert.ThrowsAsync<MobilePairingFailedException>(() =>
                phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, "nope", "pairing-malformed"));

            Assert.DoesNotContain(desktop.Identity.PeerId, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(desktop.Identity.PublicKeyFingerprint), failure.ToString(), StringComparison.Ordinal);
            Assert.Null(await desktop.PairingStore.ReadPairingAsync("pairing-wrong"));
            Assert.Null(await desktop.PairingStore.ReadPairingAsync("pairing-malformed"));
            Assert.Null(await phone.Store.ReadTrustedPeerAsync(desktop.Identity.PeerId));
            Assert.IsType<MonitorApp.Peer.Transport.PeerAuthenticationException>(failure.InnerException);
            Assert.IsType<FormatException>(malformed.InnerException);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task ExpiredCodeIsRefusedOverTheWire()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            var time = new ManualTime();
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot, time);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var shown = desktop.Codes.Issue(desktop.Identity.PeerId, desktop.Identity.PublicKeyFingerprint, TimeSpan.FromMinutes(10)).Code;
            time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

            await Assert.ThrowsAsync<MobilePairingFailedException>(() =>
                phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, shown, "pairing-expired"));
            Assert.Null(await desktop.PairingStore.ReadPairingAsync("pairing-expired"));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task GuessingIsRateLimitedOverTheWireEvenForTheCorrectCodeAfterwards()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var shown = desktop.Codes.Issue(desktop.Identity.PeerId, desktop.Identity.PublicKeyFingerprint, TimeSpan.FromMinutes(10)).Code;

            for (var attempt = 0; attempt < 8; attempt++)
            {
                await Assert.ThrowsAsync<MobilePairingFailedException>(() => phone.PairWithCodeAsync(
                    IPAddress.Loopback, desktop.Port, $"{attempt + 1:D5}-ZZZZZ", $"pairing-guess-{attempt}"));
            }

            await Assert.ThrowsAsync<MobilePairingFailedException>(() =>
                phone.PairWithCodeAsync(IPAddress.Loopback, desktop.Port, shown, "pairing-after-lockout"));
            Assert.Null(await desktop.PairingStore.ReadPairingAsync("pairing-after-lockout"));
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    private static async Task WaitForPendingAsync(DesktopPeer desktop, string pairingId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await desktop.PairingStore.ReadPairingAsync(pairingId) is { State: PairingApprovalState.PendingApproval })
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Desktop never received the pairing request.");
    }

    private static void Cleanup(params string[] roots)
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
