package app.monitor.lite

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.PowerManager
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Automatic updates with the app closed (settings.json "update.auto", on
 * unless set to false; lite/core update.dart is the same rule for Windows).
 *
 * A JobScheduler job runs every few hours on an unmetered network with the
 * battery not low. It looks for a newer lite-v release, downloads the APK,
 * checks it against the release's SHA256SUMS and hands it to
 * PackageInstaller asking for no tap (Android 12+). Android grants that only
 * when this app installed the version that is running now; otherwise it
 * answers "needs your confirmation" and a notification asks for one tap,
 * after which later updates go through on their own. It installs only while
 * the screen is off, so nobody's open app closes under them.
 */
object Updater {
    private const val JOB_ID = 4711
    private const val REPO = "williebob555-lab/monitor-app"
    private const val PREFIX = "lite-v"
    private const val PERIOD_MS = 3 * 3600_000L

    fun ensureScheduled(ctx: Context) {
        runCatching {
            val js = ctx.getSystemService(JobScheduler::class.java)
            if (js.getPendingJob(JOB_ID) != null) return
            val job = JobInfo.Builder(JOB_ID, ComponentName(ctx, UpdateJob::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_UNMETERED)
                .setRequiresBatteryNotLow(true)
                .setPeriodic(PERIOD_MS)
                .setPersisted(true)
                .build()
            js.schedule(job)
        }.onFailure { Collector.logToFolder(ctx, "ERROR update job not scheduled: $it") }
    }

    fun autoOn(root: File): Boolean = runCatching {
        JSONObject(File(root, "settings.json").readText()).optJSONObject("update")?.opt("auto") != false
    }.getOrDefault(true)

    /** -1, 0, 1 like compareTo, on dotted numbers ("2.6.10" > "2.6.9"). */
    fun compare(a: String, b: String): Int {
        val x = a.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
        val y = b.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val c = (x.getOrElse(i) { 0 }).compareTo(y.getOrElse(i) { 0 })
            if (c != 0) return c
        }
        return 0
    }

    private fun get(url: String, accept: String? = null): HttpURLConnection {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 15_000
        c.readTimeout = 60_000
        c.instanceFollowRedirects = true
        c.setRequestProperty("User-Agent", "monitor-lite")
        accept?.let { c.setRequestProperty("Accept", it) }
        if (c.responseCode != 200) throw IllegalStateException("$url answered ${c.responseCode}")
        return c
    }

    private fun installer(ctx: Context): String = runCatching {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R)
            ctx.packageManager.getInstallSourceInfo(ctx.packageName).installingPackageName ?: "unknown"
        else ctx.packageManager.getInstallerPackageName(ctx.packageName) ?: "unknown"
    }.getOrDefault("unknown")

    /** One check; returns what happened, for the log. */
    fun checkAndInstall(ctx: Context): String {
        val root = File(Collector.folder(ctx))
        if (!autoOn(root)) return "automatic updates are off in settings.json"
        val current = BuildConfig.VERSION_NAME
        val list = JSONArray(get("https://api.github.com/repos/$REPO/releases?per_page=30", "application/vnd.github+json")
            .inputStream.bufferedReader().use { it.readText() })
        var best: JSONObject? = null
        var bestV: String? = null
        for (i in 0 until list.length()) {
            val r = list.optJSONObject(i) ?: continue
            if (r.optBoolean("draft")) continue
            val tag = r.optString("tag_name")
            if (!tag.startsWith(PREFIX)) continue
            val v = tag.removePrefix(PREFIX)
            if (bestV == null || compare(v, bestV) > 0) { best = r; bestV = v }
        }
        if (best == null || bestV == null || compare(bestV, current) <= 0) return "up to date ($current)"
        val want = "monitor-app-$bestV.apk"
        var url: String? = null
        var sums: String? = null
        val assets = best.optJSONArray("assets") ?: JSONArray()
        for (i in 0 until assets.length()) {
            val a = assets.optJSONObject(i) ?: continue
            if (a.optString("name") == want) url = a.optString("browser_download_url")
            if (a.optString("name") == "SHA256SUMS") sums = a.optString("browser_download_url")
        }
        if (url == null || sums == null) return "$bestV has no $want or SHA256SUMS yet"
        val sha = get(sums).inputStream.bufferedReader().use { it.readText() }.lines()
            .map { it.trim().split(Regex("\\s+\\*?")) }.firstOrNull { it.size == 2 && it[1] == want }?.get(0)?.lowercase()
            ?: return "$bestV: $want is not in SHA256SUMS"
        if (ctx.getSystemService(PowerManager::class.java).isInteractive) return "$bestV found; waiting for the screen to be off"
        val f = File(ctx.cacheDir, want)
        val md = MessageDigest.getInstance("SHA-256")
        get(url).inputStream.use { input ->
            f.outputStream().use { out ->
                val buf = ByteArray(64 * 1024)
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    md.update(buf, 0, n)
                    out.write(buf, 0, n)
                }
            }
        }
        val got = md.digest().joinToString("") { "%02x".format(it) }
        if (got != sha) {
            f.delete()
            return "ERROR $want did not match SHA256SUMS; deleted"
        }
        Collector.logToFolder(ctx, "update: installing $bestV automatically (installed by ${installer(ctx)})")
        val r = Installer.install(ctx, f, auto = true)
        return if (r == "started") "handed $bestV to Android" else "$bestV not installed: Install unknown apps is off for Monitor App"
    }
}

class UpdateJob : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        val ctx = applicationContext
        Thread {
            val msg = runCatching { Updater.checkAndInstall(ctx) }.getOrElse { "ERROR update check: $it" }
            Collector.logToFolder(ctx, "update: $msg")
            jobFinished(params, false)
        }.start()
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean = false
}
