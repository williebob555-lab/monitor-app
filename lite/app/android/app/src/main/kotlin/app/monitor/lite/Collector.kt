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
 * stays running in between: no service, no ongoing notification, no wake lock.
 *
 * File format is shared with the Dart code in lite/core (see lite/FORMAT.md).
 */
object Collector {
    const val SYNCTHING_PORT = 22000
    const val INTERVAL_SCREEN_ON = 60
    const val INTERVAL_SCREEN_OFF = 300
    /** While someone has the app open on any device (lite/FORMAT.md, watch/). */
    const val INTERVAL_WATCHED = 15
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
        // CPU of this collection thread only. The whole-process figure
        // (Process.getElapsedCpuTime) also counts the app's screen when it is
        // open in the same process, which logged false "OVER BUDGET: cpu 4 s"
        // lines on the Pixel while Wilson looked at it (2026-11-26 22:42).
        val cpu0 = SystemClock.currentThreadTimeMillis()
        val p = prefs(ctx)
        val id = deviceId(ctx)
        val pm = ctx.getSystemService(PowerManager::class.java)
        val screenOn = pm.isInteractive
        val root = File(folder(ctx))
        val watchers = watchers(root, System.currentTimeMillis() / 1000)
        val plugged = (ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            ?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
        // Fast only when it costs little: screen on, or on the charger.
        val next = when {
            watchers.isNotEmpty() && (screenOn || plugged) -> INTERVAL_WATCHED
            screenOn -> INTERVAL_SCREEN_ON
            else -> INTERVAL_SCREEN_OFF
        }
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
            if (watchers.isNotEmpty()) j.put("watch", org.json.JSONArray(watchers))
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
            j.put("readings", readings(ctx))

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
            val pingPool = Executors.newFixedThreadPool(4)
            val jobs = others.mapNotNull { d ->
                val o = runCatching { JSONObject(File(d, "now.json").readText()) }.getOrNull() ?: return@mapNotNull null
                val oid = o.optString("id", d.name)
                val seen = (now / 1000 - o.optLong("t", 0)).coerceAtLeast(0)
                val oip = o.optJSONObject("ts")?.optString("ip")?.takeIf { it.isNotEmpty() && it != "null" }
                oid to pool.submit<JSONObject> {
                    val v = JSONObject().put("seen", seen)
                    if (oip == null || ip == null) v.put("tcp", "noip")
                    else if (seen <= 86400) {
                        // Ping alongside the connect: an unreachable device costs one timeout, not two.
                        val pg = pingPool.submit<Int?> { ping(oip) }
                        v.put("tcp", tcp(oip, 3000))
                        runCatching { pg.get(4, TimeUnit.SECONDS) }.getOrNull()?.let { v.put("ping", it) }
                    }
                    v
                }
            }
            for ((oid, f) in jobs) runCatching { peers.put(oid, f.get(8, TimeUnit.SECONDS)) }
            pool.shutdownNow()
            pingPool.shutdownNow()
            j.put("peers", peers)
            // Where this phone sits in the hierarchy: a top-level device with
            // nothing attached that it can see.
            j.put("node", JSONObject().put("id", j.getString("id")).put("type", "device")
                .put("parent", JSONObject.NULL).put("children", org.json.JSONArray()).put("links", org.json.JSONArray()))
            // "Update now" from any device. Android installs nothing without a
            // tap on this phone, so asking shows a notification here instead.
            j.put("controls", org.json.JSONArray().put(JSONObject().put("key", "update").put("label", "Monitor App")
                .put("options", org.json.JSONArray().put("update")).put("value", BuildConfig.VERSION_NAME)
                .put("note", "Android installs only with a tap on the phone itself: asking puts a notification there.")
                .put("confirm", "Android needs a tap on ${deviceName(ctx)} itself: a notification appears there; tap it, then Update at the top.")))
            runCatching { answerRequests(ctx, root, id, dir, now / 1000) }
                .onFailure { log(dir, "ERROR requests: $it") }

            val runMs = SystemClock.elapsedRealtime() - started
            val cpuMs = SystemClock.currentThreadTimeMillis() - cpu0
            val rssMb = rssMb()
            val over = mutableListOf<String>()
            if (runMs > BUDGET_RUN_MS) over += "run $runMs ms > $BUDGET_RUN_MS ms"
            if (cpuMs > BUDGET_CPU_MS) over += "cpu $cpuMs ms > $BUDGET_CPU_MS ms"
            val self = JSONObject().put("runMs", runMs).put("cpuMs", cpuMs)
            rssMb?.let { self.put("rssMb", Math.round(it * 10) / 10.0) }
            if (over.isNotEmpty()) self.put("over", org.json.JSONArray(over))
            j.put("self", self)

            // Written in place, then its time set explicitly. On Android a file
            // renamed over now.json kept the first write's modification time
            // (seen on the Pixel and the tablet, 2026-11-26), and Syncthing,
            // which looks at time and size, then skipped every same-size
            // update. Readers keep the previous report if they catch a half
            // write, and Syncthing re-scans a file that changes while it reads.
            val out = File(dir, "now.json")
            out.writeText(j.toString())
            out.setLastModified(System.currentTimeMillis())
            File(dir, ".syncthing.now.json.tmp").delete()
            val history = History(dir, ctx.filesDir)
            // History stays at one line a minute however fast the live rate is.
            if (now - p.getLong("lastHistory", 0) >= 55_000L) {
                history.add(JSONObject(historyLine(j)), now)
                p.edit().putLong("lastHistory", now).apply()
            }

            runCatching { RuleNotices.run(ctx, root, now / 1000) }
                .onFailure { log(dir, "ERROR notifications check: $it") }

            if (over.isNotEmpty()) log(dir, "OVER BUDGET: ${over.joinToString("; ")}")
            val lastSummary = p.getLong("lastSummary", 0)
            if (now - lastSummary > 3600_000L || reason == "app-open") {
                val peerText = others.joinToString(" ") { d ->
                    val v = peers.optJSONObject(d.name)
                    "${d.name}:${v?.optString("tcp") ?: "-"}/${v?.opt("ping") ?: "-"}ms/seen${v?.opt("seen") ?: "-"}s"
                }
                log(dir, "ok reason=$reason run=${runMs}ms cpu=${cpuMs}ms rss=${rssMb?.let { "%.1f".format(it) }}MB " +
                    "screen=${if (screenOn) "on" else "off"} next=${next}s ts=${ip ?: "none"} " +
                    "st=${j.getJSONObject("st").getBoolean("up")} batteryExempt=${pm.isIgnoringBatteryOptimizations(ctx.packageName)} peers[$peerText]")
                p.edit().putLong("lastSummary", now).apply()
                Updater.ensureScheduled(ctx)
            }
            if (now - p.getLong("lastPrune", 0) > 6 * 3600_000L) {
                val used = history.prune(now, History.Retention.load(root))
                log(dir, "history ${used / 1024} KB")
                runCatching { Patterns.write(dir, others, now) }.onFailure { log(dir, "ERROR patterns: $it") }
                p.edit().putLong("lastPrune", now).apply()
            }
        } catch (e: Throwable) {
            log(dir, "ERROR run failed: $e ${e.stackTrace.take(3).joinToString(" | ")}")
        }
        return next
    }

    /** Requests other devices left for this one (lite/core control.dart):
     *  devices/<from>/requests/<id>.json, answered once in results/<id>.json
     *  within ten minutes. Only "update" is offered here. */
    private fun answerRequests(ctx: Context, root: File, me: String, dir: File, nowS: Long) {
        val results = File(dir, "results")
        results.listFiles()?.forEach { if (nowS * 1000 - it.lastModified() > 86_400_000L) it.delete() }
        File(dir, "requests").listFiles()?.forEach { if (nowS * 1000 - it.lastModified() > 86_400_000L) it.delete() }
        val devices = File(root, "devices").listFiles() ?: return
        // settings.json "controls": {"allow": [ids]}; missing = every device.
        val allow = runCatching { JSONObject(File(root, "settings.json").readText()).optJSONObject("controls")?.optJSONArray("allow") }.getOrNull()
            ?.let { a -> (0 until a.length()).map { a.optString(it) }.toSet() }
        for (d in devices) {
            val files = File(d, "requests").listFiles() ?: continue
            for (f in files) {
                if (!f.name.endsWith(".json") || f.name.contains(".sync-conflict") || f.length() > 16 * 1024) continue
                val q = runCatching { JSONObject(f.readText()) }.getOrNull() ?: continue
                val rid = q.optString("id")
                if (q.optString("host") != me || !rid.matches(Regex("[0-9a-f]{8,64}"))) continue
                // A device asks only from its own folder, and only if allowed.
                if (q.optString("from") != d.name || (allow != null && d.name !in allow)) continue
                if (!q.optString("key").matches(Regex("[a-z0-9._-]{1,64}")) || q.optString("value").length > 256) continue
                if (Math.abs(nowS - q.optLong("t")) > 600) continue
                val out = File(results, "$rid.json")
                if (out.exists()) continue
                val from = q.optString("fromName", q.optString("from"))
                val ok = q.optString("key") == "update"
                val msg = if (ok) "notification shown; installing needs a tap on ${deviceName(ctx)}" else "this device has no setting ${q.optString("key")}"
                if (ok) Alerts.post(ctx, "update", 9, from, "Update asked from $from",
                    "Open Monitor App and tap Update at the top to install the newest version. Android needs your tap for this.")
                results.mkdirs()
                out.writeText(JSONObject().put("id", rid).put("ok", ok).put("message", msg).put("t", nowS).toString())
                log(dir, "${if (ok) "control" else "ERROR control"} ${q.optString("key")}=${q.optString("value")} for ${q.optString("target")}, asked by $from: $msg")
                if (ok) event(ctx, "control.applied", q.optString("target"), "${q.optString("key")}, asked by $from: $msg")
            }
        }
    }

    /** Extra readings (lite/FORMAT.md "readings"): what the device is, its
     *  battery's health and the Wi-Fi signal. None needs a permission. */
    private fun readings(ctx: Context): org.json.JSONArray {
        val a = org.json.JSONArray()
        fun add(key: String, label: String, value: Any, group: String, unit: String? = null, level: String? = null, note: String? = null) {
            val o = JSONObject().put("key", key).put("label", label).put("value", value).put("group", group)
            unit?.let { o.put("unit", it) }
            note?.let { o.put("note", it) }
            level?.let { o.put("level", it) }
            a.put(o)
        }
        add("sys.maker", "Maker", Build.MANUFACTURER.replaceFirstChar { it.uppercase() }, "System")
        runCatching { Location.reading(ctx, System.currentTimeMillis()) }.getOrNull()?.let { a.put(it) }
        add("sys.model", "Model", Build.MODEL, "System")
        if (Build.VERSION.SDK_INT >= 31 && Build.SOC_MODEL.isNotBlank() && Build.SOC_MODEL != "unknown") add("sys.cpu", "Processor", Build.SOC_MODEL, "System")
        add("sys.cores", "Processor threads", Runtime.getRuntime().availableProcessors().toString(), "System")
        ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.let { b ->
            val h = when (b.getIntExtra(BatteryManager.EXTRA_HEALTH, 0)) {
                BatteryManager.BATTERY_HEALTH_GOOD -> "good"
                BatteryManager.BATTERY_HEALTH_OVERHEAT -> "overheating"
                BatteryManager.BATTERY_HEALTH_DEAD -> "dead"
                BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE -> "over voltage"
                BatteryManager.BATTERY_HEALTH_COLD -> "cold"
                BatteryManager.BATTERY_HEALTH_UNSPECIFIED_FAILURE -> "failure"
                else -> null
            }
            h?.let { add("pow.health", "Battery health", it, "Power", level = if (it == "good") null else "warn") }
            val mv = b.getIntExtra(BatteryManager.EXTRA_VOLTAGE, -1)
            if (mv > 0) add("pow.volts", "Battery voltage", Math.round(mv / 10.0) / 100.0, "Power", unit = "V")
            if (Build.VERSION.SDK_INT >= 34) {
                val c = b.getIntExtra(BatteryManager.EXTRA_CYCLE_COUNT, -1)
                if (c >= 0) add("pow.cycles", "Charge cycles", c, "Power")
            }
        }
        if (Build.VERSION.SDK_INT >= 29) runCatching {
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            for (n in cm.allNetworks) {
                val c = cm.getNetworkCapabilities(n) ?: continue
                if (!c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || c.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
                val dbm = c.signalStrength
                if (dbm != Int.MIN_VALUE && dbm < 0) {
                    add("wifi.dbm", "Wi-Fi signal", dbm, "Wi-Fi", unit = "dBm", level = if (dbm < -80) "warn" else null)
                }
                break
            }
        }
        // Tailscale, as far as Android lets another app see it: a VPN
        // network is up and this phone has a Tailscale (100.64/10) address.
        runCatching {
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val vpn = cm.allNetworks.any { cm.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true }
            val ts = tailscaleIp() != null
            add("ts.state", "State", if (ts) "Running" else if (vpn) "VPN up, no Tailscale address" else "Stopped", "Tailscale",
                level = if (ts) null else "fault")
        }
        // Syncthing on this phone: its sync port answers locally. Peer and
        // folder counts need Syncthing-Fork's API key, which another app
        // cannot read (see ROADMAP row 9).
        runCatching {
            val up = tcp("127.0.0.1", 300) == "open"
            add("st.state", "State", if (up) "Running" else "Not running", "Syncthing", level = if (up) null else "fault")
        }
        // Which layer of the network works (same keys as computers, see
        // core netcheck.dart): router, this network's DNS, public DNS, internet.
        runCatching {
            val cm = ctx.getSystemService(ConnectivityManager::class.java)
            val under = cm.allNetworks.firstOrNull { n -> cm.getNetworkCapabilities(n)?.let {
                !it.hasTransport(NetworkCapabilities.TRANSPORT_VPN) && it.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) } == true }
            val gw = under?.let { cm.getLinkProperties(it) }?.routes?.firstOrNull { it.isDefaultRoute && it.gateway is Inet4Address }?.gateway?.hostAddress
            val dns = cm.getLinkProperties(cm.activeNetwork)?.dnsServers?.filterIsInstance<Inet4Address>()?.mapNotNull { it.hostAddress }.orEmpty().take(3)
            val pool = Executors.newFixedThreadPool(4)
            try {
                val g = gw?.let { h -> pool.submit<Long?> { listOf(53, 80, 443).firstNotNullOfOrNull { reachMs(h, it) } } }
                val d = pool.submit<Pair<String, Long>?> { dns.firstNotNullOfOrNull { s -> dnsMs(s)?.let { s to it } } }
                val p = pool.submit<Pair<String, Long>?> { listOf("1.1.1.1", "9.9.9.9").firstNotNullOfOrNull { s -> dnsMs(s)?.let { s to it } } }
                val i = pool.submit<Long?> { listOf("1.1.1.1", "9.9.9.9").firstNotNullOfOrNull { h -> reachMs(h, 443, openOnly = true) } }
                if (gw != null) { val r = g!!.get(); add("net.gateway", "Router", if (r == null) "no answer" else "answers", "Network",
                    level = if (r == null) "warn" else null, note = if (r == null) gw else "$gw, $r ms") }
                d.get().let { add("net.dns", "DNS (this network's)", if (it == null) "no answer" else "answers", "Network",
                    level = if (it == null) "warn" else null, note = if (it == null) dns.joinToString(", ").ifEmpty { "none set" } else "${it.first}, ${it.second} ms") }
                p.get().let { add("net.dnsDirect", "DNS (public)", if (it == null) "no answer" else "answers", "Network",
                    note = if (it == null) "1.1.1.1, 9.9.9.9" else "${it.first}, ${it.second} ms") }
                i.get().let { add("net.internet", "Internet", if (it == null) "no" else "reachable", "Network",
                    level = if (it == null) "warn" else null, note = if (it == null) "no answer from 1.1.1.1 or 9.9.9.9" else "$it ms") }
            } finally { pool.shutdown() }
            val ifs = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().filter { it.isUp && !it.isLoopback }
                .flatMap { n -> n.inetAddresses.toList().filterIsInstance<Inet4Address>().map { "${n.name} ${it.hostAddress}" } }
            if (ifs.isNotEmpty()) add("net.ifaces", "Interfaces", ifs.joinToString(", "), "Network")
        }
        // Storage per volume (phone storage, SD card, USB drive), only when
        // there is more than one: one volume is already "Storage".
        if (Build.VERSION.SDK_INT >= 30) runCatching {
            val sm = ctx.getSystemService(android.os.storage.StorageManager::class.java)
            val vols = sm.storageVolumes.mapNotNull { v ->
                val d = v.directory ?: return@mapNotNull null
                val st = android.os.StatFs(d.path)
                if (st.totalBytes < (1L shl 30)) null else Triple(v.getDescription(ctx), st.totalBytes, st.availableBytes)
            }
            if (vols.size > 1) for ((name, total, free) in vols) {
                val used = (total - free).toDouble() / total
                val gb = if (total >= (1L shl 40)) "%.1f TB".format(total / (1L shl 40).toDouble()) else "${total / (1L shl 30)} GB"
                add("drive.${name.replace(Regex("[^A-Za-z0-9]+"), "_")}", "$name ($gb)", Math.round(used * 1000) / 10.0, "Drives", unit = "%",
                    level = if (used >= 0.95) "fault" else if (used >= 0.9) "warn" else null)
            }
        }
        return a
    }

    /** Names of devices whose app is open now: fresh files in <folder>/watch/. */
    fun watchers(root: File, nowS: Long): List<String> =
        File(root, "watch").listFiles()?.filter { it.isFile && it.name.endsWith(".json") && !it.name.startsWith(".syncthing.") }
            ?.mapNotNull { f ->
                runCatching { JSONObject(f.readText()) }.getOrNull()
                    ?.takeIf { it.optLong("until", 0) > nowS }
                    ?.let { it.optString("name", it.optString("id", "?")) }
            } ?: emptyList()

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

    /** This phone's Tailscale address. 100.64/10 is also the carrier's
     *  CGNAT range: the Pixel had rmnet16 100.86.5.58 (carrier) next to
     *  tun0 100.88.178.60 (Tailscale) and used to publish the carrier's, so
     *  no device could reach it. The VPN interface wins; cellular never. */
    fun tailscaleIp(): String? = runCatching {
        val found = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().flatMap { i ->
            i.inetAddresses.toList().filterIsInstance<Inet4Address>()
                .filter { val b = it.address; (b[0].toInt() and 0xff) == 100 && (b[1].toInt() and 0xff) in 64..127 }
                .map { i.name to it.hostAddress }
        }
        val cell = Regex("^(rmnet|ccmni|pdp|wwan|clat|v4-rmnet|seth|ccinet)")
        (found.firstOrNull { it.first.startsWith("tun") || it.first.contains("tailscale", true) }
            ?: found.firstOrNull { !cell.containsMatchIn(it.first) })?.second
    }.getOrNull()

    /** ms to reach [host]:[port], counting a refusal as reached unless
     *  [openOnly]; null when nothing answers within 2 s. */
    fun reachMs(host: String, port: Int, openOnly: Boolean = false): Long? {
        val t0 = SystemClock.elapsedRealtime()
        return try {
            Socket().use { it.connect(InetSocketAddress(host, port), 2000) }
            SystemClock.elapsedRealtime() - t0
        } catch (e: ConnectException) {
            if (!openOnly && e.message?.contains("refused", true) == true) SystemClock.elapsedRealtime() - t0 else null
        } catch (e: Exception) { null }
    }

    /** One A query for a random name under example.com (no cache can answer
     *  it). NOERROR or NXDOMAIN: the resolver works. ms or null. */
    fun dnsMs(server: String): Long? = runCatching {
        val id = SecureRandom().nextInt(0x10000)
        val name = "monitor-${java.lang.Long.toHexString(SecureRandom().nextInt().toLong() and 0xffffffffL)}.example.com"
        val q = java.io.ByteArrayOutputStream()
        q.write(byteArrayOf((id shr 8).toByte(), id.toByte(), 1, 0, 0, 1, 0, 0, 0, 0, 0, 0))
        for (l in name.split('.')) { q.write(l.length); q.write(l.toByteArray()) }
        q.write(byteArrayOf(0, 0, 1, 0, 1))
        val out = q.toByteArray()
        java.net.DatagramSocket().use { s ->
            s.soTimeout = 2000
            val t0 = SystemClock.elapsedRealtime()
            s.send(java.net.DatagramPacket(out, out.size, java.net.InetAddress.getByName(server), 53))
            val buf = ByteArray(512)
            var rc = -1
            while (rc < 0) {
                val p = java.net.DatagramPacket(buf, buf.size)
                s.receive(p) // throws on timeout
                if (p.length >= 12 && (((buf[0].toInt() and 0xff) shl 8) or (buf[1].toInt() and 0xff)) == id && (buf[2].toInt() and 0x80) != 0)
                    rc = buf[3].toInt() and 0x0f
            }
            if (rc == 0 || rc == 3) SystemClock.elapsedRealtime() - t0 else null
        }
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
        // Pattern conditions (core report.dart historyLine): app open somewhere, network type.
        if ((j.optJSONArray("watch")?.length() ?: 0) > 0) h.put("w", 1)
        when (j.optJSONObject("net")?.optString("type")) {
            "wifi" -> h.put("nt", 1); "cellular" -> h.put("nt", 2); "ethernet" -> h.put("nt", 3); "none" -> h.put("nt", 0)
        }
        j.optJSONArray("readings")?.let { rs ->
            for (i in 0 until rs.length()) {
                val r = rs.optJSONObject(i) ?: continue
                val v = r.opt("value")
                if (v is Number) h.put(r.getString("key"), v)
            }
        }
        return h.toString()
    }

    fun log(dir: File, line: String) {
        runCatching {
            val f = File(dir, "log.txt")
            val stamp = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.US).format(Date())
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

    /** devices/<id>/events/YYYY-MM.jsonl (UTC month), FORMAT.md "Events". */
    fun event(ctx: Context, type: String, subject: String? = null, detail: String? = null) {
        runCatching {
            val dir = File(folder(ctx), "devices/${deviceId(ctx)}/events")
            if (!canWrite() || !(dir.isDirectory || dir.mkdirs())) return
            val fmt = SimpleDateFormat("yyyy-MM", Locale.US).apply { timeZone = java.util.TimeZone.getTimeZone("UTC") }
            val j = JSONObject().put("t", System.currentTimeMillis() / 1000).put("type", type)
            subject?.let { j.put("subject", it) }
            detail?.let { j.put("detail", it) }
            File(dir, "${fmt.format(Date())}.jsonl").appendText(j.toString() + "\n")
        }
    }

    fun logToFolder(ctx: Context, line: String) {
        val dir = File(folder(ctx), "devices/${deviceId(ctx)}")
        if (canWrite() && (dir.isDirectory || dir.mkdirs())) log(dir, line) else internalLog(ctx, line)
    }
}
