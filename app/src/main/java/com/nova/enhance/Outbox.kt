package com.nova.enhance

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID

/**
 * 📤 Every change you confirm on the phone (✓ Handled, ＋ projects, 🎓 Teach the Brain) is kept here and reaches the PC for sure:
 * straight away when the PC answers, otherwise as a small encrypted file in “Nova phone outbox” in your Google Drive — the PC reads it
 * at every start and every 15 minutes. The PC lists what it applied (applied.json in the sync), so each change shows “✓ applied on
 * your PC 14:05”. Each change has an id: never applied twice. Designed by A.HAMROUNI
 */
object Outbox {
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_pc", Context.MODE_PRIVATE)
    fun all(c: Context): JSONArray = try { JSONArray(sp(c).getString("outbox", "[]")) } catch (_: Exception) { JSONArray() }
    private fun save(c: Context, a: JSONArray) = sp(c).edit().putString("outbox", a.toString()).apply()
    private val lock = Any()

    /** Sends a change (live first, then Google Drive). Returns the PC's answer when it was live, else null (kept, on its way). */
    fun send(c: Context, op: String, args: JSONObject, label: String, progress: (String) -> Unit = {}): JSONObject? {
        val id = SimpleDateFormat("yyyyMMddHHmmss", Locale.US).format(Date()) + "-" + UUID.randomUUID().toString().take(8)
        val e = JSONObject().put("id", id).put("op", op).put("args", args).put("label", label).put("at", System.currentTimeMillis()).put("state", "waiting")
        synchronized(lock) { val a = all(c); a.put(e); save(c, a) }
        return deliver(c, e, progress)
    }

    private fun set(c: Context, id: String, f: (JSONObject) -> Unit) = synchronized(lock) {
        val a = all(c); for (i in 0 until a.length()) { val o = a.getJSONObject(i); if (o.optString("id") == id) f(o) }; save(c, a)
    }

    private fun deliver(c: Context, e: JSONObject, progress: (String) -> Unit): JSONObject? {
        val id = e.optString("id")
        val args = JSONObject(e.optJSONObject("args")?.toString() ?: "{}").put("oid", id)
        val live = runCatching { PcLink.ask(c, e.optString("op"), args, progress) }
        live.onSuccess { r -> set(c, id) { it.put("state", "applied").put("appliedAt", System.currentTimeMillis()).put("how", "live") }; return r }
        if (e.optString("state") == "drive") return null
        // The PC did not answer: the encrypted change goes to Google Drive.
        val r = runCatching {
            val plain = JSONObject().put("id", id).put("op", e.optString("op")).put("args", args).put("label", e.optString("label")).put("at", e.optLong("at")).toString()
            GDrive.upload(c, "$id.nes", PcLink.seal(PcLink.keyFor(c), PcLink.gzip(plain.toByteArray())))
        }
        r.onSuccess { set(c, id) { it.put("state", "drive").put("driveAt", System.currentTimeMillis()) } }
            .onFailure { err -> set(c, id) { it.put("state", "waiting").put("err", err.message ?: "failed") } }
        return null
    }

    /** Tries again what has not reached the PC or Drive (the ⏰ job, ⟳, opening My PC). Returns how many went. */
    fun flush(c: Context): Int {
        val todo = (0 until all(c).length()).map { all(c).getJSONObject(it) }.filter { it.optString("state") == "waiting" }
        var n = 0
        for (e in todo) { deliver(c, e) {}; if (all(c).let { a -> (0 until a.length()).any { a.getJSONObject(it).optString("id") == e.optString("id") && a.getJSONObject(it).optString("state") != "waiting" } }) n++ else break }
        // the old “handled” queue (before the outbox)
        val q = PcLink.queued(c)
        if (q.length() > 0) { for (i in 0 until q.length()) send(c, "handled", q.getJSONObject(i), "✓ Handled"); sp(c).edit().putString("handled_queue", "[]").apply() }
        return n
    }

    /** The PC's list of applied changes (applied.json from the sync): ✓ with its time. Old applied ones are forgotten after 7 days. */
    fun receipts(c: Context, applied: JSONObject) = synchronized(lock) {
        val a = all(c); val keep = JSONArray(); val week = System.currentTimeMillis() - 7L * 86_400_000
        for (i in 0 until a.length()) {
            val o = a.getJSONObject(i)
            applied.optJSONObject(o.optString("id"))?.let { r -> if (o.optString("state") != "applied") o.put("state", "applied").put("appliedAt", r.optLong("t")).put("how", "drive") }
            if (o.optString("state") != "applied" || o.optLong("appliedAt") > week) keep.put(o)
        }
        save(c, keep)
    }

    /** One line for the home page: “📤 2 changes on their way to your PC (via Google Drive)” · “✓ last change applied 14:05”. */
    fun line(c: Context): String {
        val a = all(c); val list = (0 until a.length()).map { a.getJSONObject(it) }
        val waiting = list.count { it.optString("state") == "waiting" }; val drive = list.count { it.optString("state") == "drive" }
        val last = list.filter { it.optString("state") == "applied" }.maxByOrNull { it.optLong("appliedAt") }
        val f = SimpleDateFormat("dd MMM HH:mm", Locale.UK)
        return when {
            waiting > 0 -> "📤 $waiting change(s) kept on the phone — they go when Google Drive or your PC answers" + (list.firstOrNull { it.optString("state") == "waiting" }?.optString("err")?.let { if (it.isNotBlank()) " ($it)" else "" } ?: "")
            drive > 0 -> "☁ $drive change(s) in Google Drive — your PC applies them at its next start / within 15 min"
            last != null -> "✓ last change applied on your PC ${f.format(Date(last.optLong("appliedAt")))} · ${last.optString("label")}"
            else -> ""
        }
    }
}
