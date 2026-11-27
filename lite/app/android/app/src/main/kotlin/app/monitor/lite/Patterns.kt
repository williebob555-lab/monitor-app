package app.monitor.lite

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.Calendar

/**
 * Same rules as lite/core/lib/src/patterns.dart (read that file): what this
 * device's history shows, in words, written every 6 hours to
 * devices/<id>/patterns.json, plus one line of values per day in
 * history/patterns/YYYY.jsonl so patterns outlast the files they came from.
 * Keep the two in step; core's tests describe the expected results.
 */
object Patterns {
    data class P(val key: String, val group: String, val text: String, val hours: Double, val value: Double? = null,
                 val unit: String? = null, val estimated: Boolean = false) {
        fun json(): JSONObject = JSONObject().put("key", key).put("group", group).put("text", text).apply {
            if (value != null) put("value", value)
            if (unit != null) put("unit", unit)
            put("hours", Math.round(hours * 10) / 10.0)
            if (estimated) put("src", "estimated")
        }
    }

    private const val MIN_HOURS = 1.0

    /** Writes patterns.json and today's line of history/patterns. [others] are the other devices' folders. */
    fun write(deviceDir: File, others: List<File>, nowMs: Long) {
        val names = HashMap<String, String>()
        val up = HashMap<String, Set<Long>>()
        for (o in others) {
            runCatching { JSONObject(File(o, "now.json").readText()).optString("name") }.getOrNull()?.takeIf { it.isNotEmpty() }?.let { names[o.name] = it }
            up[o.name] = readHourly(o, nowMs).map { it.getLong("t") }.toSet()
        }
        val list = find(readDetail(deviceDir, nowMs), readHourly(deviceDir, nowMs), names, up)
        val out = File(deviceDir, "patterns.json")
        out.writeText(JSONObject().put("v", 1).put("t", nowMs / 1000).put("patterns", JSONArray().apply { list.forEach { put(it.json()) } }).toString(2))
        out.setLastModified(System.currentTimeMillis())
        record(deviceDir, list, nowMs)
    }

    fun record(deviceDir: File, list: List<P>, nowMs: Long) {
        val day = History.cal(nowMs).apply { set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }
        val t = day.timeInMillis / 1000
        val line = JSONObject().put("t", t)
        for (p in list) if (p.value != null) line.put(p.key, Math.round(p.value * 100) / 100.0)
        if (line.length() == 1) return
        val dir = File(deviceDir, "history/patterns").apply { mkdirs() }
        val f = File(dir, History.dailyName(day))
        val lines = History.readLines(f).filter { it.optLong("t") != t } + line
        f.writeText(lines.sortedBy { it.getLong("t") }.joinToString("") { "$it\n" })
        f.setLastModified(System.currentTimeMillis())
    }

    fun readDetail(deviceDir: File, nowMs: Long, days: Int = 7): List<JSONObject> {
        val out = ArrayList<JSONObject>()
        for (i in days * 24 downTo 0) out += History.readLines(File(deviceDir, "history/detail/" + History.detailName(History.cal(nowMs - i * 3600_000L))))
        return out.sortedBy { it.getLong("t") }
    }

    fun readHourly(deviceDir: File, nowMs: Long, days: Int = 30): List<JSONObject> {
        val from = nowMs / 1000 - days * 86400L
        val names = LinkedHashSet<String>()
        var d = days
        while (d >= 0) { names += History.hourlyName(History.cal(nowMs - d * 86400_000L)); d -= 28 }
        names += History.hourlyName(History.cal(nowMs))
        return names.flatMap { History.readLines(File(deviceDir, "history/hourly/$it")) }.filter { it.getLong("t") >= from }.sortedBy { it.getLong("t") }
    }

    fun find(detail: List<JSONObject>, hourly: List<JSONObject>, names: Map<String, String>, up: Map<String, Set<Long>>?): List<P> =
        battery(detail) + byTimeOfDay(hourly, "cpu", "Processor", "CPU") + usual(hourly, "mem", "Memory", "Memory") +
            usual(hourly, "temp.cpu", "Temperature", "CPU temperature", "°C") + traffic(hourly) + reach(hourly, names, up)

    private fun num(l: JSONObject, k: String): Double? = (l.opt(k) as? Number)?.toDouble()
    private fun avg(l: JSONObject, k: String): Double? = when (val v = l.opt(k)) {
        is Number -> v.toDouble()
        is JSONArray -> if (v.length() == 3 && v.opt(1) is Number) v.getDouble(1) else null
        else -> null
    }
    private fun max(l: JSONObject, k: String): Double? = when (val v = l.opt(k)) {
        is Number -> v.toDouble()
        is JSONArray -> if (v.length() == 3 && v.opt(2) is Number) v.getDouble(2) else null
        else -> null
    }
    private fun f(v: Double) = if (v >= 10) Math.round(v).toString() else "%.1f".format(java.util.Locale.ROOT, v)
    private fun h(hours: Double) = if (hours >= 48) "${Math.round(hours / 24)} days" else "${f(hours)} h"

    private fun battery(lines: List<JSONObject>): List<P> {
        val acc = LinkedHashMap<String, DoubleArray>() // percent change, seconds
        for (i in 1 until lines.size) {
            val a = lines[i - 1]; val b = lines[i]
            val ba = num(a, "b") ?: continue; val bb = num(b, "b") ?: continue
            val dt = b.getLong("t") - a.getLong("t")
            if (dt <= 0 || dt > 1200) continue
            val ca = num(a, "c") ?: continue
            if (ca != num(b, "c")) continue
            val cond: String
            if (ca == 1.0) {
                if (ba >= 99) continue // full: not charging any more
                cond = "charge"
            } else {
                if (ba >= 99 && bb >= 99) continue // held at 100 % on mains, not drain
                val sa = num(a, "s"); val sb = num(b, "s")
                cond = if (sa == null) "drain" else if (sa != sb) continue else if (sa == 1.0) "drain.on" else "drain.off"
            }
            val x = acc.getOrPut(cond) { doubleArrayOf(0.0, 0.0) }
            x[0] += bb - ba; x[1] += dt.toDouble()
        }
        val out = ArrayList<P>()
        for ((k, v) in acc) {
            val hours = v[1] / 3600
            if (hours < MIN_HOURS) continue
            val rate = v[0] / hours
            val over = "(over ${h(hours)})"
            if (k == "charge") {
                if (rate > 0) out += P("battery.charge", "Battery", "Charges ${f(rate)} %/h $over", hours, rate, "%/h")
                continue
            }
            val drain = -rate
            val whenText = when (k) { "drain.on" -> " with the screen on"; "drain.off" -> " with the screen off"; else -> " on battery" }
            if (drain <= 0.05) {
                out += P("battery.$k", "Battery", "Hardly drains$whenText $over", hours, 0.0, "%/h")
                continue
            }
            out += P("battery.$k", "Battery", "Drains ${f(drain)} %/h$whenText $over", hours, drain, "%/h")
            out += P("battery.$k.lasts", "Battery", "A full charge would last about ${h(100 / drain)}$whenText", hours, 100 / drain, "h", estimated = true)
        }
        return out
    }

    private val parts = listOf("at night", "in the morning", "in the afternoon", "in the evening")

    private fun byTimeOfDay(hourly: List<JSONObject>, k: String, group: String, label: String): List<P> {
        val sum = DoubleArray(4); val n = IntArray(4)
        for (l in hourly) {
            val v = avg(l, k) ?: continue
            val part = History.cal(l.getLong("t") * 1000).get(Calendar.HOUR_OF_DAY) / 6
            sum[part] += v; n[part]++
        }
        val ok = (0 until 4).filter { n[it] >= 2 }
        val hours = n.sum().toDouble()
        if (ok.size < 2 || hours < 6) return emptyList()
        fun a(i: Int) = sum[i] / n[i]
        val hi = ok.reduce { x, y -> if (a(x) >= a(y)) x else y }
        val lo = ok.reduce { x, y -> if (a(x) <= a(y)) x else y }
        if (a(hi) - a(lo) < 2) return listOf(P("$k.steady", group, "$label is about ${f(a(hi))} % all day (over ${h(hours)})", hours, a(hi), "%"))
        return listOf(P("$k.busiest", group, "$label is busiest ${parts[hi]} (${f(a(hi))} %) and quietest ${parts[lo]} (${f(a(lo))} %), over ${h(hours)}", hours, a(hi), "%"))
    }

    private fun usual(hourly: List<JSONObject>, k: String, group: String, label: String, unit: String = "%"): List<P> {
        val avgs = hourly.mapNotNull { avg(it, k) }.sorted()
        if (avgs.size < 3) return emptyList()
        val peak = hourly.mapNotNull { max(it, k) }.fold(-1e9) { x, y -> maxOf(x, y) }
        val usual = avgs[avgs.size / 2]
        val hours = avgs.size.toDouble()
        val sp = if (unit == "%") " %" else " $unit"
        return listOf(P("$k.usual", group, "$label is usually ${f(usual)}$sp, highest ${f(peak)}$sp (over ${h(hours)})", hours, usual, unit))
    }

    private fun traffic(hourly: List<JSONObject>): List<P> {
        var rx = 0.0; var tx = 0.0; var hours = 0
        for (l in hourly) {
            val r = avg(l, "rx") ?: continue; val t = avg(l, "tx") ?: continue
            rx += r * 3600; tx += t * 3600; hours++
        }
        if (hours < 6) return emptyList()
        fun size(b: Double) = if (b >= 1e9) "${f(b / 1e9)} GB" else if (b >= 1e6) "${f(b / 1e6)} MB" else "${f(b / 1e3)} KB"
        val perDay = 24.0 / hours
        return listOf(P("net.perDay", "Network", "Downloads about ${size(rx * perDay)} and uploads ${size(tx * perDay)} a day (over ${h(hours.toDouble())})",
            hours.toDouble(), (rx + tx) * perDay, "B/day"))
    }

    private fun reach(hourly: List<JSONObject>, names: Map<String, String>, up: Map<String, Set<Long>>?): List<P> {
        val sum = LinkedHashMap<String, Double>(); val n = LinkedHashMap<String, Int>()
        for (l in hourly) {
            for (key in l.keys()) {
                if (!key.startsWith("p.")) continue
                val v = avg(l, key) ?: continue
                val id = key.substring(2)
                if (up != null && up[id]?.contains(l.getLong("t")) != true) continue
                sum[id] = (sum[id] ?: 0.0) + v; n[id] = (n[id] ?: 0) + 1
            }
        }
        val always = ArrayList<String>(); val out = ArrayList<P>()
        for ((id, s) in sum) {
            val hours = n[id]!!.toDouble()
            if (hours < 6) continue
            val pct = 100 * s / hours
            val name = names[id] ?: id
            if (pct >= 99) always += name
            else out += P("reach.$id", "Connections", if (pct < 1) "Never reached $name (over ${h(hours)})" else "Reached $name ${f(pct)} % of the time (over ${h(hours)})", hours, pct, "%")
        }
        if (always.isNotEmpty()) {
            always.sort()
            out.add(0, P("reach.always", "Connections", "Always reached ${always.joinToString(", ")}", (n.values.maxOrNull() ?: 0).toDouble(), 100.0, "%"))
        }
        return out
    }
}
