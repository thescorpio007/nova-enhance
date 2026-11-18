package com.nova.enhance

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 🧊 "Nova Enhance isn't responding": Android keeps a report of what Nova was doing (Android 11 and newer).
 * Nova reads it on the next start and keeps it, so it can be shared and the exact line fixed.
 */
object Freeze {
    private fun file(c: Context) = File(c.filesDir, "freeze-report.txt")
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_pc", Context.MODE_PRIVATE)

    /** A new freeze since the last check: its time ("18 Nov 15:45"), or null. */
    fun check(c: Context): String? {
        if (android.os.Build.VERSION.SDK_INT < 30) return null
        return try {
            val am = c.getSystemService(ActivityManager::class.java) ?: return null
            val seen = sp(c).getLong("freeze_seen", 0)
            val x = am.getHistoricalProcessExitReasons(c.packageName, 0, 10)
                .filter { it.reason == ApplicationExitInfo.REASON_ANR && it.timestamp > seen }.maxByOrNull { it.timestamp } ?: return null
            sp(c).edit().putLong("freeze_seen", x.timestamp).apply()
            val trace = try { x.traceInputStream?.use { s -> s.readBytes().toString(Charsets.UTF_8) } } catch (_: Exception) { null } ?: ""
            val t = SimpleDateFormat("dd MMM HH:mm", Locale.UK).format(Date(x.timestamp))
            // The screen thread first (where the freeze is), then Nova's own lines from the other threads.
            val main = Regex("(?s)\"main\".*?(?=\\n\\s*\\n\")").find(trace)?.value ?: ""
            val ours = trace.lines().filter { it.contains("com.nova.enhance") }.distinct().take(80)
            val txt = "Nova Enhance freeze · $t · Android ${android.os.Build.VERSION.RELEASE} · ${android.os.Build.MODEL}\n" +
                "Reason: ${x.description ?: "not responding"}\n\n--- screen thread ---\n" + main.take(12_000) +
                "\n\n--- Nova's lines in all threads ---\n" + ours.joinToString("\n")
            file(c).writeText(txt)
            t
        } catch (_: Exception) { null }
    }

    fun last(c: Context): Pair<String, String>? = file(c).takeIf { it.exists() }?.let {
        SimpleDateFormat("dd MMM yyyy HH:mm", Locale.UK).format(Date(sp(c).getLong("freeze_seen", it.lastModified()))) to it.readText()
    }

    fun clear(c: Context) { file(c).delete() }
}
