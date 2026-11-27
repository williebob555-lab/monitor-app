package app.monitor.lite

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import org.json.JSONObject
import java.io.File

/**
 * Phone notifications about other devices, only for the ones switched on
 * in the app (Device > "Notify me on this phone"). Checked at the end of
 * each collection run from the files already in the shared folder: no
 * extra wake-ups, no network.
 *
 * Two kinds, each sent once and cleared when it is over:
 *  - a device stopped reporting for [STALE_S] seconds;
 *  - a device's Syncthing says a folder is not up to date (st.problems)
 *    for [STUCK_S] seconds.
 */
object Alerts {
    const val STALE_S = 30 * 60L
    const val STUCK_S = 15 * 60L
    private const val CHANNEL = "devices"

    fun ids(ctx: Context): Set<String> = Collector.prefs(ctx).getStringSet("notify", emptySet()) ?: emptySet()

    fun set(ctx: Context, id: String, on: Boolean) {
        val p = Collector.prefs(ctx)
        val now = ids(ctx).toMutableSet().apply { if (on) add(id) else remove(id) }
        p.edit().putStringSet("notify", now).apply()
        if (!on) clear(ctx, id)
    }

    fun enabled(ctx: Context): Boolean = NotificationManagerCompat.from(ctx).areNotificationsEnabled()

    /** Runs after every collection. Cheap: reads one small file per device switched on. */
    fun check(ctx: Context, root: File, nowS: Long) {
        val ids = ids(ctx)
        if (ids.isEmpty()) return
        val p = Collector.prefs(ctx)
        val e = p.edit()
        for (id in ids) {
            val f = File(root, "devices/$id/now.json")
            val j = runCatching { JSONObject(f.readText()) }.getOrNull() ?: continue
            val name = j.optString("name", id)
            val t = j.optLong("t", 0)
            val age = nowS - t

            // Stopped reporting.
            val staleKey = "alert.stale.$id"
            if (t > 0 && age >= STALE_S) {
                if (p.getLong(staleKey, 0) != t) {
                    post(ctx, id, 0, name, "$name stopped reporting ${ago(age)} ago", "Its last readings reached this phone ${ago(age)} ago.")
                    e.putLong(staleKey, t)
                }
            } else if (p.contains(staleKey)) {
                cancel(ctx, id, 0)
                e.remove(staleKey)
            }

            // Syncthing folder not up to date, judged only on fresh readings.
            val problem = if (age < STALE_S) syncthingProblem(j) else null
            val sinceKey = "alert.stuck.since.$id"
            val sentKey = "alert.stuck.sent.$id"
            if (problem != null) {
                val since = p.getLong(sinceKey, 0).takeIf { it > 0 } ?: nowS.also { e.putLong(sinceKey, it) }
                if (nowS - since >= STUCK_S && p.getString(sentKey, null) != problem) {
                    post(ctx, id, 1, name, "$name: Syncthing folder stuck", "Not up to date for ${ago(nowS - since)}: $problem")
                    e.putString(sentKey, problem)
                }
            } else if (p.contains(sinceKey) || p.contains(sentKey)) {
                cancel(ctx, id, 1)
                e.remove(sinceKey).remove(sentKey)
            }
        }
        e.apply()
    }

    private fun syncthingProblem(j: JSONObject): String? {
        val rs = j.optJSONArray("readings") ?: return null
        for (i in 0 until rs.length()) {
            val r = rs.optJSONObject(i) ?: continue
            if (r.optString("key") == "st.problems") return r.opt("value")?.toString()?.takeIf { it.isNotBlank() }
        }
        return null
    }

    private fun ago(s: Long): String = when {
        s < 3600 -> "${s / 60} min"
        s < 48 * 3600 -> "${s / 3600} h"
        else -> "${s / 86400} days"
    }

    private fun clear(ctx: Context, id: String) {
        cancel(ctx, id, 0)
        cancel(ctx, id, 1)
        Collector.prefs(ctx).edit()
            .remove("alert.stale.$id").remove("alert.stuck.since.$id").remove("alert.stuck.sent.$id").apply()
    }

    private fun tag(id: String, kind: Int) = "$id/$kind"

    private fun cancel(ctx: Context, id: String, kind: Int) =
        runCatching { NotificationManagerCompat.from(ctx).cancel(tag(id, kind), 1) }

    fun post(ctx: Context, id: String, kind: Int, name: String, title: String, text: String) {
        val nm = NotificationManagerCompat.from(ctx)
        if (!nm.areNotificationsEnabled()) {
            Collector.logToFolder(ctx, "notification for $name not shown: notifications are off for the app")
            return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            ctx.getSystemService(NotificationManager::class.java)
                .createNotificationChannel(NotificationChannel(CHANNEL, "Devices", NotificationManager.IMPORTANCE_DEFAULT))
        }
        val open = PendingIntent.getActivity(
            ctx, 0, Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE,
        )
        val n = NotificationCompat.Builder(ctx, CHANNEL)
            .setSmallIcon(R.drawable.ic_notify)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()
        try {
            nm.notify(tag(id, kind), 1, n)
            Collector.logToFolder(ctx, "notified: $title")
        } catch (e: SecurityException) {
            Collector.logToFolder(ctx, "notification for $name refused: $e")
        }
    }
}
