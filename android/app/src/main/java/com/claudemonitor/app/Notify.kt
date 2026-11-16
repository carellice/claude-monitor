package com.claudemonitor.app

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build

// Optional notification when a limit resets, toggled from the main screen.
object Notify {
    private const val PREFS = "settings"
    private const val KEY = "notify_reset"
    private const val CHANNEL = "reset"

    fun enabled(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY, false)

    fun setEnabled(ctx: Context, on: Boolean) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY, on).apply()
    }

    fun permitted(ctx: Context) =
        Build.VERSION.SDK_INT < 33 || ctx.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    // Limits that were in use and whose reset time passed since the previous refresh.
    // The new reset must differ, so a server that has not rolled over yet is not reported twice.
    fun resetLimits(old: List<Limit>, new: List<Limit>, now: Long): List<Limit> = old.filter { o ->
        val r = o.reset
        o.pct > 0 && r != null && r <= now && new.find { it.key == o.key }?.reset != r
    }

    fun limitsReset(ctx: Context, a: Account, limits: List<Limit>) {
        if (limits.isEmpty() || !enabled(ctx) || !permitted(ctx)) return
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Reset dei limiti", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        for (l in limits) {
            val n = Notification.Builder(ctx, CHANNEL)
                .setSmallIcon(R.drawable.ic_logo)
                .setContentTitle("${Fmt.label(l.key)}: limite azzerato")
                .setContentText(a.email)
                .setContentIntent(open)
                .setAutoCancel(true)
                .build()
            nm.notify((a.email + l.key).hashCode(), n)
        }
    }
}
