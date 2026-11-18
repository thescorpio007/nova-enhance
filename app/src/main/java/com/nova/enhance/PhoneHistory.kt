package com.nova.enhance

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * 🕘 Search history on the phone — saved by itself: every search with its date, subject, answer and sources.
 * A deep search is saved after each round, so if Nova closes (or another app takes the phone) nothing is lost:
 * it shows as "interrupted" with ▶ Continue. Kept 60 days by default (90 · 180 · always). Stays on the phone.
 */
object PhoneHistory {

    /** This run of the app: a search still marked "running" from another run was cut off. */
    private val RUN = System.currentTimeMillis().toString()

    private fun dir(c: Context) = File(c.filesDir, "history").apply { mkdirs() }
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_pc", Context.MODE_PRIVATE)
    fun days(c: Context) = sp(c).getInt("hist_days", 60)
    fun setDays(c: Context, d: Int) { sp(c).edit().putInt("hist_days", d).apply(); prune(c) }

    class Entry(val o: JSONObject) {
        val id get() = o.optString("id")
        val ts get() = o.optLong("ts")
        val kind get() = o.optString("kind")           // deep · find · mail · pc
        val q get() = o.optString("q")
        val status: String get() = o.optString("status").let { if (it == "running" && o.optString("run") != RUN) "interrupted" else it }
        val answer get() = o.optString("answer")
        val count get() = o.optInt("n")
        val note get() = o.optString("note")
    }

    fun newId() = System.currentTimeMillis().toString() + (100..999).random()

    @Synchronized fun save(c: Context, o: JSONObject) {
        try {
            o.put("run", RUN); if (!o.has("ts")) o.put("ts", System.currentTimeMillis())
            val f = File(dir(c), o.getString("id") + ".json")
            val tmp = File(f.path + ".tmp"); tmp.writeText(o.toString()); tmp.renameTo(f)   // a cut-off save never leaves half a file
        } catch (_: Exception) { }
    }

    fun get(c: Context, id: String): Entry? = try { Entry(JSONObject(File(dir(c), "$id.json").readText())) } catch (_: Exception) { null }

    fun delete(c: Context, id: String) { File(dir(c), "$id.json").delete() }
    fun clear(c: Context) { dir(c).listFiles()?.forEach { it.delete() } }

    /** Removes what is older than the days kept (never when "always"). */
    fun prune(c: Context) {
        val d = days(c); if (d <= 0) return
        val cut = System.currentTimeMillis() - d * 86_400_000L
        dir(c).listFiles()?.forEach { if (it.lastModified() < cut) it.delete() }
    }

    fun list(c: Context): List<Entry> {
        prune(c)
        return (dir(c).listFiles { f -> f.name.endsWith(".json") } ?: emptyArray())
            .mapNotNull { try { Entry(JSONObject(it.readText())) } catch (_: Exception) { null } }.sortedByDescending { it.ts }
    }

    /** A quick search (on the phone or asked to the PC): the question, how many were found, the best titles. */
    fun quick(c: Context, kind: String, q: String, n: Int, note: String, top: List<String>) {
        if (q.isBlank()) return
        save(c, JSONObject().put("id", newId()).put("kind", kind).put("q", q).put("status", "done").put("n", n).put("note", note)
            .put("answer", top.take(12).joinToString("\n") { "• $it" }))
    }

    // ------------------------------------------------------------------ the sources of a deep search, saved and read back

    fun srcJson(s: DeepPhone.Src): JSONObject = JSONObject().put("k", s.kind).put("t", s.title).put("s", s.sub).put("x", s.text.take(1500)).put("sc", s.score)
        .put("terms", JSONArray(s.terms.toList())).apply {
            s.mail?.let { put("mid", it.id) }
            s.file?.let { put("path", it.path).put("size", it.size).put("m", it.m) }
            s.drive?.let { put("did", it.id).put("dn", it.name).put("dl", it.link).put("dm", it.modified).put("dsz", it.size) }
        }

    /** Back from the saved form; emails are read again from the phone's index (null when no longer there). Blocking. */
    fun srcOf(c: Context, o: JSONObject): DeepPhone.Src? {
        val mail = if (o.has("mid")) (PcLink.mail(c, o.optLong("mid")) ?: return null) else null
        val file = if (o.has("path")) PcLink.FileHit(0, o.optString("path"), o.optLong("size"), o.optLong("m"), "") else null
        val drive = if (o.has("did")) GDrive.Found(o.optString("did"), o.optString("dn"), o.optLong("dsz"), o.optString("dm"), o.optString("dl")) else null
        return DeepPhone.Src(o.optString("k"), o.optString("t"), o.optString("s"), mail, file, drive, o.optString("x"), o.optDouble("sc", 0.0)).also { s ->
            o.optJSONArray("terms")?.let { a -> for (i in 0 until a.length()) s.terms.add(a.optString(i)) }
        }
    }

    /** A saved deep search as it was shown. Blocking. */
    fun result(c: Context, e: Entry): DeepPhone.Result {
        val a = e.o.optJSONArray("sources") ?: JSONArray()
        val src = (0 until a.length()).mapNotNull { srcOf(c, a.getJSONObject(it)) }
        val log = e.o.optJSONArray("log")?.let { l -> (0 until l.length()).map { l.optString(it) } } ?: emptyList()
        return DeepPhone.Result(e.answer.ifBlank { "⚠ Interrupted — ${src.size} found so far. ▶ Continue to finish it." }, src, log, e.o.optBoolean("ai"), e.id)
    }
}
