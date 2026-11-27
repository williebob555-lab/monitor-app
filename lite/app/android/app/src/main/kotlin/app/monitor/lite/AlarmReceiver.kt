package app.monitor.lite

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Woken by the system alarm: collect once, book the next alarm, finish. */
class AlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        val ctx = context.applicationContext
        Thread {
            var next = Collector.INTERVAL_SCREEN_ON
            try {
                next = Collector.run(ctx, "alarm")
            } finally {
                Collector.schedule(ctx, next)
                pending.finish()
            }
        }.start()
    }
}

/** Starts the alarm chain again after a reboot or an app update. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val ctx = context.applicationContext
        Migration.run(ctx)
        Collector.logToFolder(ctx, "start (${intent.action?.substringAfterLast('.')}) app ${BuildConfig.VERSION_NAME}")
        Collector.schedule(ctx, 15)
        Updater.ensureScheduled(ctx)
    }
}
