using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace MonitorApp.Android;

/// <summary>
/// Hosts the portable peer runtime for near-continuous collection and sync. Started explicitly by the user,
/// shows a persistent notification, and is stoppable from the notification or the activity.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[global::Android.App.MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "peer-monitoring-collection-and-sync")]
public sealed class PeerForegroundService : Service
{
    public const string ActionStart = "app.monitor.selfhosted.START";
    public const string ActionStop = "app.monitor.selfhosted.STOP";
    private const string ChannelId = "monitor-peer";
    private const int NotificationId = 1;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(PeerForegroundService)).SetAction(ActionStart));

    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(PeerForegroundService)).SetAction(ActionStop));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            _ = StopRuntimeAsync();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, BuildNotification());
        }
        _ = StartRuntimeAsync();
        // The user starts monitoring explicitly; do not resurrect it silently after a process kill.
        return StartCommandResult.NotSticky;
    }

    private async Task StartRuntimeAsync()
    {
        var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(false);
        runtime.Start();
    }

    private async Task StopRuntimeAsync()
    {
        var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(false);
        await runtime.StopAsync().ConfigureAwait(false);
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Monitoring", NotificationImportance.Low));
        var stop = PendingIntent.GetService(
            this, 0, new Intent(this, typeof(PeerForegroundService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        var open = PendingIntent.GetActivity(
            this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable)!;
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Monitor App is collecting")
            .SetContentText("Collecting device readings and syncing with paired desktops.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetContentIntent(open)
            .AddAction(new Notification.Action.Builder(null, "Stop", stop).Build())
            .Build()!;
    }
}
