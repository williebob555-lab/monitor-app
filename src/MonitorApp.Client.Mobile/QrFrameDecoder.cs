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

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };
        var result = reader.Decode(new PlanarYUVLuminanceSource(luminance, rowStride, height, 0, 0, width, height, false));
        return result?.Text is { } text && IsInvitationText(text) ? text : null;
    }

    /// <summary>True for text that claims to be a Monitor App pairing invitation or a phone/tablet device code (T085).</summary>
    public static bool IsInvitationText(string text) =>
        text.StartsWith(PeerPairingInvitation.Prefix, StringComparison.Ordinal) || MonitorApp.Peer.Replication.MeshFaceToFace.IsMeshCode(text);
}
