package com.claudemonitor.app

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.util.concurrent.locks.ReentrantLock
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class Limit(val key: String, val pct: Double, val reset: Long?) {
    fun toJson(): JSONObject = JSONObject().put("key", key).put("pct", pct).put("reset", reset ?: JSONObject.NULL)

    companion object {
        fun fromJson(o: JSONObject) =
            Limit(o.getString("key"), o.getDouble("pct"), if (o.isNull("reset")) null else o.getLong("reset"))
    }
}

class Account(
    var email: String,
    var accessToken: String,
    var refreshToken: String,
    var expiresAt: Long,
) {
    var limits: List<Limit> = emptyList()
    var error: String? = null
    var updated: Long? = null

    fun toJson(): JSONObject = JSONObject()
        .put("email", email).put("access_token", accessToken)
        .put("refresh_token", refreshToken).put("expires_at", expiresAt)
        .put("limits", JSONArray(limits.map { it.toJson() }))
        .put("error", error ?: JSONObject.NULL)
        .put("updated", updated ?: JSONObject.NULL)

    companion object {
        fun fromJson(o: JSONObject): Account {
            val a = Account(o.getString("email"), o.getString("access_token"), o.getString("refresh_token"), o.getLong("expires_at"))
            val arr = o.optJSONArray("limits") ?: JSONArray()
            a.limits = (0 until arr.length()).map { Limit.fromJson(arr.getJSONObject(it)) }
            a.error = if (o.isNull("error")) null else o.getString("error")
            a.updated = if (o.isNull("updated")) null else o.getLong("updated")
            return a
        }
    }
}

// Accounts (tokens and last known limits) live in SharedPreferences, encrypted with an Android Keystore AES key.
object Store {
    // Held for the whole duration of a refresh so token rotation never races with login or removal.
    val lock = ReentrantLock()
    private const val PREFS = "store"
    private const val KEY = "accounts"

    fun load(ctx: Context): MutableList<Account> {
        val blob = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(KEY, null) ?: return mutableListOf()
        return try {
            val arr = JSONArray(Crypto.decrypt(blob))
            (0 until arr.length()).map { Account.fromJson(arr.getJSONObject(it)) }.toMutableList()
        } catch (e: Exception) {
            mutableListOf()
        }
    }

    fun save(ctx: Context, accounts: List<Account>) {
        val json = JSONArray(accounts.map { it.toJson() }).toString()
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(KEY, Crypto.encrypt(json)).commit()
    }
}

private object Crypto {
    private const val ALIAS = "claude_monitor_key"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build()
        )
        return gen.generateKey()
    }

    fun encrypt(plain: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val ct = c.doFinal(plain.toByteArray())
        return Base64.encodeToString(c.iv, Base64.NO_WRAP) + ":" + Base64.encodeToString(ct, Base64.NO_WRAP)
    }

    fun decrypt(blob: String): String {
        val (iv, ct) = blob.split(":").map { Base64.decode(it, Base64.NO_WRAP) }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
        return String(c.doFinal(ct))
    }
}
