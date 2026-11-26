package app.monitor.lite

import android.app.NotificationManager
import android.app.job.JobScheduler
import android.content.Context
import java.io.File

/**
 * This app installs as an update over the old Monitor App (same package id
 * and key). The first time it runs, it removes everything the old app left
 * in its private storage: its database, logs, keys, settings, scheduled jobs
 * and notification channels. Runs once; files this app owns are kept.
 */
object Migration {
    private val keepFiles = setOf("history-spool.jsonl", "history-state.json", "hidden.json", "log.txt")

    fun run(ctx: Context) {
        val p = Collector.prefs(ctx)
        if (p.getBoolean("oldAppRemoved", false)) return
        var removed = 0
        fun clear(dir: File?, keep: (File) -> Boolean = { false }) {
            dir?.listFiles()?.forEach { f -> if (!keep(f) && f.deleteRecursively()) removed++ }
        }
        runCatching {
            clear(ctx.filesDir) { it.name in keepFiles }
            clear(ctx.noBackupFilesDir)
            clear(ctx.cacheDir)
            clear(File(ctx.applicationInfo.dataDir, "databases"))
            clear(File(ctx.applicationInfo.dataDir, "shared_prefs")) { it.name == "monitor.xml" }
            // Any other folders the old app made (keep Android's and Flutter's own).
            val system = setOf("files", "shared_prefs", "cache", "code_cache", "no_backup", "databases", "app_flutter", "lib")
            clear(File(ctx.applicationInfo.dataDir)) { it.name in system }
            ctx.getSystemService(JobScheduler::class.java)?.cancelAll()
            ctx.getSystemService(NotificationManager::class.java)?.let { nm ->
                nm.cancelAll()
                nm.notificationChannels.forEach { nm.deleteNotificationChannel(it.id) }
            }
        }.onFailure { Collector.logToFolder(ctx, "old app cleanup failed: $it") }
        p.edit().putBoolean("oldAppRemoved", true).apply()
        Collector.logToFolder(ctx, "removed $removed items left by the old Monitor App")
    }
}
