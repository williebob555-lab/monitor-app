namespace MonitorApp.Client.Core;

/// <summary>
/// Platform camera QR scanner (Android supplies one; heads without a camera simply have none). The result is the raw
/// decoded text of the first Monitor App pairing QR in view; the caller validates and uses it.
/// </summary>
public interface IQrScanner
{
    /// <summary>False when this device has no usable camera; the UI should hide the scan button and offer the typed code.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Opens the scanner and completes with the decoded text, or <see langword="null"/> when the person backed out.
    /// Throws <see cref="QrScanUnavailableException"/> when scanning cannot run (no camera, permission denied) and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<string?> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>Scanning could not run (no camera, camera permission denied, camera failure); the message is safe to show the user.</summary>
public sealed class QrScanUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
