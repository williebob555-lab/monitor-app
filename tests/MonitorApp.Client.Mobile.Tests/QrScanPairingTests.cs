using MonitorApp.Client.Core;
using MonitorApp.Client.Mobile;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Mobile.Tests;
using MonitorApp.Peer.Transport;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace MonitorApp.Client.Mobile.Tests;

/// <summary>
/// Camera QR pairing without a camera: frames are rendered from a real QR encoder, decoded by the production
/// <see cref="QrFrameDecoder"/> (the same code the Android scanner runs on each preview frame), and the resulting
/// text goes through the real invitation decode and pairing against a real desktop peer.
/// </summary>
public sealed class QrScanPairingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly PeerPairingInvitation FixedInvitation = new(
        System.Net.IPAddress.Parse("100.64.0.5"), 55433, "desktop-peer-1", new byte[32], null);

    [Fact]
    public void AFrameWithThePairingQrDecodesToTheExactInvitationEvenWithRowPaddingAndNoise()
    {
        var text = FixedInvitation.Encode();
        var frame = Render(text, modulePixels: 5, rowPadding: 24, noise: true);

        var decoded = QrFrameDecoder.TryDecodeInvitation(frame.Luminance, frame.RowStride, frame.Width, frame.Height);

        Assert.Equal(text, decoded);
        Assert.Equal(FixedInvitation.PeerId, PeerPairingInvitation.Decode(decoded!).PeerId);
    }

    [Fact]
    public void EveryInvitationDecodesNotJustMostOfThem()
    {
        // Before the upright-code fallback, about one random invitation in twenty rendered perfectly and still read as nothing.
        for (var i = 0; i < 200; i++)
        {
            var text = new PeerPairingInvitation(
                System.Net.IPAddress.Parse("127.0.0.1"), 40000 + i, Guid.NewGuid().ToString("N"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), null).Encode();
            var frame = Render(text, modulePixels: 4 + (i % 5), rowPadding: 16, noise: false);
            Assert.Equal(text, QrFrameDecoder.TryDecodeInvitation(frame.Luminance, frame.RowStride, frame.Width, frame.Height));
        }
    }

    [Fact]
    public void OtherQrCodesAndEmptyFramesAreIgnoredSoScanningContinues()
    {
        var url = Render("https://example.com/not-monitor-app", modulePixels: 5, rowPadding: 0, noise: false);
        Assert.Null(QrFrameDecoder.TryDecodeInvitation(url.Luminance, url.RowStride, url.Width, url.Height));

        var blank = new byte[320 * 240];
        Array.Fill(blank, (byte)128);
        Assert.Null(QrFrameDecoder.TryDecodeInvitation(blank, 320, 320, 240));

        Assert.Throws<ArgumentException>(() => QrFrameDecoder.TryDecodeInvitation(new byte[10], 320, 320, 240));
    }

    [Fact]
    public async Task ScanningTheDesktopQrPairsThePhoneThroughTheRealRuntime()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());

            var frame = Render(desktop.Invitation().Encode(), modulePixels: 4, rowPadding: 16, noise: true);
            var scanner = new FakeScanner(() => QrFrameDecoder.TryDecodeInvitation(frame.Luminance, frame.RowStride, frame.Width, frame.Height));
            using var source = new InProcessPeerDataSource(phone, qrScanner: scanner);
            Assert.True(source.CanScanQr);

            var pairing = source.ScanAndPairAsync();
            string? pending = null;
            // If the pairing attempt fails before the desktop sees it, surface that failure instead of a bare timeout.
            await WaitAsync(() => (pending = phone.GetStatus().PendingPairingId) is not null || pairing.IsCompleted);
            if (pending is null)
            {
                await pairing;
                Assert.Fail("Pairing completed without a pending request: " + pairing.Status);
            }

            await desktop.ApproveWhenPendingAsync(pending!);
            Assert.Equal(desktop.Identity.PeerId, await pairing.WaitAsync(Deadline));
            Assert.Equal("Active", Assert.Single(await source.GetPeersAsync()).TrustState);
            Assert.Equal(1, scanner.Scans);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task ScannerProblemsBecomeValidationMessagesThatPointAtTheTypedCode()
    {
        var phoneRoot = TestDirectories.Create();
        try
        {
            using var identity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(Options(phoneRoot), identity, new ScriptedDeviceReader());

            // No scanner at all (desktop-style head) and a scanner that reports no camera.
            using var none = new InProcessPeerDataSource(phone);
            Assert.False(none.CanScanQr);
            var missing = await Assert.ThrowsAsync<ApiValidationException>(() => none.ScanAndPairAsync());
            Assert.Contains("code", missing.Message, StringComparison.OrdinalIgnoreCase);
            using var noCamera = new InProcessPeerDataSource(phone, qrScanner: new FakeScanner(() => "ignored") { Available = false });
            Assert.False(noCamera.CanScanQr);
            await Assert.ThrowsAsync<ApiValidationException>(() => noCamera.ScanAndPairAsync());

            // Camera permission denied: the message is kept and the typed code is offered.
            var denied = new FakeScanner(() => throw new QrScanUnavailableException("Camera permission was denied."));
            using var deniedSource = new InProcessPeerDataSource(phone, qrScanner: denied);
            var error = await Assert.ThrowsAsync<ApiValidationException>(() => deniedSource.ScanAndPairAsync());
            Assert.Contains("denied", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("code", error.Message, StringComparison.OrdinalIgnoreCase);

            // The person backs out: no pairing, no error.
            using var cancelled = new InProcessPeerDataSource(phone, qrScanner: new FakeScanner(() => null));
            Assert.Null(await cancelled.ScanAndPairAsync());
            Assert.Empty(await cancelled.GetPeersAsync());

            // A scanned string that is not a valid invitation never reaches the network.
            using var junk = new InProcessPeerDataSource(phone, qrScanner: new FakeScanner(() => PeerPairingInvitation.Prefix + "not-base64!"));
            await Assert.ThrowsAsync<ApiValidationException>(() => junk.ScanAndPairAsync());
            Assert.Null(phone.GetStatus().PendingPairingId);

            // Cancelling the token propagates as cancellation, not as a validation error.
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            using var slow = new InProcessPeerDataSource(phone, qrScanner: new FakeScanner(() => null, honourCancellation: true));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.ScanAndPairAsync(cts.Token));
        }
        finally
        {
            Cleanup(phoneRoot);
        }
    }

    private static MobilePeerOptions Options(string directory) => new(
        directory,
        "test-phone",
        CollectionInterval: TimeSpan.FromHours(1),
        SyncInterval: TimeSpan.FromMilliseconds(50),
        MaximumSyncBackoff: TimeSpan.FromMilliseconds(400),
        HeartbeatInterval: TimeSpan.FromSeconds(5));

    /// <summary>Renders a QR with a quiet zone as a luminance plane, optionally padded per row (camera stride) and noisy.</summary>
    private static Frame Render(string text, int modulePixels, int rowPadding, bool noise)
    {
        var matrix = new QRCodeWriter().encode(
            text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 4 });
        var width = matrix.Width * modulePixels;
        var height = matrix.Height * modulePixels;
        var stride = width + rowPadding;
        var luminance = new byte[stride * height];
        var random = new Random(7);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = matrix[x / modulePixels, y / modulePixels] ? 20 : 235;
                if (noise)
                {
                    value += random.Next(-15, 16);
                }

                luminance[(y * stride) + x] = (byte)value;
            }
        }

        return new Frame(luminance, stride, width, height);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Condition was not met before the deadline.");
            }
        }
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record Frame(byte[] Luminance, int RowStride, int Width, int Height);

    private sealed class FakeScanner(Func<string?> scan, bool honourCancellation = false) : IQrScanner
    {
        public bool Available { get; init; } = true;

        public int Scans { get; private set; }

        public bool IsAvailable => Available;

        public Task<string?> ScanAsync(CancellationToken cancellationToken = default)
        {
            if (honourCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Scans++;
            return Task.FromResult(scan());
        }
    }
}
