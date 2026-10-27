package com.nova.enhance

import android.content.Context
import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Error records kept on the phone (last 200). Each has time, place, AI, model, code, message and
 * the first 100 characters of the text. API keys are never written here.
 */
object ErrorLog {
    private const val MAX = 200
    private fun file(c: Context) = File(c.filesDir, "errors.json")
    private val fmt get() = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)

    @Synchronized
    fun add(c: Context, where: String, ai: String = "", model: String = "", code: Int = 0,
            message: String = "", text: String = "") {
        try {
            val arr = read(c)
            val keys = Store(c).let { s -> Providers.all.map { s.key(it) }.filter { it.length > 8 } }
            var msg = message
            var excerpt = text.replace('\n', ' ').take(100)
            keys.forEach { k -> msg = msg.replace(k, "***"); excerpt = excerpt.replace(k, "***") }
            arr.put(JSONObject().put("t", System.currentTimeMillis()).put("where", where).put("ai", ai)
                .put("model", model).put("code", code).put("msg", msg.take(600)).put("text", excerpt))
            val trimmed = JSONArray()
            val from = (arr.length() - MAX).coerceAtLeast(0)
            for (i in from until arr.length()) trimmed.put(arr.get(i))
            file(c).writeText(trimmed.toString())
        } catch (_: Exception) { }
    }

    fun read(c: Context): JSONArray = try {
        val f = file(c); if (f.exists()) JSONArray(f.readText()) else JSONArray()
    } catch (_: Exception) { JSONArray() }

    fun count(c: Context) = read(c).length()

    fun clear(c: Context) { file(c).delete() }

    /** Plain-text report for sharing (newest first). */
    fun report(c: Context): String {
        val arr = read(c)
        val ver = try { c.packageManager.getPackageInfo(c.packageName, 0).versionName } catch (_: Exception) { "?" }
        val sb = StringBuilder()
        sb.append("Nova Enhance error log\n")
        sb.append("App ${ver} · Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}) · ${Build.MANUFACTURER} ${Build.MODEL}\n")
        sb.append("Records: ${arr.length()} · exported ${fmt.format(Date())}\n\n")
        for (i in arr.length() - 1 downTo 0) {
            val o = arr.getJSONObject(i)
            sb.append("• ${fmt.format(Date(o.optLong("t")))} · ${o.optString("where")}")
            if (o.optString("ai").isNotBlank()) sb.append(" · ${o.optString("ai")}")
            if (o.optString("model").isNotBlank()) sb.append(" · ${o.optString("model")}")
            if (o.optInt("code") != 0) sb.append(" · code ${o.optInt("code")}")
            sb.append("\n  ${o.optString("msg")}\n")
            if (o.optString("text").isNotBlank()) sb.append("  Text: \"${o.optString("text")}\"\n")
        }
        return sb.toString()
    }

    /** Records crashes so they can be shared after the app restarts. */
    fun installCrashHandler(c: Context) {
        val prev = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            add(c, "CRASH", message = e.toString() + "\n" + e.stackTrace.take(8).joinToString("\n") { "   at $it" })
            prev?.uncaughtException(t, e)
        }
    }
}
