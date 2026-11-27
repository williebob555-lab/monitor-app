package app.monitor.lite

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File

class MainActivity : FlutterActivity() {
    private val main = Handler(Looper.getMainLooper())

    companion object {
        /** The activity while it is on screen: Android's install
         *  confirmation is started from here, where a foreground app may
         *  open it (from the install broadcast it was silently blocked). */
        @Volatile var resumed: java.lang.ref.WeakReference<MainActivity>? = null
    }

    override fun onResume() {
        super.onResume()
        resumed = java.lang.ref.WeakReference(this)
    }

    override fun onPause() {
        if (resumed?.get() === this) resumed = null
        super.onPause()
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        Migration.run(applicationContext)
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
                "installApk" -> Thread {
                    val r = runCatching { Installer.install(ctx, File(call.argument<String>("path")!!)) }
                    r.exceptionOrNull()?.let { Collector.logToFolder(ctx, "ERROR install could not start: $it") }
                    main.post {
                        if (r.getOrNull() == "permission" && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                            open(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
                        }
                        r.fold({ result.success(it) }, { result.error("install", it.toString(), null) })
                    }
                }.start()
                "notify" -> result.success(
                    mapOf("ids" to Alerts.ids(ctx).toList(), "enabled" to Alerts.enabled(ctx)),
                )
                "setNotify" -> {
                    val on = call.argument<Boolean>("on") == true
                    Alerts.set(ctx, call.argument<String>("id")!!, on)
                    if (on && Build.VERSION.SDK_INT >= 33 &&
                        checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                    ) {
                        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 7)
                    }
                    result.success(null)
                }
                "location" -> result.success(mapOf("on" to Location.on(ctx), "granted" to Location.granted(ctx)))
                "setLocation" -> {
                    val on = call.argument<Boolean>("on") == true
                    Location.set(ctx, on)
                    if (on && !Location.granted(ctx)) requestPermissions(arrayOf(Manifest.permission.ACCESS_COARSE_LOCATION), 8)
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
