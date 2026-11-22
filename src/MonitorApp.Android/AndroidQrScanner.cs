using Android.Content;
using Android.Content.PM;
using MonitorApp.Client.Core;

namespace MonitorApp.Android;

/// <summary>
/// Camera QR scanner for the phone: launches <see cref="QrScanActivity"/> (framework camera2, no Google Play Services and no
/// extra AndroidX package, so it works on a sideloaded APK) and completes with the decoded invitation text. The activity asks
/// for the CAMERA permission itself; a denial surfaces as <see cref="QrScanUnavailableException"/> so the UI falls back to the
/// typed code.
/// </summary>
public sealed class AndroidQrScanner : IQrScanner
{
    private readonly Context context;

    public AndroidQrScanner(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
    }

    public bool IsAvailable => context.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraAny) == true;

    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new QrScanUnavailableException("This device has no camera.");
        }

        var scan = QrScanSession.Begin();
        using var registration = cancellationToken.Register(() =>
        {
            QrScanSession.Cancel(scan);
            QrScanActivity.FinishCurrent();
        });
        var intent = new Intent(context, typeof(QrScanActivity));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
        var text = await scan.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }
}

/// <summary>Hands the single in-flight scan between <see cref="AndroidQrScanner"/> and <see cref="QrScanActivity"/>.</summary>
internal static class QrScanSession
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<string?>? current;

    public static TaskCompletionSource<string?> Begin()
    {
        lock (Gate)
        {
            if (current is { Task.IsCompleted: false })
            {
                throw new InvalidOperationException("A QR scan is already in progress.");
            }

            current = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return current;
        }
    }

    /// <summary>Delivers a decoded invitation to the pending scan, if any.</summary>
    public static void Complete(string text) => Snapshot()?.TrySetResult(text);

    /// <summary>The scanner could not run (permission denied, camera failure).</summary>
    public static void Fail(string message) => Snapshot()?.TrySetException(new QrScanUnavailableException(message));

    /// <summary>The person closed the scanner without a result (completes with null).</summary>
    public static void Close() => Snapshot()?.TrySetResult(null);

    /// <summary>The caller's cancellation token fired.</summary>
    public static void Cancel(TaskCompletionSource<string?> scan) => scan.TrySetCanceled();

    private static TaskCompletionSource<string?>? Snapshot()
    {
        lock (Gate)
        {
            return current;
        }
    }
}
