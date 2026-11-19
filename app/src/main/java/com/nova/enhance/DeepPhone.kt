package com.nova.enhance

import android.content.Context
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 🔎 Deep search on the phone — the same idea as the PC:
 * your AI splits the question into its parts (the whole system, each product, the references),
 * then Nova searches round after round: the emails and files on this phone, and inside every file of your Google Drive,
 * then the document numbers it found on the way. The answer is written from what was found, with the sources to open.
 * 🔒 Private words (wallet, password, bank…) never go to the AI or to Google: such a question is searched on the phone only.
 */
object DeepPhone {

    private val ASK = Regex("\\b(go deep|deep search|dig|everything about|all about|approved earlier|was approved|previously approved|history of|full history|whole system|all parts)\\b", RegexOption.IGNORE_CASE)
    private val REF = Regex("\\b[A-Z]{2,5}[-_ ]?\\d{3,6}(?:[-_ ]?(?:R|Rev)?\\d{1,2})?\\b")

    private fun day(ms: Long) = java.text.SimpleDateFormat("dd MMM yyyy", java.util.Locale.UK).format(java.util.Date(ms))

    /** The question itself asks to dig. */
    fun wanted(q: String) = ASK.containsMatchIn(q)

    class Src(val kind: String, val title: String, val sub: String, val mail: PcLink.Hit? = null, val file: PcLink.FileHit? = null,
              val drive: GDrive.Found? = null, var text: String = "", var score: Double = 0.0, val terms: HashSet<String> = HashSet())

    class Result(val answer: String, val sources: List<Src>, val log: List<String>, val usedAi: Boolean, val id: String = "")

    private fun ai(c: Context): Provider? {
        val store = Store(c)
        return Providers.all.filter { store.hasKey(it) }.let { k -> k.firstOrNull { it.id == store.target } ?: k.firstOrNull() }
    }

    private fun chat(c: Context, sys: String, user: String, fast: Boolean, waitMs: Long): String? {
        val p = ai(c) ?: return null
        val store = Store(c)
        var out: String? = null
        val t = Thread { try { out = Providers.chat(p, store.key(p), store.model(p), sys + PcLink.guide(c, "search", "deep", "submittal", "answer"), listOf(Msg("user", user)), fast) } catch (_: Exception) { } }
        t.start(); t.join(waitMs)
        return out
    }

    /** The parts to look for: the subject, other wordings, each part of a system, the references. */
    private fun plan(c: Context, q: String, priv: Boolean): List<String> {
        val own = Regex("\"([^\"]+)\"").findAll(q).map { it.groupValues[1] }.toMutableList()
        own += REF.findAll(q).map { it.value }
        if (!priv) {
            val raw = chat(c, "You plan a deep search over a construction professional's own emails and project files (submittals, approvals, RFIs, drawings, method statements). " +
                "Reply with JSON only: {\"subject\":\"the main thing asked, 2-5 words\",\"wide\":[\"up to 3 wider names of the same thing, e.g. the whole system it belongs to\"]," +
                "\"parts\":[\"up to 8 products, components, layers, brands or materials that are usually part of it (as written in submittals)\"]," +
                "\"refs\":[\"document numbers or codes in the question\"]} — short phrases, English, no filler words.", q, true, 12000)
            if (raw != null) try {
                val o = JSONObject(raw.substring(raw.indexOf('{'), raw.lastIndexOf('}') + 1))
                o.optString("subject").takeIf { it.isNotBlank() }?.let { own.add(0, it) }
                for (k in listOf("refs", "wide", "parts")) o.optJSONArray(k)?.let { a -> for (i in 0 until a.length()) own.add(a.optString(i)) }
            } catch (_: Exception) { }
        }
        if (own.isEmpty()) own.add(q)
        return own.map { it.trim().trim('"') }.filter { it.length > 2 }.distinctBy { it.lowercase() }.take(14)
    }

    private fun keyOf(s: Src) = when { s.mail != null -> "m${s.mail.id}"; s.drive != null -> "d${s.drive.id}"; else -> "f${s.file?.path?.lowercase()}" }

    /** [resume] = the id of an interrupted search in 🕘 History: its parts, rounds and finds are taken back, and it goes on from there. */
    fun run(c: Context, q: String, project: String, stop: AtomicBoolean, resume: String? = null, progress: (String) -> Unit): Result {
        val t0 = System.currentTimeMillis()
        val priv = PhoneSearch.PRIVATE.containsMatchIn(q)
        val drive = !priv && GDrive.usable(c)
        val log = ArrayList<String>()
        val found = LinkedHashMap<String, Src>()
        fun timeUp() = stop.get() || System.currentTimeMillis() - t0 > 4 * 60_000

        val old = resume?.let { PhoneHistory.get(c, it) }
        val id = old?.id ?: PhoneHistory.newId()
        val done = HashSet<String>()
        var leadsAdded = false
        val terms: MutableList<String>
        if (old != null) {
            progress("▶ Taking back what was found before…")
            fun arr(k: String) = old.o.optJSONArray(k)?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
            terms = arr("terms").toMutableList(); done += arr("done"); log += arr("log"); leadsAdded = old.o.optBoolean("leads")
            old.o.optJSONArray("sources")?.let { a -> for (i in 0 until a.length()) PhoneHistory.srcOf(c, a.getJSONObject(i))?.let { found[keyOf(it)] = it } }
            log.add("▶ Continued: ${done.size} round(s) and ${found.size} find(s) taken back")
        } else {
            progress("🔎 Splitting the question into its parts…")
            terms = plan(c, q, priv).toMutableList()
            log.add("Parts: " + terms.joinToString(" · "))
        }
        val started = old?.ts ?: System.currentTimeMillis()
        // 🕘 Saved after every round: Nova closing, or another app taking the phone, loses nothing.
        fun save(status: String, answer: String = "", ai: Boolean = false) {
            val src = found.values.sortedByDescending { it.score }.take(80)
            PhoneHistory.save(c, org.json.JSONObject().put("id", id).put("ts", started).put("kind", "deep").put("q", q).put("project", project)
                .put("status", status).put("answer", answer).put("ai", ai).put("n", found.size).put("leads", leadsAdded)
                .put("note", "${done.size} round(s) · ${terms.size} part(s)")
                .put("terms", org.json.JSONArray(terms)).put("done", org.json.JSONArray(done.toList())).put("log", org.json.JSONArray(log))
                .put("sources", org.json.JSONArray(src.map { PhoneHistory.srcJson(it) })))
        }
        save("running")

        fun add(key: String, s: Src, term: String, w: Double) {
            val x = found.getOrPut(key) { s }
            if (x.terms.add(term.lowercase())) x.score += w
        }
        fun round(term: String, weight: Double) {
            if (timeUp()) return
            progress("🔎 “$term” — phone emails & files" + (if (drive) " · Google Drive" else "") + "…  (${found.size} found)")
            var n = 0
            runCatching { PcLink.search(c, term, project).first.take(30) }.getOrDefault(emptyList()).forEach { h ->
                n++; add("m${h.id}", Src("mail", h.subject.ifBlank { "(no subject)" }, (if (h.sent) "Me" else h.from) + " · " + day(h.date), mail = h, text = h.first), term, weight)
            }
            runCatching { PcLink.searchFiles(c, term, 40) }.getOrDefault(emptyList()).forEach { f ->
                n++; add("f${f.path.lowercase()}", Src("file", f.path.substringAfterLast('\\').substringAfterLast('/'), f.path, file = f, text = f.snippet), term, weight + 0.3)
            }
            if (drive && !timeUp()) runCatching { GDrive.fullText(c, term, 25) }.getOrDefault(emptyList()).forEach { d ->
                // The same file already found on the phone (same name) counts once.
                val same = found.values.firstOrNull { it.file != null && it.title.equals(d.name, true) }
                if (same != null) { if (same.terms.add(term.lowercase())) same.score += weight + 0.5 }
                else { n++; add("d${d.id}", Src("drive", d.name, "Google Drive · " + d.modified, drive = d, text = "(the phrase is inside this file)"), term, weight + 0.5) }
            }
            log.add("“$term”: $n")
            if (!timeUp()) { done.add(term.lowercase()); save("running") }
        }

        // Round 1 · the whole subject and its wider names · Round 2 · each part
        terms.toList().forEachIndexed { i, t -> if (t.lowercase() !in done) round(t, if (i == 0) 2.0 else 1.0) }
        // Round 3 · the leads: document numbers seen in what was found (the submittal, its approval, the RFIs about it)
        if (!timeUp() && !leadsAdded) {
            val seen = terms.map { it.lowercase() }.toHashSet()
            val leads = found.values.sortedByDescending { it.score }.take(25)
                .flatMap { REF.findAll(it.title + " " + it.text).map { m -> m.value.replace('_', '-') } }
                .groupingBy { it }.eachCount().entries.sortedByDescending { it.value }.map { it.key }
                .filter { it.lowercase() !in seen && !Regex("^(ISO|BS|EN|ASTM|DIN|IEC)\\b", RegexOption.IGNORE_CASE).containsMatchIn(it) }.take(6)
            if (leads.isNotEmpty()) log.add("Leads: " + leads.joinToString(" · "))
            terms.addAll(leads); leadsAdded = true; save("running")
        }
        if (leadsAdded) terms.toList().filter { it.lowercase() !in done }.forEach { round(it, 0.8) }

        // The best ones: most parts matched, then approvals, then newest.
        val approval = Regex("\\bcode\\s*[-:#]?\\s*(1|2|a|b)\\b|\\bapproved\\b|no objection", RegexOption.IGNORE_CASE)
        found.values.forEach { if (approval.containsMatchIn(it.title + " " + it.text)) it.score += 1.0 }
        val best = found.values.sortedWith(compareByDescending<Src> { it.score }.thenByDescending { it.mail?.date ?: it.file?.m ?: 0L }).take(60)
        if (best.isEmpty()) {
            val a = "Nothing found on the phone" + (if (drive) " or in Google Drive" else "") + " for: " + terms.joinToString(", ")
            save(if (stop.get()) "stopped" else "done", a); return Result(a, best, log, false, id)
        }

        // The email bodies of the best ones (on the phone), for the answer.
        best.take(25).forEach { s -> s.mail?.let { h -> if (s.text.length < 400) runCatching { PcLink.mail(c, h.id)?.body }.getOrNull()?.let { s.text = it } } }

        val secs = (System.currentTimeMillis() - t0) / 1000
        if (priv || timeUp() && stop.get()) {
            val a = "${best.size} found in $secs s" + (if (priv) " · 🔒 private words: not sent to the AI" else " · stopped")
            save(if (priv) "done" else "stopped", a); return Result(a, best, log, false, id)
        }

        progress("✦ Reading the ${minOf(best.size, 30)} best results and writing the answer…")
        val ctx = StringBuilder()
        best.take(30).forEachIndexed { i, s ->
            ctx.append("[${i + 1}] ").append(when (s.kind) { "mail" -> "EMAIL"; "drive" -> "GOOGLE DRIVE FILE"; else -> "FILE" }).append(": ").append(s.title).append(" — ").append(s.sub)
                .append(" · matched: ").append(s.terms.joinToString(", ")).append("\n").append(s.text.replace(Regex("\\s+"), " ").take(900)).append("\n\n")
        }
        val sys = "You answer from the search results below only (emails and project files of " + Profile.who(Store(c)) + "). Never invent a number, date, status or name. " +
            "Start with one or two sentences answering the question. Then a markdown table: | Item / part | Document no. | Status (approved / code / under review / rejected) | Date | Source |, " +
            "one row per part or document, Source = [n]. Then 'Missing:' — the parts nothing was found for. Short."
        val ans = chat(c, sys, "Question: $q\n\nParts searched: ${terms.joinToString(", ")}\n\nResults:\n$ctx", false, 90_000)
        val a = ans?.trim()?.ifBlank { null } ?: "${best.size} found in $secs s (the AI did not answer — the sources are below)"
        save("done", a, ans != null)
        return Result(a, best, log, ans != null, id)
    }
}
