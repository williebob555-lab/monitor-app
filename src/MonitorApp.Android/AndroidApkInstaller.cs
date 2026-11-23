using Android.App;
using Android.Content;
using Android.Content.PM;
using MonitorApp.Peer.Mobile;

namespace MonitorApp.Android;

/// <summary>
/// Hands a verified APK to Android's package installer through a <see cref="PackageInstaller"/> session. Android then shows
/// its own "update this app?" confirmation, and refuses any APK not signed with the installed app's key. The first time,
/// Android also asks the person to allow Monitor App to install updates ("Install unknown apps").
/// </summary>
internal sealed class AndroidApkInstaller(Context context) : IApkInstaller
{
    public async Task<string?> InstallAsync(string apkPath, CancellationToken cancellationToken)
    {
        var packageManager = context.PackageManager!;
        if (!packageManager.CanRequestPackageInstalls())
        {
            var settings = new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
                global::Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            return "Android needs your permission for Monitor App to install its own updates. Turn on \"Allow from this source\" in the screen that opened, come back, and press Install again.";
        }

        var installer = packageManager.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        try
        {
            var length = new FileInfo(apkPath).Length;
            await using (var input = File.OpenRead(apkPath))
            using (var output = session.OpenWrite("monitorapp.apk", 0, length))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                session.Fsync(output);
            }

            var intent = new Intent(context, typeof(ApkInstallResultReceiver)).SetPackage(context.PackageName);
            // Mutable: the installer adds the status and the confirmation intent to it.
            var pending = PendingIntent.GetBroadcast(context, sessionId, intent, PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent)!;
            session.Commit(pending.IntentSender!);
            return null;
        }
        catch (Exception exception) when (exception is IOException or Java.Lang.Exception)
        {
            session.Abandon();
            return $"Android could not start the install: {exception.Message}";
        }
    }
}

/// <summary>Receives the installer's status: shows Android's confirmation when it asks for the person, otherwise nothing.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class ApkInstallResultReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
        {
            return;
        }

        var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
        if (status != (int)PackageInstallStatus.PendingUserAction)
        {
            return;
        }

        Intent? confirm;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            confirm = intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent;
        }
        else
        {
#pragma warning disable CA1422, CS0618 // The typed overload exists only from API 33.
            confirm = intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422, CS0618
        }

        if (confirm is not null)
        {
            confirm.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(confirm);
        }
    }
}
