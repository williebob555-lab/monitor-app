using MonitorApp.Peer.Transport;
using ZXing;
using ZXing.Common;

namespace MonitorApp.Client.Mobile;

/// <summary>
/// Decodes camera frames to a Monitor App pairing QR. Platform-neutral: the Android scanner feeds it the luminance (Y)
/// plane of each preview frame, so the decode path is the same code the tests exercise. Other QR codes in view
/// (URLs, Wi-Fi codes) are ignored so scanning keeps going until the real invitation appears.
/// </summary>
public static class QrFrameDecoder
{
    private static BarcodeReaderGeneric[] Readers =>
    [
        new() { Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true } },
        new() { Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, PureBarcode = true } },
    ];

    /// <summary>
    /// Tries to read a pairing invitation from one luminance frame. Returns the QR text only when it starts with the
    /// invitation prefix; the text is not otherwise validated here (pairing decodes and validates it).
    /// </summary>
    /// <param name="luminance">Y plane bytes, row-major, <paramref name="rowStride"/> bytes per row (may exceed the width).</param>
    /// <param name="rowStride">Bytes from the start of one row to the start of the next.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    public static string? TryDecodeInvitation(byte[] luminance, int rowStride, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(luminance);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowStride, width);
        if (luminance.Length < (rowStride * (height - 1)) + width)
        {
            throw new ArgumentException("The luminance buffer is smaller than the frame it describes.", nameof(luminance));
        }

        // The detector misreads the finder patterns of a few percent of perfectly rendered codes (measured: 16 in 300
        // random invitations), so a frame it cannot read gets a second pass that treats the frame as an upright code.
        // That pass is what reads a phone held square to the desktop screen, which is how people scan in practice.
        var source = new PlanarYUVLuminanceSource(luminance, rowStride, height, 0, 0, width, height, false);
        foreach (var reader in Readers)
        {
            if (reader.Decode(source)?.Text is { } text)
            {
                return IsInvitationText(text) ? text : null;
            }
        }

        return null;
    }

    /// <summary>True for text that claims to be a Monitor App pairing invitation or a phone/tablet device code (T085).</summary>
    public static bool IsInvitationText(string text) =>
        text.StartsWith(PeerPairingInvitation.Prefix, StringComparison.Ordinal) || MonitorApp.Peer.Replication.MeshFaceToFace.IsMeshCode(text);
}
