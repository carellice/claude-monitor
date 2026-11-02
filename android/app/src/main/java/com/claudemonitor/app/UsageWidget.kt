package com.claudemonitor.app

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.os.Build
import android.view.View
import android.widget.RemoteViews
import kotlin.math.roundToInt

class UsageWidget : AppWidgetProvider() {
    override fun onUpdate(ctx: Context, manager: AppWidgetManager, ids: IntArray) {
        Widget.updateAll(ctx)
        Jobs.schedule(ctx)
    }

    override fun onEnabled(ctx: Context) {
        Jobs.schedule(ctx)
        Jobs.refreshNow(ctx)
    }

    override fun onReceive(ctx: Context, intent: Intent) {
        super.onReceive(ctx, intent)
        if (intent.action == Widget.ACTION_REFRESH) {
            Widget.updateAll(ctx, loading = true)
            Jobs.refreshNow(ctx)
        }
    }
}

object Widget {
    const val ACTION_REFRESH = "com.claudemonitor.app.REFRESH"

    fun updateAll(ctx: Context, loading: Boolean = false) {
        val manager = AppWidgetManager.getInstance(ctx)
        val ids = manager.getAppWidgetIds(ComponentName(ctx, UsageWidget::class.java))
        if (ids.isEmpty()) return
        val accounts = Store.load(ctx)
        val pkg = ctx.packageName
        val open = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val refresh = PendingIntent.getBroadcast(ctx, 1,
            Intent(ctx, UsageWidget::class.java).setAction(ACTION_REFRESH),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val last = accounts.mapNotNull { it.updated }.maxOrNull()
        val status = if (loading || Refresher.busy) "aggiorno…" else last?.let { Fmt.time(it) } ?: ""

        for (id in ids) {
            val rv = RemoteViews(pkg, R.layout.widget)
            rv.setTextViewText(R.id.updated, status)
            rv.setViewVisibility(R.id.empty, if (accounts.isEmpty()) View.VISIBLE else View.GONE)
            rv.removeAllViews(R.id.accounts)
            for (a in accounts) rv.addView(R.id.accounts, accountRow(ctx, a))
            rv.setOnClickPendingIntent(R.id.root, open)
            rv.setOnClickPendingIntent(R.id.refresh, refresh)
            manager.updateAppWidget(id, rv)
        }
    }

    private fun accountRow(ctx: Context, a: Account): RemoteViews {
        val row = RemoteViews(ctx.packageName, R.layout.widget_account)
        row.setTextViewText(R.id.name, a.email)
        val slots = listOf(
            Triple(R.id.bar1, R.id.pct1, R.id.reset1) to "five_hour",
            Triple(R.id.bar2, R.id.pct2, R.id.reset2) to "seven_day",
        )
        for ((views, key) in slots) {
            val (bar, pct, reset) = views
            val l = a.limits.find { it.key == key }
            val high = l != null && l.pct >= 90
            row.setProgressBar(bar, 100, l?.pct?.roundToInt()?.coerceIn(0, 100) ?: 0, false)
            row.setTextViewText(pct, l?.let { Fmt.pct(it) } ?: "—")
            row.setTextColor(pct, ctx.getColor(if (high) R.color.danger else R.color.text))
            row.setTextViewText(reset, l?.let { Fmt.resetShort(it) } ?: "")
            if (Build.VERSION.SDK_INT >= 31) {
                row.setColorStateList(bar, "setProgressTintList",
                    ColorStateList.valueOf(ctx.getColor(if (high) R.color.danger else R.color.accent)))
            }
        }
        row.setTextViewText(R.id.error, a.error ?: "")
        row.setViewVisibility(R.id.error, if (a.error != null) View.VISIBLE else View.GONE)
        return row
    }
}
