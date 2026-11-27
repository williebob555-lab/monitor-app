package app.monitor.lite

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.location.LocationManager
import org.json.JSONObject

/**
 * Where this phone is (roadmap row 29), only when switched on in the app,
 * off by default. Coarse only: the last position Android already knows
 * (no GPS, no extra wake-ups), rounded to about 1 km. Switching it off
 * removes the reading at the next run.
 */
object Location {
    fun on(ctx: Context): Boolean = Collector.prefs(ctx).getBoolean("location", false)

    fun set(ctx: Context, on: Boolean) {
        Collector.prefs(ctx).edit().putBoolean("location", on).apply()
        Collector.logToFolder(ctx, "location ${if (on) "switched on" else "switched off"} on this phone")
    }

    fun granted(ctx: Context): Boolean =
        ctx.checkSelfPermission(Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED

    /** The reading, or null when off. Says why when on but not available. */
    fun reading(ctx: Context, nowMs: Long): JSONObject? {
        if (!on(ctx)) return null
        val r = JSONObject().put("key", "loc").put("label", "Location").put("group", "Location")
        if (!granted(ctx)) return r.put("value", JSONObject.NULL).put("src", "unknown").put("note", "switched on, but Android has not allowed location for Monitor App")
        val lm = ctx.getSystemService(LocationManager::class.java) ?: return r.put("value", JSONObject.NULL).put("src", "unknown")
        val best = listOf(LocationManager.NETWORK_PROVIDER, LocationManager.PASSIVE_PROVIDER, "fused")
            .mapNotNull { p -> runCatching { @Suppress("MissingPermission") lm.getLastKnownLocation(p) }.getOrNull() }
            .maxByOrNull { it.time }
            ?: return r.put("value", JSONObject.NULL).put("src", "unknown").put("note", "Android has no recent position")
        val lat = Math.round(best.latitude * 100) / 100.0
        val lon = Math.round(best.longitude * 100) / 100.0
        val ageMin = (nowMs - best.time) / 60000
        return r.put("value", "$lat,$lon").put("src", "measured")
            .put("note", "about 1 km; ${if (ageMin < 1) "just now" else "$ageMin min ago"}; accuracy ${best.accuracy.toInt()} m")
    }
}
