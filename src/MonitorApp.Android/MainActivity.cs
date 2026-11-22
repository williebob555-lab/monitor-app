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
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
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
}
