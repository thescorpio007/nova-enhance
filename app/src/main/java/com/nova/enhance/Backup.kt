package com.nova.enhance

import android.content.Context
import android.util.Base64
import org.json.JSONObject
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

/**
 * One backup file with everything: settings, models, history, and the API keys
 * locked with the user's password (AES-256-GCM, key derived with PBKDF2). Without the
 * password the keys can't be read; everything else still restores.
 */
object Backup {
    private const val PREFS = "nova_enhance"
    private const val ITER = 120_000

    fun build(c: Context, password: String): String {
        val sp = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val settings = JSONObject()
        val keys = JSONObject()
        for ((k, v) in sp.all) {
            if (k.startsWith("key_")) { if (v is String && v.isNotBlank()) keys.put(k, v); continue }
            when (v) {
                is Boolean -> settings.put(k, JSONObject().put("t", "b").put("v", v))
                is Int -> settings.put(k, JSONObject().put("t", "i").put("v", v))
                is Long -> settings.put(k, JSONObject().put("t", "l").put("v", v))
                is String -> settings.put(k, JSONObject().put("t", "s").put("v", v))
            }
        }
        val root = JSONObject()
            .put("app", "Nova Enhance").put("format", 1).put("created", System.currentTimeMillis())
            .put("settings", settings)
            .put("history", Db.get(c).exportJson())
        if (keys.length() > 0) root.put("keys", encrypt(keys.toString(), password))
        return root.toString(1)
    }

    /** Returns a summary line. Throws with a clear message on a bad file. */
    fun restore(c: Context, text: String, password: String): String {
        val root = try { JSONObject(text) } catch (_: Exception) { throw IllegalArgumentException("This isn't a Nova Enhance backup file.") }
        if (root.optString("app") != "Nova Enhance") throw IllegalArgumentException("This isn't a Nova Enhance backup file.")
        val e = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
        val settings = root.optJSONObject("settings") ?: JSONObject()
        for (k in settings.keys()) {
            val o = settings.getJSONObject(k)
            when (o.optString("t")) {
                "b" -> e.putBoolean(k, o.optBoolean("v"))
                "i" -> e.putInt(k, o.optInt("v"))
                "l" -> e.putLong(k, o.optLong("v"))
                "s" -> e.putString(k, o.optString("v"))
            }
        }
        var keysNote = "no API keys in the file"
        root.optJSONObject("keys")?.let { enc ->
            keysNote = try {
                val keys = JSONObject(decrypt(enc, password))
                for (k in keys.keys()) e.putString(k, keys.getString(k))
                "${keys.length()} API key(s) restored"
            } catch (_: Exception) {
                "API keys NOT restored (wrong password) — re-enter them in AI & API keys"
            }
        }
        e.apply()
        val added = Db.get(c).importJson(root.optJSONArray("history") ?: org.json.JSONArray())
        return "Settings restored · $added conversation(s) added · $keysNote"
    }

    private fun keyFrom(password: String, salt: ByteArray): SecretKeySpec {
        val f = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
        val k = f.generateSecret(PBEKeySpec(password.toCharArray(), salt, ITER, 256)).encoded
        return SecretKeySpec(k, "AES")
    }

    private fun encrypt(plain: String, password: String): JSONObject {
        val rnd = SecureRandom()
        val salt = ByteArray(16).also { rnd.nextBytes(it) }
        val iv = ByteArray(12).also { rnd.nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, keyFrom(password, salt), GCMParameterSpec(128, iv))
        val out = cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        fun b64(b: ByteArray) = Base64.encodeToString(b, Base64.NO_WRAP)
        return JSONObject().put("alg", "AES-256-GCM/PBKDF2-SHA256").put("iter", ITER)
            .put("salt", b64(salt)).put("iv", b64(iv)).put("data", b64(out))
    }

    private fun decrypt(o: JSONObject, password: String): String {
        fun d(s: String) = Base64.decode(o.getString(s), Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, keyFrom(password, d("salt")), GCMParameterSpec(128, d("iv")))
        return String(cipher.doFinal(d("data")), Charsets.UTF_8)
    }
}
