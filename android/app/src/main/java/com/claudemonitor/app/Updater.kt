package com.claudemonitor.app

import android.app.Activity
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.provider.Settings
import android.widget.Toast
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import kotlin.concurrent.thread

// Checks the latest GitHub release and installs its APK through PackageInstaller.
object Updater {
    private const val REPO = "carellice/claude-monitor"
    private const val ASSET = "ClaudeMonitor.apk"
    const val ACTION_INSTALL_STATUS = "com.claudemonitor.app.INSTALL_STATUS"

    class Release(val version: String, val url: String)

    @Volatile var available: Release? = null
        private set
    @Volatile var downloading = false
        private set
    private var lastCheck = 0L

    fun currentVersion(ctx: Context): String = ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: "0"

    private fun parse(v: String) = v.trimStart('v').split(".").map { it.toIntOrNull() ?: 0 }

    private fun newer(a: String, b: String): Boolean {
        val x = parse(a)
        val y = parse(b)
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = x.getOrElse(i) { 0 } - y.getOrElse(i) { 0 }
            if (d != 0) return d > 0
        }
        return false
    }

    // At most every 6 hours unless forced; the result is published through Bus.
    fun checkAsync(ctx: Context, force: Boolean = false, done: ((Release?, String?) -> Unit)? = null) {
        val now = System.currentTimeMillis()
        if (!force && now - lastCheck < 6 * 3_600_000L) return
        lastCheck = now
        val app = ctx.applicationContext
        thread {
            var error: String? = null
            try {
                val c = URL("https://api.github.com/repos/$REPO/releases/latest").openConnection() as HttpURLConnection
                c.setRequestProperty("User-Agent", "claude-monitor-android/" + currentVersion(app))
                c.setRequestProperty("Accept", "application/vnd.github+json")
                c.connectTimeout = 15000
                c.readTimeout = 20000
                if (c.responseCode != 200) throw Exception("GitHub HTTP ${c.responseCode}")
                val d = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                val tag = d.getString("tag_name").trimStart('v')
                val assets = d.optJSONArray("assets")
                var found: Release? = null
                if (newer(tag, currentVersion(app)) && assets != null)
                    for (i in 0 until assets.length()) {
                        val a = assets.getJSONObject(i)
                        if (a.getString("name") == ASSET) found = Release(tag, a.getString("browser_download_url"))
                    }
                available = found
            } catch (e: Exception) {
                error = e.message ?: "errore"
            }
            Bus.post {
                Bus.listener?.invoke()
                done?.invoke(available, error)
            }
        }
    }

    fun install(activity: Activity) {
        val r = available ?: return
        if (downloading) return
        if (!activity.packageManager.canRequestPackageInstalls()) {
            Toast.makeText(activity, "Consenti l'installazione di app da Claude Monitor, poi riprova", Toast.LENGTH_LONG).show()
            activity.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.packageName)))
            return
        }
        downloading = true
        Bus.listener?.invoke()
        val app = activity.applicationContext
        thread {
            var error: String? = null
            try {
                val file = File(app.cacheDir, "update.apk")
                val c = URL(r.url).openConnection() as HttpURLConnection
                c.connectTimeout = 15000
                c.readTimeout = 60000
                c.inputStream.use { input -> file.outputStream().use { input.copyTo(it) } }

                val installer = app.packageManager.packageInstaller
                val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
                val session = installer.openSession(installer.createSession(params))
                session.use { s ->
                    s.openWrite("ClaudeMonitor.apk", 0, file.length()).use { out ->
                        file.inputStream().use { it.copyTo(out) }
                        s.fsync(out)
                    }
                    // The system calls MainActivity back with the status (and the confirmation screen to show).
                    val status = PendingIntent.getActivity(app, 3,
                        Intent(app, MainActivity::class.java).setAction(ACTION_INSTALL_STATUS),
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
                    s.commit(status.intentSender)
                }
            } catch (e: Exception) {
                error = e.message ?: "errore"
            }
            downloading = false
            Bus.post {
                Bus.listener?.invoke()
                if (error != null) Toast.makeText(app, "Aggiornamento non riuscito: $error", Toast.LENGTH_LONG).show()
            }
        }
    }
}
