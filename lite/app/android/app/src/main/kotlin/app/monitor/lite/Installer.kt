package app.monitor.lite

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import androidx.core.content.IntentCompat
import java.io.File

/**
 * Installs a downloaded update through Android's PackageInstaller, which
 * reports back how it went (2.0.1 to 2.3.0 handed the file to the system
 * viewer and never learned what happened). Every step lands in the device's
 * log.txt in the shared folder.
 */
object Installer {
    private const val ACTION = "app.monitor.lite.INSTALL_STATUS"
    const val EXTRA_AUTO = "app.monitor.lite.AUTO"

    /** "permission": Install unknown apps is off for this app and its
     *  settings page was opened. "started": Android has the update. */
    fun install(ctx: Context, f: File, auto: Boolean = false): String {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !ctx.packageManager.canRequestPackageInstalls()) {
            Collector.logToFolder(ctx, "install needs Install unknown apps for Monitor App${if (auto) "" else "; opening that setting"}")
            return "permission"
        }
        val pi = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(ctx.packageName)
        // No tap when this app installed the running version (Android 12+).
        // Otherwise Android still asks once (STATUS_PENDING_USER_ACTION).
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = pi.createSession(params)
        pi.openSession(id).use { s ->
            s.openWrite("update.apk", 0, f.length()).use { out ->
                f.inputStream().use { it.copyTo(out) }
                s.fsync(out)
            }
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) PendingIntent.FLAG_MUTABLE else 0)
            val intent = Intent(ctx, InstallReceiver::class.java).setAction(ACTION).setPackage(ctx.packageName)
                .putExtra(EXTRA_AUTO, auto)
            s.commit(PendingIntent.getBroadcast(ctx, id, intent, flags).intentSender)
        }
        Collector.logToFolder(ctx, "install handed to Android (${f.length() / 1024} KB)")
        return "started"
    }
}

class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, Int.MIN_VALUE)
        val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                val confirm = IntentCompat.getParcelableExtra(intent, Intent.EXTRA_INTENT, Intent::class.java)
                if (confirm == null) {
                    Collector.logToFolder(ctx, "ERROR install wants a confirmation but Android gave none")
                    return
                }
                Collector.logToFolder(ctx, "install waiting for your confirmation")
                // Opened from the app's own screen when it is showing. From
                // here (a broadcast) Android 14+ silently blocks it: the Pixel
                // sat at "waiting for your confirmation" with a full bar on
                // 2026-11-27. Otherwise a notification opens it on a tap.
                val act = if (intent.getBooleanExtra(Installer.EXTRA_AUTO, false)) null else MainActivity.resumed?.get()
                if (act != null) {
                    act.runOnUiThread {
                        runCatching { act.startActivity(confirm) }
                            .onSuccess { Collector.logToFolder(ctx, "install confirmation opened") }
                            .onFailure {
                                Collector.logToFolder(ctx, "ERROR could not open the install confirmation: $it")
                                notifyConfirm(ctx, confirm)
                            }
                    }
                } else {
                    notifyConfirm(ctx, confirm)
                }
            }
            PackageInstaller.STATUS_SUCCESS -> Collector.logToFolder(ctx, "install succeeded")
            else -> Collector.logToFolder(ctx, "ERROR install failed: status=$status ${msg ?: ""}")
        }
    }

    private fun notifyConfirm(ctx: Context, confirm: Intent) {
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        val tap = PendingIntent.getActivity(ctx, 9, confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), flags)
        Alerts.post(ctx, "update", 9, Collector.deviceName(ctx), "Monitor App update ready",
            "Tap to install it. After this one, updates install on their own.", tap)
    }
}
