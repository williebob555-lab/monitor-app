using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using MonitorApp.Client.Mobile;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Mobile.Tests;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Client.Mobile.Tests;

/// <summary>
/// "Add a phone or tablet" (T085) through the real screens: each device draws its code as a QR, the other device's
/// "camera" decodes exactly that drawing with the production frame decoder, and both owners confirm with a fingerprint.
/// </summary>
public sealed class MeshJoinScreenTests
{
    [Fact]
    public async Task APhoneAndATabletAddEachOtherByScanningEachOthersScreens()
    {
        var roots = new[] { TestDirectories.Create(), TestDirectories.Create() };
        try
        {
            using var phoneIdentity = new SoftwareIdentity();
            using var phonePresence = new SoftwarePresenceSigner();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(roots[0], "Phone"), phoneIdentity, new ScriptedDeviceReader(), phonePresence);
            using var tabletIdentity = new SoftwareIdentity();
            using var tabletPresence = new SoftwarePresenceSigner();
            await using var tablet = await MobilePeerRuntime.CreateAsync(Options(roots[1], "Tablet"), tabletIdentity, new ScriptedDeviceReader(), tabletPresence);

            PairDeviceViewModel? phoneScreen = null;
            PairDeviceViewModel? tabletScreen = null;
            // The connections own (and dispose) the sources.
            var phoneSource = new InProcessPeerDataSource(phone, qrScanner: new ScreenScanner(() => tabletScreen!.ShownQrRows));
            var tabletSource = new InProcessPeerDataSource(tablet, qrScanner: new ScreenScanner(() => phoneScreen!.ShownQrRows));
            using var phoneConnection = new NodeConnection(phoneSource);
            using var tabletConnection = new NodeConnection(tabletSource);
            phoneScreen = new PairDeviceViewModel(phoneConnection);
            tabletScreen = new PairDeviceViewModel(tabletConnection);
            Assert.True(phoneScreen.CanAddMeshDevices);

            // The tablet shows its code; the phone scans it and is asked to compare the tablet's check code.
            await tabletScreen.ShowJoinCodeCommand.ExecuteAsync(null);
            Assert.True(tabletScreen.HasShownCode, tabletScreen.ErrorMessage);
            await phoneScreen.ScanDeviceCodeCommand.ExecuteAsync(null);
            Assert.True(phoneScreen.HasPendingDevice, phoneScreen.ErrorMessage);
            Assert.True(phoneScreen.PendingDevice!.IsJoinRequest);
            Assert.Equal("Tablet", phoneScreen.PendingDevice.DeviceName);
            Assert.Contains(tablet.VerificationCode, phoneScreen.PendingDevicePrompt, StringComparison.Ordinal);

            // Fingerprint declined: nothing is added and the code stays pending.
            phonePresence.Deny = true;
            await phoneScreen.ConfirmPendingDeviceCommand.ExecuteAsync(null);
            Assert.NotNull(phoneScreen.ErrorMessage);
            Assert.Null(await phone.Store.ReadTrustedPeerAsync(tablet.Identity.PeerId));
            phonePresence.Deny = false;

            await phoneScreen.ConfirmPendingDeviceCommand.ExecuteAsync(null);
            Assert.Null(phoneScreen.ErrorMessage);
            Assert.True(phoneScreen.HasShownCode);
            Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(tablet.Identity.PeerId))?.TrustState);

            // The tablet scans the phone's reply, checks the phone's code and joins.
            await tabletScreen.ScanDeviceCodeCommand.ExecuteAsync(null);
            Assert.True(tabletScreen.HasPendingDevice, tabletScreen.ErrorMessage);
            Assert.False(tabletScreen.PendingDevice!.IsJoinRequest);
            Assert.Contains(phone.VerificationCode, tabletScreen.PendingDevicePrompt, StringComparison.Ordinal);
            var paired = false;
            tabletScreen.Paired += (_, _) => paired = true;
            await tabletScreen.ConfirmPendingDeviceCommand.ExecuteAsync(null);
            Assert.Null(tabletScreen.ErrorMessage);
            Assert.True(paired);
            Assert.Contains("Joined", tabletScreen.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(PeerTrustState.Active, (await tablet.Store.ReadTrustedPeerAsync(phone.Identity.PeerId))?.TrustState);
            Assert.Equal(1, tabletPresence.Prompts);
        }
        finally
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
    }

    [Fact]
    public async Task AComputerPairingCodeAndADeviceCodeAreNotMixedUp()
    {
        var root = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(root, "Phone"), identity, new ScriptedDeviceReader());
            var invitation = new MonitorApp.Peer.Transport.PeerPairingInvitation(System.Net.IPAddress.Parse("100.64.0.5"), 55433, "desk", new byte[32], null).Encode();
            using var scansInvitation = new InProcessPeerDataSource(phone, qrScanner: new ScreenScanner(() => InProcessPeerDataSource.QrRows(invitation)));
            await Assert.ThrowsAsync<ApiValidationException>(() => scansInvitation.ScanMeshCodeAsync());

            var joinCode = phone.CreateMeshJoinCode();
            using var scansDeviceCode = new InProcessPeerDataSource(phone, qrScanner: new ScreenScanner(() => InProcessPeerDataSource.QrRows(joinCode)));
            var error = await Assert.ThrowsAsync<ApiValidationException>(() => scansDeviceCode.ScanAndPairAsync());
            Assert.Contains("phone or tablet", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MobilePeerOptions Options(string directory, string name) => new(
        directory, name, CollectionInterval: TimeSpan.FromHours(1), SyncInterval: TimeSpan.FromSeconds(1),
        MaximumSyncBackoff: TimeSpan.FromSeconds(2), HeartbeatInterval: TimeSpan.FromSeconds(5));

    /// <summary>A camera pointed at the other screen: renders its QR rows to pixels and decodes them like a preview frame.</summary>
    private sealed class ScreenScanner(Func<IReadOnlyList<string>> screen) : IQrScanner
    {
        public bool IsAvailable => true;

        public Task<string?> ScanAsync(CancellationToken cancellationToken = default)
        {
            var rows = screen();
            const int pixels = 4;
            var size = rows.Count * pixels;
            var luminance = new byte[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    luminance[(y * size) + x] = rows[y / pixels][x / pixels] == '1' ? (byte)20 : (byte)235;
                }
            }

            return Task.FromResult(QrFrameDecoder.TryDecodeInvitation(luminance, size, size, size));
        }
    }
}
