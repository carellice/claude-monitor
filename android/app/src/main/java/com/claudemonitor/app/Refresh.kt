package com.claudemonitor.app

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.Handler
import android.os.Looper
import kotlin.concurrent.thread

// Notifies the open screen and the widgets that stored data changed.
object Bus {
    private val main = Handler(Looper.getMainLooper())
    var listener: (() -> Unit)? = null

    fun post(block: () -> Unit) {
        main.post(block)
    }

    fun changed(ctx: Context) {
        val app = ctx.applicationContext
        main.post {
            listener?.invoke()
            Widget.updateAll(app)
        }
    }
}

object Refresher {
    // Fake accounts seeded by debug builds for screenshots (see MainActivity.seedDemo).
    const val DEMO_TOKEN = "demo"

    @Volatile var busy = false
        private set

    fun refreshAll(ctx: Context) {
        if (!Store.lock.tryLock()) return // another refresh is already running
        try {
            busy = true
            Bus.changed(ctx)
            val list = Store.load(ctx)
            for (a in list) {
                if (a.accessToken == DEMO_TOKEN) continue
                try {
                    Api.usage(a)
                    a.error = null
                    a.updated = System.currentTimeMillis()
                } catch (e: ApiException) {
                    a.error = e.message
                } catch (e: Exception) {
                    a.error = "Errore: ${e.message}"
                }
                Store.save(ctx, list) // save after each account: tokens may have rotated
            }
        } finally {
            busy = false
            Store.lock.unlock()
        }
        Bus.changed(ctx)
    }

    fun refreshAsync(ctx: Context) {
        val app = ctx.applicationContext
        thread { refreshAll(app) }
    }
}

object Jobs {
    private const val PERIODIC_ID = 1
    private const val NOW_ID = 2

    private fun scheduler(ctx: Context) = ctx.getSystemService(JobScheduler::class.java)

    fun schedule(ctx: Context) {
        val js = scheduler(ctx)
        if (js.getPendingJob(PERIODIC_ID) != null) return
        js.schedule(
            JobInfo.Builder(PERIODIC_ID, ComponentName(ctx, RefreshJob::class.java))
                .setPeriodic(15 * 60 * 1000L)
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPersisted(true)
                .build()
        )
    }

    fun refreshNow(ctx: Context) {
        scheduler(ctx).schedule(
            JobInfo.Builder(NOW_ID, ComponentName(ctx, RefreshJob::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .build()
        )
    }
}

class RefreshJob : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        thread {
            Refresher.refreshAll(applicationContext)
            jobFinished(params, false)
        }
        return true
    }

    override fun onStopJob(params: JobParameters) = true
}
