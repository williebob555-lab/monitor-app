using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Mobile;

namespace MonitorApp.Android;

/// <summary>
/// Hosts the portable peer runtime for near-continuous collection and sync. Started explicitly by the user, then resumed after
/// updates, reboots and app opens until they stop it; shows a persistent notification, and is stoppable from the notification or
/// the activity.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[global::Android.App.MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "peer-monitoring-collection-and-sync")]
public sealed class PeerForegroundService : Service
{
    public const string ActionStart = "app.monitor.selfhosted.START";
    public const string ActionStop = "app.monitor.selfhosted.STOP";
    private const string ChannelId = "monitor-peer";
    private const int NotificationId = 1;
    private const string AlertChannelId = "monitor-alerts";
    private Action<IReadOnlyList<InboxAlert>>? alertHandler;

    private const string PreferencesName = "monitoring";
    private const string LeftOnKey = "left-on";

    /// <summary>The person starts monitoring; it is remembered so updates, reboots and app opens resume it until they stop it.</summary>
    public static void Start(Context context)
    {
        RememberLeftOn(context, true);
        context.StartForegroundService(new Intent(context, typeof(PeerForegroundService)).SetAction(ActionStart));
    }

    public static void Stop(Context context)
    {
        RememberLeftOn(context, false);
        context.StartService(new Intent(context, typeof(PeerForegroundService)).SetAction(ActionStop));
    }

    /// <summary>
    /// Starts monitoring again only if the person started it and never stopped it (decision 2026-11-25: an update or reboot used to
    /// leave phones silent until someone tapped Start monitoring again).
    /// </summary>
    public static bool ResumeIfLeftOn(Context context)
    {
        if (!IsLeftOn(context))
        {
            return false;
        }

        context.StartForegroundService(new Intent(context, typeof(PeerForegroundService)).SetAction(ActionStart));
        return true;
    }

    private static bool IsLeftOn(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.GetBoolean(LeftOnKey, false) ?? false;

    private static void RememberLeftOn(Context context, bool on) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.Edit()?.PutBoolean(LeftOnKey, on)?.Apply();

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            RememberLeftOn(this, false); // also when stopped from the notification
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
        // Resumed after updates, reboots and app opens by ResumeIfLeftOn, only while the person left it on.
        return StartCommandResult.NotSticky;
    }

    private async Task StartRuntimeAsync()
    {
        var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(false);
        if (alertHandler is null)
        {
            // Alerts arrive over the sync session; the runtime re-raises unnotified alerts after every sync and
            // restart, so a notification is only marked done after Android accepted it.
            alertHandler = alerts => _ = NotifyAsync(runtime, alerts);
            runtime.NewAlerts += alertHandler;
        }

        runtime.Start();
    }

    private async Task NotifyAsync(MobilePeerRuntime runtime, IReadOnlyList<InboxAlert> alerts)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            return;
        }

        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(AlertChannelId, "Alerts", NotificationImportance.High));
        foreach (var item in alerts)
        {
            var alert = item.Alert;
            var resolved = alert.State == MonitorApp.Contracts.V1.AlertState.Resolved;
            var open = PendingIntent.GetActivity(
                this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable)!;
            var notification = new Notification.Builder(this, AlertChannelId)
                .SetContentTitle(resolved ? "Resolved: " + alert.Title : alert.Title)
                .SetContentText(alert.Detail)
                .SetStyle(new Notification.BigTextStyle().BigText(alert.Detail))
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert)
                .SetAutoCancel(true)
                .SetContentIntent(open)
                .Build()!;
            // One notification per alert id: a resolve replaces the raise instead of stacking.
            manager.Notify(StableNotificationId(alert.AlertId), notification);
            await runtime.MarkAlertNotifiedAsync(item).ConfigureAwait(false);
        }
    }

    private static int StableNotificationId(string alertId)
    {
        var hash = 17;
        foreach (var character in alertId)
        {
            hash = unchecked((hash * 31) + character);
        }

        return 1000 + (hash & 0x3fffffff);
    }

    private async Task StopRuntimeAsync()
    {
        var runtime = await MobileRuntimeHost.GetAsync(this).ConfigureAwait(false);
        if (alertHandler is not null)
        {
            runtime.NewAlerts -= alertHandler;
            alertHandler = null;
        }

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

/// <summary>Resumes monitoring after this app is updated or the device restarts, if the person left it on.</summary>
[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public sealed class ResumeMonitoringReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action;
        if (context is not null && (action == Intent.ActionBootCompleted || action == Intent.ActionMyPackageReplaced))
        {
            MonitorApp.Peer.Application.DiagnosticLog.Write("node", $"resuming monitoring after {action}: {PeerForegroundService.ResumeIfLeftOn(context)}");
        }
    }
}
