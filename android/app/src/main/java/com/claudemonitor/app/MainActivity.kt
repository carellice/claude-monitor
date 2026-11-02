package com.claudemonitor.app

import android.app.Activity
import android.app.AlertDialog
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.ApplicationInfo
import android.content.res.ColorStateList
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowInsets
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import kotlin.concurrent.thread
import kotlin.concurrent.withLock
import kotlin.math.roundToInt

class MainActivity : Activity() {
    private lateinit var list: LinearLayout
    private lateinit var empty: TextView
    private lateinit var addButton: View
    private lateinit var pinWidget: View
    private var demo = false
    private val handler = Handler(Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() {
            render()
            handler.postDelayed(this, 30_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        drawEdgeToEdge()

        list = findViewById(R.id.list)
        empty = findViewById(R.id.empty)
        addButton = findViewById(R.id.add)
        addButton.setOnClickListener {
            Login.start(this)
            render()
        }
        findViewById<View>(R.id.refresh).setOnClickListener { Refresher.refreshAsync(this) }
        pinWidget = findViewById(R.id.pin_widget)
        pinWidget.setOnClickListener {
            AppWidgetManager.getInstance(this).requestPinAppWidget(ComponentName(this, UsageWidget::class.java), null, null)
        }
        findViewById<TextView>(R.id.update_banner).setOnClickListener { Updater.install(this) }
        findViewById<TextView>(R.id.footer).text = "v${Updater.currentVersion(this)} · " + getString(R.string.footer)
        handleInstallStatus(intent)
        if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0 && intent.getBooleanExtra("demo", false)) {
            demo = true
            seedDemo()
            return
        }
        Jobs.schedule(this)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleInstallStatus(intent)
    }

    // Callback from PackageInstaller (see Updater.install).
    private fun handleInstallStatus(intent: Intent) {
        if (intent.action != Updater.ACTION_INSTALL_STATUS) return
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                if (confirm != null) startActivity(confirm)
            }
            PackageInstaller.STATUS_SUCCESS -> Unit
            else -> Toast.makeText(this, "Aggiornamento non riuscito: " +
                (intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "errore"), Toast.LENGTH_LONG).show()
        }
    }

    // Debug builds only: fake accounts to check the layout without logging in.
    private fun seedDemo() {
        val now = System.currentTimeMillis()
        val a = Account("mario.rossi@gmail.com", Refresher.DEMO_TOKEN, Refresher.DEMO_TOKEN, now + 86_400_000).apply {
            updated = now
            limits = listOf(Limit("five_hour", 42.0, now + 133 * 60_000), Limit("seven_day", 18.0, now + 4 * 86_400_000))
        }
        val b = Account("lavoro@example.com", Refresher.DEMO_TOKEN, Refresher.DEMO_TOKEN, now + 86_400_000).apply {
            updated = now
            limits = listOf(Limit("five_hour", 94.0, now + 41 * 60_000), Limit("seven_day", 63.0, now + 2 * 86_400_000),
                Limit("seven_day_sonnet", 7.0, null))
        }
        Store.save(this, listOf(a, b))
        Bus.changed(this)
    }

    override fun onStart() {
        super.onStart()
        Bus.listener = { render() }
        handler.post(tick)
        if (demo) return
        val last = Store.load(this).mapNotNull { it.updated }.maxOrNull() ?: 0
        if (System.currentTimeMillis() - last > 60_000) Refresher.refreshAsync(this)
        Updater.checkAsync(this)
    }

    override fun onStop() {
        Bus.listener = null
        handler.removeCallbacks(tick)
        super.onStop()
    }

    private fun drawEdgeToEdge() {
        val root = findViewById<View>(R.id.root)
        if (Build.VERSION.SDK_INT >= 30) window.setDecorFitsSystemWindows(false)
        else @Suppress("DEPRECATION") {
            root.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        }
        root.setOnApplyWindowInsetsListener { v, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val i = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                v.setPadding(i.left, i.top, i.right, i.bottom)
            } else @Suppress("DEPRECATION") {
                v.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop,
                    insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
            }
            insets
        }
    }

    private fun render() {
        Login.lastError?.let {
            Login.lastError = null
            Toast.makeText(this, it, Toast.LENGTH_LONG).show()
        }
        val banner = findViewById<TextView>(R.id.update_banner)
        val update = Updater.available
        banner.visibility = if (update != null) View.VISIBLE else View.GONE
        if (update != null) banner.text =
            if (Updater.downloading) "Scarico la versione ${update.version}…"
            else "Nuova versione ${update.version} disponibile · tocca per aggiornare"

        val accounts = Store.load(this)
        val widgets = AppWidgetManager.getInstance(this).getAppWidgetIds(ComponentName(this, UsageWidget::class.java))
        pinWidget.visibility =
            if (widgets.isEmpty() && AppWidgetManager.getInstance(this).isRequestPinAppWidgetSupported) View.VISIBLE else View.GONE
        addButton.isEnabled = !Login.running
        addButton.alpha = if (Login.running) 0.4f else 1f
        empty.visibility = if (accounts.isEmpty()) View.VISIBLE else View.GONE
        list.removeAllViews()
        val accent = getColor(R.color.accent)
        val danger = getColor(R.color.danger)

        for (a in accounts) {
            val card = layoutInflater.inflate(R.layout.item_account, list, false)
            card.findViewById<TextView>(R.id.email).text = a.email
            card.findViewById<TextView>(R.id.updated).text =
                if (Refresher.busy) "aggiorno…" else a.updated?.let { Fmt.time(it) } ?: ""
            val error = card.findViewById<TextView>(R.id.error)
            error.text = a.error
            error.visibility = if (a.error != null) View.VISIBLE else View.GONE

            val limits = card.findViewById<LinearLayout>(R.id.limits)
            val shown = a.limits.ifEmpty { listOf(Limit("five_hour", 0.0, null), Limit("seven_day", 0.0, null)) }
            for (l in shown) {
                val row = layoutInflater.inflate(R.layout.item_limit, limits, false)
                val high = l.pct >= 90
                row.findViewById<TextView>(R.id.label).text = Fmt.label(l.key)
                row.findViewById<TextView>(R.id.pct).apply {
                    text = if (a.limits.isEmpty()) "—" else Fmt.pct(l)
                    setTextColor(if (high) danger else getColor(R.color.text))
                }
                row.findViewById<ProgressBar>(R.id.bar).apply {
                    progress = l.pct.roundToInt().coerceIn(0, 100)
                    progressTintList = ColorStateList.valueOf(if (high) danger else accent)
                }
                row.findViewById<TextView>(R.id.reset).text = if (a.limits.isEmpty()) "" else Fmt.countdown(l)
                limits.addView(row)
            }

            card.setOnLongClickListener {
                confirmRemove(a)
                true
            }
            list.addView(card)
        }
    }

    private fun confirmRemove(a: Account) {
        AlertDialog.Builder(this, R.style.DialogTheme)
            .setMessage("Rimuovere ${a.email} da Claude Monitor?")
            .setNegativeButton("Annulla", null)
            .setPositiveButton("Rimuovi") { _, _ ->
                val app = applicationContext
                thread {
                    Store.lock.withLock {
                        val accounts = Store.load(app)
                        accounts.removeAll { it.email.equals(a.email, ignoreCase = true) }
                        Store.save(app, accounts)
                    }
                    Bus.changed(app)
                }
            }
            .show()
    }
}
