package app.monitor.lite

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Rules on the phone (roadmap row 20). Checked after each collection run
 * from files already in the shared folder: no extra wake-ups, no network.
 *
 *  - The phone's own "Notify me on this phone" switches become rule files,
 *    `rules/phone-<this phone>-offline-<device>.json` (stopped reporting
 *    for 30 min) and `...-stuck-<device>.json` (Syncthing folder not up to
 *    date for 15 min), each notifying only this phone. Switching one off
 *    disables its rules; nothing is deleted.
 *  - Every firing with a notification for this phone (FORMAT.md "Rules") is
 *    shown once, unless it reaches the phone over an hour late.
 *  - Until a computer on 2.6.9 or newer is running to judge rules, the old
 *    checks on the phone ([Alerts.check]) keep working instead.
 */
object RuleNotices {
    private const val RULES_SINCE = "2.6.9"
    private const val MAX_AGE_S = 3600L
    private val ID = Regex("^[a-z0-9_-][a-z0-9._-]{0,63}$")

    fun run(ctx: Context, root: File, nowS: Long) {
        val me = Collector.deviceId(ctx)
        runCatching { syncRules(ctx, root, me) }.onFailure { Collector.logToFolder(ctx, "ERROR rules from switches: $it") }
        if (rulesRunning(root, nowS)) show(ctx, root, me, nowS) else Alerts.check(ctx, root, nowS)
    }

    /** A fresh computer that judges rules, as the computers work it out. */
    fun rulesRunning(root: File, nowS: Long): Boolean {
        val devs = File(root, "devices").listFiles() ?: return false
        return devs.any { d ->
            val j = runCatching { JSONObject(File(d, "now.json").readText()) }.getOrNull() ?: return@any false
            val kind = j.optString("kind")
            val app = j.optString("app", "")
            val next = j.optLong("next", 60)
            kind != "android" && kind != "thing" && app.isNotEmpty() && app != "dev" &&
                Updater.compare(app, RULES_SINCE) >= 0 && nowS - j.optLong("t", 0) <= maxOf(next * 2 + 60, 150)
        }
    }

    private fun ruleFor(id: String, name: String, me: String, kind: String, on: Boolean): JSONObject {
        val notify = JSONObject().put("type", "notify").put("on", JSONArray().put(me))
        val base = JSONObject().put("v", 1).put("id", "phone-$me-$kind-$id").put("enabled", on)
            .put("scope", JSONObject().put("devices", JSONArray().put(id)))
            .put("then", JSONArray().put(notify)).put("cooldown", 0).put("origin", "phone switch")
        return if (kind == "offline") {
            base.put("name", "$name stopped reporting").put("when", JSONObject().put("type", "offline").put("for", Alerts.STALE_S))
        } else {
            base.put("name", "$name: Syncthing folder stuck").put("when", JSONObject().put("type", "value")
                .put("path", "readings.st.problems").put("op", "!=").put("value", "").put("for", Alerts.STUCK_S))
        }
    }

    private fun syncRules(ctx: Context, root: File, me: String) {
        if (!ID.matches(me)) return
        val dir = File(root, "rules")
        val want = Alerts.ids(ctx).filter { ID.matches(it) }.toSet()
        val prefix = "phone-$me-"
        // Switched on: present and enabled.
        for (id in want) {
            val name = runCatching { JSONObject(File(root, "devices/$id/now.json").readText()).optString("name", id) }.getOrNull() ?: id
            for (kind in listOf("offline", "stuck")) {
                val f = File(dir, "$prefix$kind-$id.json")
                val old = runCatching { JSONObject(f.readText()) }.getOrNull()
                if (old == null) write(dir, f, ruleFor(id, name, me, kind, true))
                else if (!old.optBoolean("enabled", true)) write(dir, f, old.put("enabled", true))
            }
        }
        // Switched off: disabled, kept.
        for (f in dir.listFiles() ?: emptyArray()) {
            val n = f.name
            if (!n.startsWith(prefix) || !n.endsWith(".json") || n.contains(".sync-conflict-")) continue
            val id = n.removePrefix(prefix).removeSuffix(".json").substringAfter('-')
            if (id in want) continue
            val old = runCatching { JSONObject(f.readText()) }.getOrNull() ?: continue
            if (old.optBoolean("enabled", true)) write(dir, f, old.put("enabled", false))
        }
    }

    private fun write(dir: File, f: File, j: JSONObject) {
        dir.mkdirs()
        val tmp = File(dir, ".syncthing.${f.name}.tmp")
        tmp.writeText(j.toString(2))
        if (!tmp.renameTo(f)) { f.writeText(j.toString(2)); tmp.delete() }
        f.setLastModified(System.currentTimeMillis())
    }

    private fun show(ctx: Context, root: File, me: String, nowS: Long) {
        val p = Collector.prefs(ctx)
        val from = p.getLong("notify.from", 0)
        if (from == 0L) { p.edit().putLong("notify.from", nowS).apply(); return }
        val oldest = maxOf(nowS - MAX_AGE_S, from)
        val shown = (p.getStringSet("notify.shown", emptySet()) ?: emptySet())
            .mapNotNull { s -> s.substringBeforeLast('\t').let { k -> s.substringAfterLast('\t').toLongOrNull()?.let { k to it } } }
            .toMap().toMutableMap()
        val notes = mutableListOf<Triple<String, String, String>>()
        for (rd in File(root, "rules").listFiles() ?: emptyArray()) {
            if (!rd.isDirectory) continue
            for (f in File(rd, "fired").listFiles() ?: emptyArray()) {
                if (!f.name.endsWith(".jsonl") || f.name.contains(".sync-conflict-")) continue
                if (f.lastModified() / 1000 < oldest - 300) continue
                for (line in runCatching { f.readLines() }.getOrDefault(emptyList())) {
                    val l = runCatching { JSONObject(line) }.getOrNull() ?: continue
                    val t = l.optLong("t", 0)
                    val text = l.optString("notify", "")
                    if (t < oldest || t > nowS + 300 || text.isEmpty()) continue
                    val on = l.optJSONArray("on")
                    if (on != null && (0 until on.length()).none { on.optString(it) == me }) continue
                    val key = "${l.optString("rule")}|${l.optString("subject")}|${l.optLong("since")}"
                    if (key in shown) continue
                    shown[key] = t
                    notes.add(Triple(key, l.optString("name", l.optString("rule")), text))
                }
            }
        }
        shown.entries.removeAll { it.value < nowS - 2 * MAX_AGE_S }
        p.edit().putStringSet("notify.shown", shown.map { "${it.key}\t${it.value}" }.toSet()).apply()
        for ((key, title, text) in notes.take(3)) Alerts.post(ctx, "rule:$key", 0, title, title, text)
        if (notes.size > 3) Alerts.post(ctx, "rule:more", 0, "Monitor App", "${notes.size - 3} more rules fired", "Open Monitor App to see them.")
    }
}
