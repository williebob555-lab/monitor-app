package app.monitor.lite

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import androidx.core.content.FileProvider
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File

class MainActivity : FlutterActivity() {
    private val main = Handler(Looper.getMainLooper())

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "monitor/android").setMethodCallHandler { call, result ->
            val ctx = applicationContext
            when (call.method) {
                "config" -> result.success(
                    mapOf(
                        "id" to Collector.deviceId(ctx),
                        "folder" to Collector.folder(ctx),
                        "name" to Collector.deviceName(ctx),
                        "version" to BuildConfig.VERSION_NAME,
                        "cacheDir" to cacheDir.absolutePath,
                        "filesDir" to filesDir.absolutePath,
                    ),
                )
                "setFolder" -> {
                    Collector.prefs(ctx).edit().putString("folder", call.argument<String>("folder")).apply()
                    result.success(null)
                }
                "setName" -> {
                    Collector.prefs(ctx).edit().putString("name", call.argument<String>("name")).apply()
                    result.success(null)
                }
                "permissions" -> {
                    val pm = getSystemService(PowerManager::class.java)
                    result.success(
                        mapOf(
                            "allFiles" to Collector.canWrite(),
                            "battery" to pm.isIgnoringBatteryOptimizations(packageName),
                            "syncthing" to syncthingPackage(),
                        ),
                    )
                }
                "openAllFiles" -> {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                        open(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName")))
                    }
                    result.success(null)
                }
                "openBattery" -> {
                    open(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
                    result.success(null)
                }
                "openSyncthing" -> {
                    syncthingPackage()?.let { p -> packageManager.getLaunchIntentForPackage(p)?.let { open(it) } }
                    result.success(null)
                }
                "runNow" -> Thread {
                    val next = Collector.run(ctx, call.argument<String>("reason") ?: "app")
                    Collector.schedule(ctx, next)
                    main.post { result.success(next) }
                }.start()
                "installApk" -> {
                    val f = File(call.argument<String>("path")!!)
                    val uri = FileProvider.getUriForFile(ctx, "$packageName.files", f)
                    open(
                        Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
                    )
                    result.success(null)
                }
                "log" -> {
                    Collector.logToFolder(ctx, call.argument<String>("line") ?: "")
                    result.success(null)
                }
                else -> result.notImplemented()
            }
        }
    }

    private fun open(i: Intent) {
        runCatching { startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            .onFailure { Collector.logToFolder(applicationContext, "could not open ${i.action}: $it") }
    }

    private fun syncthingPackage(): String? = listOf(
        "com.github.catfriend1.syncthingandroid", // Syncthing-Fork
        "research.syncthing.android",
        "com.nutomic.syncthingandroid",
    ).firstOrNull {
        @Suppress("DEPRECATION") runCatching { packageManager.getPackageInfo(it, 0); true }.getOrDefault(false)
    }
}
