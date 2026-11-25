using System.Reflection;
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
    private static readonly object UpdaterLock = new();
    private static MobileUpdater? updater;
    private static CancellationTokenSource? updaterLoop;

    /// <summary>
    /// The phone's updater, one per process, checking at start and then hourly while the process lives. Disabled (and saying
    /// why) when the APK carries no packaged update key, which only release builds have.
    /// </summary>
    public static MobileUpdater GetUpdater(Context context)
    {
        lock (UpdaterLock)
        {
            if (updater is not null)
            {
                return updater;
            }

            var appContext = context.ApplicationContext ?? context;
            MobileUpdateTrust? trust = null;
            try
            {
                using var asset = appContext.Assets!.Open("update-key.json");
                using var buffer = new MemoryStream();
                asset.CopyTo(buffer);
                trust = MobileUpdateTrust.TryParse(buffer.ToArray());
            }
            catch (Java.IO.IOException)
            {
                // Development builds ship no update key; the updater reports itself disabled.
            }

            // The release build stamps the release version into the assembly (-p:Version), the same version the APK displays.
            var versionText = typeof(MobileRuntimeHost).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            var installed = MonitorApp.Update.ReleaseVersion.TryParse(versionText.Split('-', '+')[0], out var parsed)
                ? parsed
                : new MonitorApp.Update.ReleaseVersion(0, 0, 0);
            updater = new MobileUpdater(
                Path.Combine(appContext.FilesDir!.AbsolutePath, "update"), installed, trust, new AndroidApkInstaller(appContext));
            updaterLoop = new CancellationTokenSource();
            var loop = updaterLoop.Token;
            _ = Task.Run(() => updater.RunAsync(null, loop), loop);
            return updater;
        }
    }

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
            MonitorApp.Peer.Application.DiagnosticLog.Configure(Path.Combine(appContext.FilesDir!.AbsolutePath, "logs"));
            var manufacturer = global::Android.OS.Build.Manufacturer;
            var model = global::Android.OS.Build.Model;
            var displayName = $"{manufacturer} {model}";
            // SQLite work completes synchronously on the calling thread, and callers are the activity and the
            // service's main thread. Opening the store can take seconds (the one-time schema v7 backfill reads all
            // stored readings), so it runs on the thread pool instead of freezing the screen or the service start.
            runtime = await Task.Run(() => MobilePeerRuntime.CreateAsync(
                MobilePeerOptions.Default(directory, displayName),
                new KeystorePeerIdentity("monitor-app-peer-v1"),
                new AndroidDeviceReader(appContext),
                new AndroidKeystoreUserPresenceSigner(appContext, "monitor-app-presence-v1"))).ConfigureAwait(false);
            return runtime;
        }
        finally
        {
            Gate.Release();
        }
    }
}
