using Android.Content;
using MonitorApp.Peer.Mobile;

namespace MonitorApp.Android;

/// <summary>
/// One portable runtime per process, shared by the foreground service (which only calls Start/Stop)
/// and the activity (which pairs and reads status).
/// </summary>
internal static class MobileRuntimeHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MobilePeerRuntime? runtime;

    public static async Task<MobilePeerRuntime> GetAsync(Context context)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtime is not null)
            {
                return runtime;
            }

            var appContext = context.ApplicationContext ?? context;
            var directory = Path.Combine(appContext.FilesDir!.AbsolutePath, "peer");
            var displayName = $"{global::Android.OS.Build.Manufacturer} {global::Android.OS.Build.Model}";
            runtime = await MobilePeerRuntime.CreateAsync(
                MobilePeerOptions.Default(directory, displayName),
                new KeystorePeerIdentity("monitor-app-peer-v1"),
                new AndroidDeviceReader(appContext)).ConfigureAwait(false);
            return runtime;
        }
        finally
        {
            Gate.Release();
        }
    }
}
