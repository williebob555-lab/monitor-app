package app.monitor.lite

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.Calendar

/**
 * Same rules as lite/core/lib/src/history.dart (read that file's comment):
 * readings wait in a local spool, move into history/detail/YYYY-MM-DD-HH.jsonl
 * every 15 minutes, ended hours become one line in history/hourly/YYYY-MM.jsonl,
 * ended days one line in history/daily/YYYY.jsonl (and Patterns.kt one line of
 * pattern values a day in history/patterns/YYYY.jsonl); old files are pruned by
 * the shared limits in <folder>/settings.json plus a size cap per device.
 */
class History(private val deviceDir: File, private val spoolDir: File) {
    private val historyDir = File(deviceDir, "history")
    private val spool = File(spoolDir, "history-spool.jsonl")
    private val stateFile = File(spoolDir, "history-state.json")

    data class Retention(val detailDays: Int = 7, val hourlyDays: Int = 365, val dailyDays: Int = 1825, val maxMb: Int = 50) {
        companion object {
            fun load(folder: File): Retention {
                val h = runCatching { JSONObject(File(folder, "settings.json").readText()).optJSONObject("history") }.getOrNull()
                    ?: return Retention()
                fun pick(k: String, d: Int, lo: Int, hi: Int) = if (h.has(k) && h.opt(k) is Number) h.getDouble(k).toInt().coerceIn(lo, hi) else d
                return Retention(pick("detailDays", 7, 1, 90), pick("hourlyDays", 365, 7, 3650), pick("dailyDays", 1825, 30, 36500), pick("maxMb", 50, 5, 2000))
            }
        }
    }

    private fun state(): JSONObject = runCatching { JSONObject(stateFile.readText()) }.getOrElse { JSONObject() }

    fun add(line: JSONObject, nowMs: Long) {
        spoolDir.mkdirs()
        spool.appendText(line.toString() + "\n")
        val last = state().optLong("lastFlush", 0)
        val nowS = nowMs / 1000
        if (nowS - last >= 15 * 60 || last / 3600 != nowS / 3600) flush(nowMs)
    }

    fun flush(nowMs: Long) {
        val byHour = LinkedHashMap<String, StringBuilder>()
        for (l in readLines(spool)) {
            byHour.getOrPut(detailName(cal(l.getLong("t") * 1000))) { StringBuilder() }.append(l.toString()).append('\n')
        }
        val detail = File(historyDir, "detail").apply { mkdirs() }
        for ((name, text) in byHour) File(detail, name).appendText(text.toString())
        if (spool.exists()) spool.writeText("")
        val st = state()
        st.put("lastFlush", nowMs / 1000)
        summarizeEnded(nowMs, st)
        stateFile.writeText(st.toString())
    }

    private fun summarizeEnded(nowMs: Long, st: JSONObject) {
        val thisHour = cal(nowMs).apply { set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0); set(Calendar.MILLISECOND, 0) }
        val h = if (st.has("hourlyDone")) cal(st.getLong("hourlyDone") * 1000).apply { add(Calendar.HOUR_OF_DAY, 1) }
        else (thisHour.clone() as Calendar).apply { add(Calendar.HOUR_OF_DAY, -1) }
        if (thisHour.timeInMillis - h.timeInMillis > 2 * 86400_000L) h.timeInMillis = thisHour.timeInMillis - 2 * 86400_000L
        val hourlyDir = File(historyDir, "hourly").apply { mkdirs() }
        while (h.before(thisHour)) {
            val ls = readLines(File(historyDir, "detail/" + detailName(h)))
            if (ls.isNotEmpty()) File(hourlyDir, hourlyName(h)).appendText(summarize(ls, h.timeInMillis / 1000).toString() + "\n")
            st.put("hourlyDone", h.timeInMillis / 1000)
            h.add(Calendar.HOUR_OF_DAY, 1)
        }
        val today = (thisHour.clone() as Calendar).apply { set(Calendar.HOUR_OF_DAY, 0) }
        val d = if (st.has("dailyDone")) cal(st.getLong("dailyDone") * 1000).apply { add(Calendar.DAY_OF_MONTH, 1) }
        else (today.clone() as Calendar).apply { add(Calendar.DAY_OF_MONTH, -1) }
        if (today.timeInMillis - d.timeInMillis > 7 * 86400_000L) {
            d.timeInMillis = today.timeInMillis; d.add(Calendar.DAY_OF_MONTH, -7)
        }
        val dailyDir = File(historyDir, "daily").apply { mkdirs() }
        while (d.before(today)) {
            val start = d.timeInMillis / 1000
            val end = (d.clone() as Calendar).apply { add(Calendar.DAY_OF_MONTH, 1) }.timeInMillis / 1000
            val ls = readLines(File(hourlyDir, hourlyName(d))).filter { it.getLong("t") in start until end }
            if (ls.isNotEmpty()) File(dailyDir, dailyName(d)).appendText(summarize(ls, start).toString() + "\n")
            st.put("dailyDone", start)
            d.add(Calendar.DAY_OF_MONTH, 1)
        }
    }

    /** Delete by age, then oldest-first above the size cap. Returns bytes used. */
    fun prune(nowMs: Long, r: Retention): Long {
        val keep = mutableListOf<File>()
        for ((kind, days) in listOf("detail" to r.detailDays, "hourly" to r.hourlyDays, "daily" to r.dailyDays, "patterns" to r.dailyDays)) {
            val limit = nowMs - days * 86400_000L
            File(historyDir, kind).listFiles()?.filter { it.isFile }?.forEach { f ->
                val end = endOf(f.name.removeSuffix(".jsonl"), kind)
                if (f.name.contains(".sync-conflict-") || (end != null && end < limit)) f.delete() else keep += f
            }
        }
        var total = keep.sumOf { it.length() }
        val cap = r.maxMb * 1024L * 1024L
        if (total > cap) {
            fun rank(f: File) = when (f.parentFile?.name) { "detail" -> 0; "hourly" -> 1; else -> 2 }
            for (f in keep.sortedWith(compareBy<File>({ rank(it) }, { it.name }))) {
                if (total <= cap) break
                total -= f.length()
                f.delete()
            }
        }
        return total
    }

    private fun endOf(name: String, kind: String): Long? {
        val p = name.split("-").map { it.toIntOrNull() ?: return null }
        val c = Calendar.getInstance().apply { clear() }
        when {
            kind == "detail" && p.size == 4 -> { c.set(p[0], p[1] - 1, p[2], p[3], 0); c.add(Calendar.HOUR_OF_DAY, 1) }
            kind == "hourly" && p.size == 2 -> { c.set(p[0], p[1] - 1, 1, 0, 0); c.add(Calendar.MONTH, 1) }
            (kind == "daily" || kind == "patterns") && p.size == 1 -> { c.set(p[0], 0, 1, 0, 0); c.add(Calendar.YEAR, 1) }
            else -> return null
        }
        return c.timeInMillis
    }

    companion object {
        fun cal(ms: Long): Calendar = Calendar.getInstance().apply { timeInMillis = ms }
        private fun two(v: Int) = v.toString().padStart(2, '0')
        fun detailName(c: Calendar) = "${c.get(Calendar.YEAR)}-${two(c.get(Calendar.MONTH) + 1)}-${two(c.get(Calendar.DAY_OF_MONTH))}-${two(c.get(Calendar.HOUR_OF_DAY))}.jsonl"
        fun hourlyName(c: Calendar) = "${c.get(Calendar.YEAR)}-${two(c.get(Calendar.MONTH) + 1)}.jsonl"
        fun dailyName(c: Calendar) = "${c.get(Calendar.YEAR)}.jsonl"

        fun readLines(f: File): List<JSONObject> {
            if (!f.exists()) return emptyList()
            return f.readLines().mapNotNull { l -> runCatching { JSONObject(l) }.getOrNull()?.takeIf { it.has("t") } }
        }

        /** {"t": start, "n": count, key: [min, avg, max]} for every numeric value, nested ones as "a.b". */
        fun summarize(lines: List<JSONObject>, start: Long): JSONObject {
            val acc = LinkedHashMap<String, DoubleArray>() // min, sum, max, count
            fun add(k: String, v: Any?) {
                when {
                    v is Number -> acc.getOrPut(k) { doubleArrayOf(Double.MAX_VALUE, 0.0, -Double.MAX_VALUE, 0.0) }.let {
                        val d = v.toDouble(); it[0] = minOf(it[0], d); it[1] += d; it[2] = maxOf(it[2], d); it[3] += 1
                    }
                    v is JSONArray && v.length() == 3 -> acc.getOrPut(k) { doubleArrayOf(Double.MAX_VALUE, 0.0, -Double.MAX_VALUE, 0.0) }.let {
                        it[0] = minOf(it[0], v.getDouble(0)); it[1] += v.getDouble(1); it[2] = maxOf(it[2], v.getDouble(2)); it[3] += 1
                    }
                    v is JSONObject -> for (kk in v.keys()) add("$k.$kk", v.get(kk))
                }
            }
            var n = 0
            for (l in lines) {
                n += l.optInt("n", 1)
                for (k in l.keys()) if (k != "t" && k != "n") add(k, l.get(k))
            }
            fun r1(v: Double) = Math.round(v * 10) / 10.0
            val out = JSONObject().put("t", start).put("n", n)
            for ((k, a) in acc) out.put(k, JSONArray().put(r1(a[0])).put(r1(a[1] / a[3])).put(r1(a[2])))
            return out
        }
    }
}
