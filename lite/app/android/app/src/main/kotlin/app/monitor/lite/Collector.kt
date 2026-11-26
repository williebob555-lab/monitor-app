package app.monitor.lite

import android.app.ActivityManager
import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.TrafficStats
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.PowerManager
import android.os.Process
import android.os.StatFs
import android.os.SystemClock
import android.provider.Settings
import org.json.JSONObject
import java.io.File
import java.net.ConnectException
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * The Android collector. A system alarm wakes [AlarmReceiver] once a minute
 * while the screen is on and every 5 minutes while it is off. Each run reads
 * a handful of system values, checks the other devices over Tailscale,
 * writes this device's files into the Syncthing folder and exits. Nothing
 * stays running in between: no service, no notification, no wake lock.
 *
 * File format is shared with the Dart code in lite/core (see lite/FORMAT.md).
 */
object Collector {
    const val SYNCTHING_PORT = 22000
    const val INTERVAL_SCREEN_ON = 60
    const val INTERVAL_SCREEN_OFF = 300
    const val BUDGET_RUN_MS = 5000L
    const val BUDGET_CPU_MS = 500L
    const val LOG_MAX_BYTES = 256 * 1024

    private val lock = Any()

    fun prefs(ctx: Context) = ctx.getSharedPreferences("monitor", Context.MODE_PRIVATE)

    fun deviceId(ctx: Context): String {
        val p = prefs(ctx)
        p.getString("id", null)?.let { return it }
        val b = ByteArray(6).also { SecureRandom().nextBytes(it) }
        val id = b.joinToString("") { "%02x".format(it) }
        p.edit().putString("id", id).apply()
        return id
    }

    fun defaultFolder(): String = File(Environment.getExternalStorageDirectory(), "Monitor").absolutePath

    fun folder(ctx: Context): String = prefs(ctx).getString("folder", null) ?: defaultFolder()

    fun deviceName(ctx: Context): String =
        prefs(ctx).getString("name", null)
            ?: Settings.Global.getString(ctx.contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    fun canWrite(): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.R || Environment.isExternalStorageManager()

    /** Schedule the next wake-up. Inexact and allowed while idle: Android may
     *  batch it with other wake-ups, which is what keeps battery use low. */
    fun schedule(ctx: Context, seconds: Int) {
        val am = ctx.getSystemService(AlarmManager::class.java)
        val pi = PendingIntent.getBroadcast(
            ctx, 1, Intent(ctx, AlarmReceiver::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        am.setAndAllowWhileIdle(
            AlarmManager.ELAPSED_REALTIME_WAKEUP,
            SystemClock.elapsedRealtime() + seconds * 1000L, pi,
        )
    }

    /** One collection run. Returns the seconds until the next one. */
    fun run(ctx: Context, reason: String): Int = synchronized(lock) { runLocked(ctx, reason) }

    private fun runLocked(ctx: Context, reason: String): Int {
        val started = SystemClock.elapsedRealtime()
        val cpu0 = Process.getElapsedCpuTime()
        val p = prefs(ctx)
        val id = deviceId(ctx)
        val pm = ctx.getSystemService(PowerManager::class.java)
        val screenOn = pm.isInteractive
        val next = if (screenOn) INTERVAL_SCREEN_ON else INTERVAL_SCREEN_OFF
        val root = File(folder(ctx))
        val dir = File(root, "devices/$id")
        if (!canWrite() || !(dir.isDirectory || dir.mkdirs())) {
            internalLog(ctx, "cannot write to ${dir.path} (all files access: ${canWrite()})")
            return next
        }
        try {
            val now = System.currentTimeMillis()
            val j = JSONObject()
            j.put("v", 1).put("id", id).put("name", deviceName(ctx)).put("kind", "android")
            j.put("t", now / 1000).put("next", next)
            val seq = p.getInt("seq", 0) + 1
            p.edit().putInt("seq", seq).apply()
            j.put("seq", seq)
            j.put("os", "Android ${Build.VERSION.RELEASE} (${Build.MANUFACTURER} ${Build.MODEL})")
            j.put("machine", id)
            j.put("app", BuildConfig.VERSION_NAME)
            j.put("boot", (now - SystemClock.elapsedRealtime()) / 1000)
            j.put("screen", if (screenOn) "on" else "off")

            // Battery: the sticky broadcast costs nothing to read.
            ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.let { b ->
                val level = b.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
                val scale = b.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
                val status = b.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
                val plugged = b.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)
                val temp = b.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE)
                val o = JSONObject()
                if (level >= 0 && scale > 0) o.put("pct", Math.round(1000.0 * level / scale) / 10.0)
                o.put("charging", status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL)
                o.put("plugged", plugged != 0)
                if (temp != Int.MIN_VALUE) o.put("tempC", temp / 10.0)
                j.put("battery", o)
            }
            j.put("cpuNote", "Android does not let apps read whole-device CPU use.")

            val mi = ActivityManager.MemoryInfo()
            ctx.getSystemService(ActivityManager::class.java).getMemoryInfo(mi)
            j.put("mem", JSONObject().put("used", mi.totalMem - mi.availMem).put("total", mi.totalMem))

            val sf = StatFs(Environment.getDataDirectory().path)
            j.put("disk", JSONObject().put("used", sf.totalBytes - sf.availableBytes).put("total", sf.totalBytes))

            // Network: rate from the total byte counters since the last run.
            val rx = TrafficStats.getTotalRxBytes()
            val tx = TrafficStats.getTotalTxBytes()
            val el = SystemClock.elapsedRealtime()
            val net = JSONObject()
            val prevEl = p.getLong("netEl", 0L)
            val secs = (el - prevEl) / 1000.0
            if (prevEl > 0 && secs in 1.0..3600.0 && rx >= p.getLong("netRx", 0) && tx >= p.getLong("netTx", 0)) {
                net.put("rxBps", Math.round((rx - p.getLong("netRx", 0)) / secs))
                net.put("txBps", Math.round((tx - p.getLong("netTx", 0)) / secs))
            }
            p.edit().putLong("netEl", el).putLong("netRx", rx).putLong("netTx", tx).apply()
            net.put("type", networkType(ctx))
            j.put("net", net)

            val ip = tailscaleIp()
            j.put("ts", JSONObject().put("ip", ip ?: JSONObject.NULL))
            j.put("st", JSONObject().put("up", tcp("127.0.0.1", 1000) == "open"))

            // What this device sees of the others.
            val peers = JSONObject()
            val others = File(root, "devices").listFiles()?.filter { it.isDirectory && it.name != id } ?: emptyList()
            val pool = Executors.newFixedThreadPool(4)
            val jobs = others.mapNotNull { d ->
                val o = runCatching { JSONObject(File(d, "now.json").readText()) }.getOrNull() ?: return@mapNotNull null
                val oid = o.optString("id", d.name)
                val seen = (now / 1000 - o.optLong("t", 0)).coerceAtLeast(0)
                val oip = o.optJSONObject("ts")?.optString("ip")?.takeIf { it.isNotEmpty() && it != "null" }
                oid to pool.submit<JSONObject> {
                    val v = JSONObject().put("seen", seen)
                    if (oip == null || ip == null) v.put("tcp", "noip")
                    else if (seen <= 86400) {
                        v.put("tcp", tcp(oip, 3000))
                        ping(oip)?.let { v.put("ping", it) }
                    }
                    v
                }
            }
            for ((oid, f) in jobs) runCatching { peers.put(oid, f.get(8, TimeUnit.SECONDS)) }
            pool.shutdownNow()
            j.put("peers", peers)

            val runMs = SystemClock.elapsedRealtime() - started
            val cpuMs = Process.getElapsedCpuTime() - cpu0
            val rssMb = rssMb()
            val over = mutableListOf<String>()
            if (runMs > BUDGET_RUN_MS) over += "run $runMs ms > $BUDGET_RUN_MS ms"
            if (cpuMs > BUDGET_CPU_MS) over += "cpu $cpuMs ms > $BUDGET_CPU_MS ms"
            val self = JSONObject().put("runMs", runMs).put("cpuMs", cpuMs)
            rssMb?.let { self.put("rssMb", Math.round(it * 10) / 10.0) }
            if (over.isNotEmpty()) self.put("over", org.json.JSONArray(over))
            j.put("self", self)

            val tmp = File(dir, ".syncthing.now.json.tmp") // Syncthing never syncs its own temp pattern
            tmp.writeText(j.toString())
            if (!tmp.renameTo(File(dir, "now.json"))) {
                File(dir, "now.json").writeText(j.toString())
                tmp.delete()
            }
            val history = History(dir, ctx.filesDir)
            history.add(JSONObject(historyLine(j)), now)

            if (over.isNotEmpty()) log(dir, "OVER BUDGET: ${over.joinToString("; ")}")
            val lastSummary = p.getLong("lastSummary", 0)
            if (now - lastSummary > 3600_000L || reason != "alarm") {
                val peerText = others.joinToString(" ") { d ->
                    val v = peers.optJSONObject(d.name)
                    "${d.name}:${v?.optString("tcp") ?: "-"}/${v?.opt("ping") ?: "-"}ms/seen${v?.opt("seen") ?: "-"}s"
                }
                log(dir, "ok reason=$reason run=${runMs}ms cpu=${cpuMs}ms rss=${rssMb?.let { "%.1f".format(it) }}MB " +
                    "screen=${if (screenOn) "on" else "off"} next=${next}s ts=${ip ?: "none"} " +
                    "st=${j.getJSONObject("st").getBoolean("up")} batteryExempt=${pm.isIgnoringBatteryOptimizations(ctx.packageName)} peers[$peerText]")
                p.edit().putLong("lastSummary", now).apply()
            }
            if (now - p.getLong("lastPrune", 0) > 6 * 3600_000L) {
                val used = history.prune(now, History.Retention.load(root))
                log(dir, "history ${used / 1024} KB")
                p.edit().putLong("lastPrune", now).apply()
            }
        } catch (e: Throwable) {
            log(dir, "ERROR run failed: $e ${e.stackTrace.take(3).joinToString(" | ")}")
        }
        return next
    }

    private fun networkType(ctx: Context): String {
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        var type = "none"
        for (n in cm.allNetworks) {
            val c = cm.getNetworkCapabilities(n) ?: continue
            if (c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
            when {
                c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> return "wifi"
                c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> type = "ethernet"
                c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> if (type == "none") type = "cellular"
            }
        }
        return type
    }

    fun tailscaleIp(): String? = runCatching {
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .firstOrNull { val b = it.address; (b[0].toInt() and 0xff) == 100 && (b[1].toInt() and 0xff) in 64..127 }
            ?.hostAddress
    }.getOrNull()

    /** open: Syncthing listening. refused: device up, Syncthing not. timeout: no route. */
    fun tcp(host: String, timeoutMs: Int): String = try {
        Socket().use { it.connect(InetSocketAddress(host, SYNCTHING_PORT), timeoutMs) }
        "open"
    } catch (e: ConnectException) {
        if (e.message?.contains("ECONNREFUSED") == true || e.message?.contains("refused", true) == true) "refused" else "timeout"
    } catch (e: SocketTimeoutException) {
        "timeout"
    } catch (e: Exception) {
        "error"
    }

    private fun ping(ip: String): Int? = runCatching {
        val pr = Runtime.getRuntime().exec(arrayOf("/system/bin/ping", "-c", "1", "-W", "2", ip))
        if (!pr.waitFor(4, TimeUnit.SECONDS)) { pr.destroy(); return null }
        if (pr.exitValue() != 0) return null
        val out = pr.inputStream.bufferedReader().readText()
        Regex("time[=<]([\\d.]+)").find(out)?.groupValues?.get(1)?.toDouble()?.let { Math.round(it).toInt() } ?: 0
    }.getOrNull()

    private fun rssMb(): Double? = runCatching {
        File("/proc/self/status").readLines().firstOrNull { it.startsWith("VmRSS:") }
            ?.split(Regex("\\s+"))?.get(1)?.toDouble()?.div(1024)
    }.getOrNull()


    private fun historyLine(j: JSONObject): String {
        val h = JSONObject().put("t", j.getLong("t"))
        j.optJSONObject("battery")?.let { b ->
            if (b.has("pct")) h.put("b", b.getDouble("pct"))
            h.put("c", if (b.optBoolean("charging")) 1 else 0)
        }
        if (j.has("screen")) h.put("s", if (j.getString("screen") == "on") 1 else 0)
        j.optJSONObject("mem")?.let { h.put("mem", Math.round(1000.0 * it.getLong("used") / it.getLong("total")) / 10.0) }
        j.optJSONObject("disk")?.let { h.put("disk", Math.round(1000.0 * it.getLong("used") / it.getLong("total")) / 10.0) }
        j.optJSONObject("net")?.let { n ->
            if (n.has("rxBps")) h.put("rx", n.getLong("rxBps"))
            if (n.has("txBps")) h.put("tx", n.getLong("txBps"))
        }
        val pv = JSONObject()
        j.optJSONObject("peers")?.let { ps ->
            for (k in ps.keys()) {
                val v = ps.getJSONObject(k)
                val ok = v.optString("tcp") == "open" || v.optString("tcp") == "refused" || v.has("ping")
                pv.put(k, if (ok) 1 else 0)
            }
        }
        h.put("p", pv)
        return h.toString()
    }

    fun log(dir: File, line: String) {
        runCatching {
            val f = File(dir, "log.txt")
            val stamp = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.US).format(Date())
            f.appendText("$stamp $line\n")
            if (f.length() > LOG_MAX_BYTES) {
                val t = f.readText()
                val cut = t.indexOf('\n', t.length / 2)
                f.writeText(t.substring(cut + 1))
            }
        }
    }

    /** Before the shared folder is usable, log inside the app's own storage. */
    fun internalLog(ctx: Context, line: String) = log(ctx.filesDir, line)

    fun logToFolder(ctx: Context, line: String) {
        val dir = File(folder(ctx), "devices/${deviceId(ctx)}")
        if (canWrite() && (dir.isDirectory || dir.mkdirs())) log(dir, line) else internalLog(ctx, line)
    }
}
