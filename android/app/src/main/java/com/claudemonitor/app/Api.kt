package com.claudemonitor.app

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.util.Base64
import org.json.JSONObject
import java.io.IOException
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URL
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.OffsetDateTime
import kotlin.concurrent.thread
import kotlin.concurrent.withLock

class ApiException(message: String) : Exception(message)

// Same OAuth client and endpoints used by Claude Code ("Login with Claude account").
object Api {
    private const val CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
    private const val AUTHORIZE_URL = "https://claude.com/cai/oauth/authorize"
    private const val TOKEN_URL = "https://platform.claude.com/v1/oauth/token"
    private const val USAGE_URL = "https://api.anthropic.com/api/oauth/usage"
    private const val SCOPES = "user:profile user:inference"

    private class Response(val status: Int, val body: String)

    private fun http(method: String, url: String, body: String?, bearer: String?): Response {
        val c = URL(url).openConnection() as HttpURLConnection
        try {
            c.requestMethod = method
            c.connectTimeout = 15000
            c.readTimeout = 20000
            c.setRequestProperty("User-Agent", "claude-monitor-android/1.0")
            c.setRequestProperty("Accept", "application/json")
            if (bearer != null) {
                c.setRequestProperty("Authorization", "Bearer $bearer")
                c.setRequestProperty("anthropic-beta", "oauth-2025-04-20")
            }
            if (body != null) {
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.outputStream.use { it.write(body.toByteArray()) }
            }
            val status = c.responseCode
            val stream = if (status in 200..299) c.inputStream else c.errorStream
            return Response(status, stream?.bufferedReader()?.use { it.readText() } ?: "")
        } catch (e: IOException) {
            throw ApiException("Rete non raggiungibile")
        } finally {
            c.disconnect()
        }
    }

    private fun applyToken(a: Account, t: JSONObject) {
        a.accessToken = t.getString("access_token")
        a.refreshToken = t.optString("refresh_token").ifEmpty { a.refreshToken }
        a.expiresAt = System.currentTimeMillis() + t.optLong("expires_in", 3600) * 1000
        t.optJSONObject("account")?.optString("email_address")?.takeIf { it.isNotEmpty() }?.let { a.email = it }
    }

    fun authorizeUrl(redirect: String, challenge: String, state: String): String =
        "$AUTHORIZE_URL?code=true&client_id=$CLIENT_ID&response_type=code" +
            "&redirect_uri=${enc(redirect)}&scope=${enc(SCOPES)}" +
            "&code_challenge=$challenge&code_challenge_method=S256&state=$state"

    fun exchange(code: String, redirect: String, verifier: String, state: String): Account {
        val body = JSONObject()
            .put("grant_type", "authorization_code").put("code", code).put("redirect_uri", redirect)
            .put("client_id", CLIENT_ID).put("code_verifier", verifier).put("state", state)
        val r = http("POST", TOKEN_URL, body.toString(), null)
        if (r.status != 200) throw ApiException("Scambio token fallito (HTTP ${r.status})")
        val a = Account("Account", "", "", 0)
        applyToken(a, JSONObject(r.body))
        return a
    }

    private fun refresh(a: Account) {
        val body = JSONObject()
            .put("grant_type", "refresh_token").put("refresh_token", a.refreshToken)
            .put("client_id", CLIENT_ID).put("scope", SCOPES)
        val r = http("POST", TOKEN_URL, body.toString(), null)
        if (r.status != 200) throw ApiException("Sessione scaduta: rimuovi e ricollega l'account")
        applyToken(a, JSONObject(r.body))
    }

    // Fetches the limits into a.limits, refreshing the access token when needed.
    fun usage(a: Account) {
        var refreshed = false
        if (a.expiresAt - System.currentTimeMillis() < 120_000) {
            refresh(a)
            refreshed = true
        }
        var r = http("GET", USAGE_URL, null, a.accessToken)
        if (r.status == 401 && !refreshed) {
            refresh(a)
            r = http("GET", USAGE_URL, null, a.accessToken)
        }
        if (r.status == 429) throw ApiException("Troppe richieste, riprovo al prossimo giro")
        if (r.status != 200) throw ApiException("Errore HTTP ${r.status}")

        val d = JSONObject(r.body)
        val list = mutableListOf<Limit>()
        for ((key, always) in listOf("five_hour" to true, "seven_day" to true, "seven_day_sonnet" to false, "seven_day_opus" to false)) {
            val o = d.optJSONObject(key)
            val hasPct = o != null && !o.isNull("utilization")
            if (!hasPct && !always) continue
            val reset = o?.optString("resets_at")?.takeIf { it.isNotEmpty() && it != "null" }
                ?.let { runCatching { OffsetDateTime.parse(it).toInstant().toEpochMilli() }.getOrNull() }
            list.add(Limit(key, if (hasPct) o!!.getDouble("utilization") else 0.0, reset))
        }
        a.limits = list
    }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")
}

// Login: opens the browser on the Claude login page and waits for the OAuth redirect on a localhost port.
object Login {
    @Volatile var running = false
        private set

    // Shown by MainActivity when it comes back to the foreground (background toasts are suppressed).
    @Volatile var lastError: String? = null

    fun start(activity: Activity) {
        if (running) return
        running = true
        val app = activity.applicationContext
        thread {
            var message: String? = null
            try {
                val account = run(activity)
                Store.lock.withLock {
                    val list = Store.load(app)
                    val existing = list.find { it.email.equals(account.email, ignoreCase = true) }
                    if (existing == null) list.add(account)
                    else {
                        existing.accessToken = account.accessToken
                        existing.refreshToken = account.refreshToken
                        existing.expiresAt = account.expiresAt
                        existing.error = null
                    }
                    Store.save(app, list)
                }
            } catch (e: Exception) {
                message = e.message ?: "Login non riuscito"
            }
            lastError = message
            running = false
            Bus.changed(app)
            if (message == null) Refresher.refreshAsync(app)
        }
    }

    private fun run(activity: Activity): Account {
        val random = SecureRandom()
        fun b64(b: ByteArray) = Base64.encodeToString(b, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        val verifier = b64(ByteArray(32).also { random.nextBytes(it) })
        val challenge = b64(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray()))
        val state = b64(ByteArray(32).also { random.nextBytes(it) })

        val s4 = ServerSocket(0, 4, InetAddress.getByName("127.0.0.1"))
        val port = s4.localPort
        val s6 = runCatching { ServerSocket(port, 4, InetAddress.getByName("::1")) }.getOrNull()
        val sockets = listOfNotNull(s4, s6).onEach { it.soTimeout = 150 }
        try {
            val redirect = "http://localhost:$port/callback"
            val url = Api.authorizeUrl(redirect, challenge, state)
            activity.runOnUiThread { activity.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }

            val deadline = System.currentTimeMillis() + 5 * 60_000
            while (System.currentTimeMillis() < deadline) {
                for (server in sockets) {
                    val client = try { server.accept() } catch (e: SocketTimeoutException) { null } ?: continue
                    val code = client.use { handle(it, state) } ?: continue
                    return Api.exchange(code, redirect, verifier, state)
                }
            }
            throw ApiException("Login scaduto (5 minuti)")
        } finally {
            sockets.forEach { runCatching { it.close() } }
        }
    }

    private fun handle(client: Socket, expectedState: String): String? {
        client.soTimeout = 5000
        val line = client.getInputStream().bufferedReader().readLine() ?: return null
        val target = line.split(" ").getOrElse(1) { "" }
        val out = client.getOutputStream()
        if (target.substringBefore('?') != "/callback") {
            respond(out, "404 Not Found", "")
            return null
        }
        val query = target.substringAfter('?', "").split('&').filter { '=' in it }.associate {
            it.substringBefore('=') to URLDecoder.decode(it.substringAfter('='), "UTF-8")
        }
        val code = query["code"]
        val error = query["error"]
        val ok = error == null && code != null && query["state"] == expectedState
        val body = if (ok)
            "<h2 style=\"color:#D97757\">Account collegato</h2>" +
                "<p><a href=\"claudemonitor://login\" style=\"display:inline-block;margin-top:16px;padding:14px 28px;" +
                "background:#D97757;color:#1F1E1D;border-radius:12px;text-decoration:none;font-weight:600\">Torna all'app</a></p>"
        else "<h2 style=\"color:#E55B4F\">Login non riuscito</h2>"
        respond(out, "200 OK",
            "<html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>" +
                "<body style=\"background:#1F1E1D;color:#F5F4EE;font-family:sans-serif;text-align:center;padding-top:80px\">" +
                body + "</body></html>")
        if (!ok) throw ApiException(if (error != null) "Login rifiutato: $error" else "Risposta di login non valida")
        return code
    }

    private fun respond(out: OutputStream, status: String, html: String) {
        val b = html.toByteArray()
        out.write("HTTP/1.1 $status\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: ${b.size}\r\nConnection: close\r\n\r\n".toByteArray())
        out.write(b)
        out.flush()
    }
}
