package com.claudemonitor.app

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.roundToInt

object Fmt {
    private val it = Locale.ITALIAN

    fun label(key: String) = when (key) {
        "five_hour" -> "Sessione (5 ore)"
        "seven_day" -> "Settimana"
        "seven_day_sonnet" -> "Settimana · Sonnet"
        "seven_day_opus" -> "Settimana · Opus"
        else -> key
    }

    fun pct(l: Limit) = "${l.pct.roundToInt()}%"

    fun time(ms: Long): String = SimpleDateFormat("HH:mm", it).format(Date(ms))

    // Long form for the app: "Reset tra 2 h 13 min" or "Reset ven 6 nov, 15:57".
    fun countdown(l: Limit): String {
        val reset = l.reset ?: return if (l.key == "five_hour") "Nessuna sessione attiva" else ""
        val left = reset - System.currentTimeMillis()
        if (left <= 0) return "Reset in corso"
        val minutes = left / 60_000
        if (minutes < 24 * 60) {
            val h = minutes / 60
            return "Reset tra " + (if (h > 0) "$h h " else "") + "${minutes % 60} min"
        }
        return "Reset " + SimpleDateFormat("EEE d MMM, HH:mm", it).format(Date(reset))
    }

    // Short form for the widget: an absolute time, so it stays correct between updates.
    fun resetShort(l: Limit): String {
        val reset = l.reset ?: return ""
        val left = reset - System.currentTimeMillis()
        if (left <= 0) return ""
        return if (left < 24 * 3_600_000L) "↻ " + time(reset)
        else "↻ " + SimpleDateFormat("EEE HH:mm", it).format(Date(reset))
    }
}
