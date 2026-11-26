using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace MonitorApp.Android;

/// <summary>
/// Hosts the shared Avalonia UI. Resizes (rotation, tablet split screen) are handled in place so the adaptive layout
/// reflows without recreating the activity; monitoring itself runs in <see cref="PeerForegroundService"/>.
/// </summary>
[Activity(
    Label = "Monitor App",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    // AvaloniaMainActivity uses AndroidX AppCompatActivity, which requires an AppCompat theme.
    Theme = "@style/Theme.AppCompat.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity<AndroidApp>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        }
    }

    // Screen off or another app in front stops the activity without detaching its views. Live screens pause on this,
    // so nothing refreshes or asks other devices for fast readings while nobody can see it. Monitoring itself runs in
    // the foreground service and is unaffected.
    protected override void OnStart()
    {
        base.OnStart();
        MonitorApp.Client.Core.AppVisibility.Set(true);
    }

    protected override void OnStop()
    {
        MonitorApp.Client.Core.AppVisibility.Set(false);
        base.OnStop();
    }
}
