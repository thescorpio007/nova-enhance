package com.nova.enhance

import android.content.Context
import org.json.JSONObject
import java.util.Calendar

/**
 * 🔍 One search on the phone for emails, project files and drawings — with the question understood (your AI reads the QUESTION only,
 * never the files) and filter chips: kind · stage · discipline · document · file type · when. Works with the PC off; without
 * internet or an AI key the built-in rules understand the usual words (IFC, first floor, RFI, mechanical, this month…).
 * Designed by A.HAMROUNI
 */
object PhoneSearch {

    // ------------------------------------------------------------------ the vocabulary (construction pack)

    /** Discipline chip → the codes in document numbers and the words in folder names. */
    val DISC: Map<String, Pair<Set<String>, Regex>> = linkedMapOf(
        "AR" to (setOf("AR") to Regex("architect|\\barch\\b", RegexOption.IGNORE_CASE)),
        "ST" to (setOf("ST") to Regex("structur", RegexOption.IGNORE_CASE)),
        "ME" to (setOf("ME", "MS", "MEP", "HV") to Regex("mechanical|\\bmep\\b|hvac|\\bmech\\b", RegexOption.IGNORE_CASE)),
        "EL" to (setOf("EL") to Regex("electric", RegexOption.IGNORE_CASE)),
        "PL" to (setOf("PL") to Regex("plumbing|drainage", RegexOption.IGNORE_CASE)),
        "FF" to (setOf("FF", "FS") to Regex("fire\\s*fighting|firefighting|fire safety|fire alarm", RegexOption.IGNORE_CASE)),
        "ID" to (setOf("ID") to Regex("interior", RegexOption.IGNORE_CASE)),
        "LS" to (setOf("LS") to Regex("landscape", RegexOption.IGNORE_CASE)),
        "CW" to (setOf("CW", "CV", "IN") to Regex("\\bcivil\\b|infrastructure", RegexOption.IGNORE_CASE)),
        "FA" to (setOf("FA", "FC") to Regex("fa[cç]ade|curtain wall", RegexOption.IGNORE_CASE)))

    /** Document chip → the codes and the words. */
    val DOC: Map<String, Pair<Set<String>, Regex>> = linkedMapOf(
        "RFI" to (setOf("RFI") to Regex("\\brfi\\b|request for information", RegexOption.IGNORE_CASE)),
        "SHD" to (setOf("SDS", "SHD", "SD") to Regex("\\bshd\\b|\\bsds\\b|shop\\s*drawing", RegexOption.IGNORE_CASE)),
        "WIR" to (setOf("WIR", "IR") to Regex("\\bwir\\b|work inspection|inspection request", RegexOption.IGNORE_CASE)),
        "MIR" to (setOf("MIR") to Regex("\\bmir\\b|material inspection", RegexOption.IGNORE_CASE)),
        "MAR" to (setOf("MAR", "MAT") to Regex("\\bmar\\b|material (approval|submittal)", RegexOption.IGNORE_CASE)),
        "Sample" to (setOf("SSM") to Regex("\\bssm\\b|\\bsamples?\\b", RegexOption.IGNORE_CASE)),
        "MS" to (setOf("MSS", "MST") to Regex("\\bmss\\b|method statement", RegexOption.IGNORE_CASE)),
        "NCR" to (setOf("NCR") to Regex("\\bncr\\b|non.?conformance", RegexOption.IGNORE_CASE)),
        "TQ" to (setOf("TQ") to Regex("\\btq\\b|technical query", RegexOption.IGNORE_CASE)),
        "Letter" to (setOf("LTR") to Regex("\\bltr\\b|\\bletters?\\b", RegexOption.IGNORE_CASE)),
        "MOM" to (setOf("MOM") to Regex("\\bmom\\b|minutes of meeting", RegexOption.IGNORE_CASE)))

    val STAGE: Map<String, Regex> = linkedMapOf(
        "IFC" to Regex("\\bifc\\b|issued? for construction", RegexOption.IGNORE_CASE),
        "IFT" to Regex("\\bift\\b|issued? for tender", RegexOption.IGNORE_CASE),
        "IFA" to Regex("\\bifa\\b|issued? for approval", RegexOption.IGNORE_CASE),
        "As-built" to Regex("as[\\s_-]?built|\\basb\\b", RegexOption.IGNORE_CASE))

    val EXT: Map<String, Set<String>> = linkedMapOf("PDF" to setOf("pdf"), "DWG" to setOf("dwg", "dxf"), "Word" to setOf("doc", "docx"),
        "Excel" to setOf("xls", "xlsx", "xlsm", "csv"), "Email" to setOf("msg", "eml"), "Image" to setOf("jpg", "jpeg", "png"))

    private val DRAWING_WORDS = Regex("\\b(drawings?|dwg|plans?|sections?|elevations?|details?|layouts?|rcp)\\b", RegexOption.IGNORE_CASE)
    private val ORD = listOf("ground", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth",
        "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth")

    // ------------------------------------------------------------------ the question, understood

    class Plan {
        var kind = ""                                   // "" · drawings · files · emails
        var stage = ""; var disc = ""; var doc = ""; var ext = ""
        var level = ""                                  // "1" · "0" (ground) · "B1" · "roof"
        var since = 0L; var whenLabel = ""
        var waiting = false
        var words = ArrayList<String>()
        var ai = false

        fun understood(construction: Boolean): String {
            val p = ArrayList<String>()
            if (kind.isNotEmpty()) p.add(kind)
            if (construction) {
                if (stage.isNotEmpty()) p.add("stage $stage")
                if (disc.isNotEmpty()) p.add("discipline $disc")
                if (doc.isNotEmpty()) p.add("document $doc")
                if (level.isNotEmpty()) p.add("level " + levelLabel(level))
            }
            if (ext.isNotEmpty()) p.add(ext)
            if (waiting) p.add("waiting for my reply")
            if (whenLabel.isNotEmpty()) p.add(whenLabel)
            if (words.isNotEmpty()) p.add("words: " + words.joinToString(" "))
            return (if (ai) "✦ Understood as: " else "Understood as: ") + p.joinToString(" · ").ifBlank { "everything" }
        }
    }

    fun levelLabel(l: String) = when {
        l == "0" -> "ground floor (also GF, Level 00)"
        l == "roof" -> "roof"
        l.startsWith("B") -> "basement $l"
        else -> (ORD.getOrNull(l.toIntOrNull() ?: -1) ?: l) + " floor (also Level %02d, L%02d, %sF)".format(l.toIntOrNull() ?: 0, l.toIntOrNull() ?: 0, l)
    }

    /** The ways a level is written in file names and titles. */
    fun levelRegex(l: String): Regex? = when {
        l.isEmpty() -> null
        l == "roof" -> Regex("\\broof\\b|\\brf\\b", RegexOption.IGNORE_CASE)
        l.startsWith("B") -> Regex("\\bbasement[\\s._-]*0*${l.drop(1)}\\b|\\bB0*${l.drop(1)}\\b", RegexOption.IGNORE_CASE)
        else -> {
            val n = l.toIntOrNull() ?: return null
            val word = ORD.getOrNull(n)
            Regex((if (n == 0) "\\bground\\s+(floor|level)\\b|\\bGF\\b|" else "") + (if (word != null && n > 0) "\\b$word\\s+(floor|level)\\b|" else "") +
                "\\b(?:level|lvl|floor|fl)[\\s._-]*0*$n\\b|(?<![A-Za-z0-9])L[\\s._-]?0*$n(?![0-9])|\\b0*$n(?:st|nd|rd|th)\\s*(?:floor|level)\\b|(?<![A-Za-z0-9])0*${n}F\\b", RegexOption.IGNORE_CASE)
        }
    }

    private val STOP = Regex("^(show|find|search|look|check|list|give|get|bring|display|all|every|each|any|please|me|my|kindly|the|a|an|of|in|for|from|to|and|with|on|about|project|which|that|is|are|was|were|i|need|want|do|did|has|have|not|no|yet|still|this|last|month|year|week|today|latest|newest|recent|file|files|document|documents|email|emails|mail|mails|drawing|drawings|dwg|pdf|floor|level|answered|unanswered|awaiting|waiting|pending|reply|replied|stage|discipline)$", RegexOption.IGNORE_CASE)

    /** The built-in rules (no internet needed). */
    fun rules(question: String, construction: Boolean): Plan {
        val p = Plan(); var q = " $question "
        // The whole word goes ("architecture", not just "architect"), so no left-over becomes a search word.
        fun take(rx: Regex): Boolean {
            if (!rx.containsMatchIn(q)) return false
            q = Regex("[\\p{L}]*(?:${rx.pattern})[\\p{L}]*", RegexOption.IGNORE_CASE).replace(q, " "); return true
        }
        if (construction) {
            for ((k, rx) in STAGE) if (take(rx)) { p.stage = k; break }
            for ((k, v) in DOC) if (take(v.second)) { p.doc = k; break }
            for ((k, v) in DISC) if (take(v.second) || Regex("\\b${k}\\b").containsMatchIn(q).also { hit -> if (hit) q = q.replace(Regex("\\b${k}\\b"), " ") }) { p.disc = k; break }
            // The level: "first floor", "level 3", "L03", "3rd floor", "basement 2", "roof".
            Regex("\\b(${ORD.joinToString("|")})\\s+(floor|level)\\b", RegexOption.IGNORE_CASE).find(q)?.let { p.level = ORD.indexOf(it.groupValues[1].lowercase()).toString(); q = q.replace(it.value, " ") }
            if (p.level.isEmpty()) Regex("\\b(?:level|lvl|floor)\\s*0*(\\d{1,2})\\b|(?<![A-Za-z0-9])L0*(\\d{1,2})\\b|\\b(\\d{1,2})(?:st|nd|rd|th)\\s*(?:floor|level)\\b", RegexOption.IGNORE_CASE).find(q)?.let { m ->
                p.level = m.groupValues.drop(1).first { it.isNotEmpty() }.toInt().toString(); q = q.replace(m.value, " ") }
            if (p.level.isEmpty()) Regex("\\bbasement\\s*0*(\\d)?\\b|\\bB0*(\\d)\\b", RegexOption.IGNORE_CASE).find(q)?.let { m ->
                p.level = "B" + (m.groupValues.drop(1).firstOrNull { it.isNotEmpty() } ?: "1"); q = q.replace(m.value, " ") }
            if (p.level.isEmpty() && take(Regex("\\broof\\b", RegexOption.IGNORE_CASE))) p.level = "roof"
        }
        for ((k, v) in EXT) if (Regex("\\b(${v.joinToString("|")})\\b|\\b${k}\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) && k != "Email") { p.ext = k; break }
        p.waiting = Regex("not\\s+(yet\\s+)?(answered|replied)|unanswered|no\\s+(answer|reply)|awaiting\\s+(my\\s+)?(reply|answer)|waiting\\s+(for\\s+)?my|pending\\s+(my\\s+)?(reply|answer)|need(s)?\\s+my\\s+(reply|answer)", RegexOption.IGNORE_CASE).containsMatchIn(q)
        val cal = Calendar.getInstance()
        fun startOf(field: Int) { cal.set(Calendar.HOUR_OF_DAY, 0); cal.set(Calendar.MINUTE, 0); cal.set(Calendar.SECOND, 0); cal.set(Calendar.MILLISECOND, 0); if (field == Calendar.MONTH) cal.set(Calendar.DAY_OF_MONTH, 1); if (field == Calendar.YEAR) cal.set(Calendar.DAY_OF_YEAR, 1) }
        when {
            Regex("\\btoday\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> { startOf(Calendar.DAY_OF_MONTH); p.since = cal.timeInMillis; p.whenLabel = "today" }
            Regex("\\bthis\\s+week\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> { p.since = System.currentTimeMillis() - 7L * 86400000; p.whenLabel = "this week" }
            Regex("\\bthis\\s+month\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> { startOf(Calendar.MONTH); p.since = cal.timeInMillis; p.whenLabel = "this month" }
            Regex("\\blast\\s+month\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> { p.since = System.currentTimeMillis() - 62L * 86400000; p.whenLabel = "since last month" }
            Regex("\\bthis\\s+year\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> { startOf(Calendar.YEAR); p.since = cal.timeInMillis; p.whenLabel = "this year" }
        }
        p.kind = when {
            p.waiting || Regex("\\b(e-?mails?|mails?|inbox|sent)\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> "emails"
            DRAWING_WORDS.containsMatchIn(q) || p.stage.isNotEmpty() && p.doc.isEmpty() || p.ext == "DWG" -> "drawings"
            Regex("\\b(files?|documents?|reports?|specs?|specifications?)\\b", RegexOption.IGNORE_CASE).containsMatchIn(q) -> "files"
            else -> ""
        }
        p.words = ArrayList(Regex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\-_.]*").findAll(q.replace(Regex("\"[^\"]*\""), " ")).map { it.value }.filter { it.length > 1 && !STOP.matches(it) }.distinct().take(7).toList())
        Regex("\"([^\"]+)\"").findAll(question).forEach { p.words.add("\"" + it.groupValues[1].trim() + "\"") }
        return p
    }

    val PRIVATE = Regex("wallet|password|passwd|keepass|bitwarden|\\b(bank|banking|crypto|seed|passport|salary|personal)\\b", RegexOption.IGNORE_CASE)

    /** Rules first; then your AI reads the question (only the question) and fills what the rules could not. 8 s at most. */
    fun plan(c: Context, question: String): Plan {
        val store = Store(c)
        val construction = Profile.isConstruction(store)
        val base = rules(question, construction)
        if (question.isBlank() || PRIVATE.containsMatchIn(question)) return base
        val p = Providers.all.filter { store.hasKey(it) }.let { k -> k.firstOrNull { it.id == store.target } ?: k.firstOrNull() } ?: return base
        val done = java.util.concurrent.atomic.AtomicReference<JSONObject?>()
        val t = Thread {
            try {
                val sys = "You turn a search request typed by " + Profile.who(store) + " into filters for a search over their own emails and project files. Reply with JSON only:\n" +
                    "{\"kind\":\"drawings|files|emails|\"," +
                    (if (construction) "\"stage\":\"IFC|IFT|IFA|As-built|\",\"discipline\":\"${DISC.keys.joinToString("|")}|\",\"document\":\"${DOC.keys.joinToString("|")}|\",\"level\":\"a floor number, 0 for ground, B1 for basement 1, roof, or empty\"," else "") +
                    "\"filetype\":\"${EXT.keys.joinToString("|")}|\",\"waiting\":true|false,\"words\":[\"up to 6 search words that must appear: names, subjects, numbers, things — never the filter words above, never filler\"]," +
                    "\"also\":[\"up to 6 other spellings or synonyms of those words, same language as the files (English)\"]}\n" +
                    "Empty string when not asked. Do not guess a filter that was not asked. \"waiting\" = the user asks for emails they have not answered yet."
                val raw = Providers.chat(p, store.key(p), store.model(p), sys, listOf(Msg("user", question)), true)
                val a = raw.indexOf('{'); val b = raw.lastIndexOf('}')
                if (a >= 0 && b > a) done.set(JSONObject(raw.substring(a, b + 1)))
            } catch (_: Exception) { }
        }
        t.start(); t.join(8000)
        val o = done.get() ?: return base
        fun s(k: String) = o.optString(k).trim()
        s("kind").lowercase().let { if (it in setOf("drawings", "files", "emails")) base.kind = it }
        if (construction) {
            if (base.stage.isEmpty()) STAGE.keys.firstOrNull { it.equals(s("stage"), true) }?.let { base.stage = it }
            if (base.disc.isEmpty()) DISC.keys.firstOrNull { it.equals(s("discipline"), true) }?.let { base.disc = it }
            if (base.doc.isEmpty()) DOC.keys.firstOrNull { it.equals(s("document"), true) }?.let { base.doc = it }
            if (base.level.isEmpty()) s("level").uppercase().let { l -> if (Regex("^(\\d{1,2}|B\\d|ROOF)$").matches(l)) base.level = if (l == "ROOF") "roof" else l.trimStart('0').ifEmpty { "0" } }
        }
        if (base.ext.isEmpty()) EXT.keys.firstOrNull { it.equals(s("filetype"), true) }?.let { base.ext = it }
        if (o.optBoolean("waiting")) { base.waiting = true; base.kind = "emails" }
        val w = o.optJSONArray("words")
        if (w != null && w.length() > 0) {
            val list = (0 until w.length()).map { w.optString(it).trim() }.filter { it.length > 1 && !STOP.matches(it) }.take(6)
            if (list.isNotEmpty()) base.words = ArrayList(list.map { if (it.contains(' ')) "\"$it\"" else it })
        }
        o.optJSONArray("also")?.let { a -> also = (0 until a.length()).map { a.optString(it).trim() }.filter { it.length > 2 }.take(6) }
        base.ai = true
        return base
    }

    /** Other spellings from the AI: used to lift results that contain them. */
    @Volatile private var also: List<String> = emptyList()

    // ------------------------------------------------------------------ one result

    class Item(val mail: PcLink.Hit?, val file: PcLink.FileHit?) {
        val isMail get() = mail != null
        var name = ""; var path = ""; var date = 0L; var size = 0L
        var stage = ""; var disc = ""; var doc = ""; var ext = ""; var docNo = ""; var rev = ""; var base = ""
        var drawing = false; var levelHit = false; var latest = false
        var project = ""; var waitingDays = -1
        var score = 0.0
        val kind get() = if (isMail) "emails" else if (drawing) "drawings" else "files"
    }

    /** A revision in a file name: …_Rev02 · …-C1 · …_R03 (not L01 / B1, which are levels). */
    private val REV = Regex("[_\\-\\s](?:rev(?:ision)?[._\\-\\s]*(\\d{1,2})|([AC-KMNP-Z]\\d{1,2}))(?=$|[_\\-\\s(.])", RegexOption.IGNORE_CASE)

    private fun discOf(code: String, text: String): String {
        if (code.isNotEmpty()) DISC.entries.firstOrNull { code in it.value.first }?.let { return it.key }
        for ((k, v) in DISC) if (v.second.containsMatchIn(text) || Regex("[\\\\/_\\s.]${k}[\\\\/_\\s.-]").containsMatchIn(text)) return k
        return ""
    }

    private fun docOf(code: String, text: String): String {
        if (code.isNotEmpty()) DOC.entries.firstOrNull { code in it.value.first }?.let { return it.key }
        for ((k, v) in DOC) if (v.second.containsMatchIn(text) || v.first.any { Regex("[\\\\/_\\s.]${it}[\\\\/_\\s.-]").containsMatchIn(text) }) return k
        return ""
    }

    private fun stageOf(text: String): String { for ((k, rx) in STAGE) if (rx.containsMatchIn(text)) return k; return "" }

    fun ofFile(f: PcLink.FileHit, construction: Boolean): Item {
        val it = Item(null, f)
        it.path = f.path; it.name = f.path.substringAfterLast('\\').substringAfterLast('/'); it.date = f.m; it.size = f.size
        val e = it.name.substringAfterLast('.', "").lowercase()
        it.ext = EXT.entries.firstOrNull { e in it.value }?.key ?: ""
        val stem = it.name.substringBeforeLast('.')
        if (construction) {
            val d = DocCodes.of(stem)
            it.docNo = d.first
            val folders = f.path.substringBeforeLast('\\', "")
            it.disc = discOf(d.second, " $stem ").ifEmpty { discOf("", folders.takeLast(160)) }
            it.doc = docOf(d.third, " $stem ").ifEmpty { docOf("", folders.takeLast(160)) }
            it.stage = stageOf(stem).ifEmpty { stageOf(folders) }
            it.drawing = e in setOf("dwg", "dxf") || (e == "pdf" || e.isEmpty()) && (d.third in setOf("DRW", "DWG", "SDS", "SHD", "SD", "DET") ||
                Regex("[\\\\/]_?drawings?[\\\\/]|\\bdrawings?\\b|\\b(plan|section|elevation|detail|layout)s?\\b", RegexOption.IGNORE_CASE).containsMatchIn(f.path) &&
                !Regex("register|report|log of|schedule", RegexOption.IGNORE_CASE).containsMatchIn(stem))
        } else it.drawing = e in setOf("dwg", "dxf")
        // Revision at the end of the name (…_Rev02, …-C1, …_R03): the same drawing in several revisions is one family.
        val m = REV.findAll(stem).lastOrNull { x -> x.range.first > 3 }
        if (m != null) { it.rev = m.groupValues[1].ifEmpty { m.groupValues[2] }.uppercase(); it.base = stem.removeRange(m.range).lowercase().replace(Regex("[\\s_\\-]+"), " ").trim() }
        else it.base = stem.lowercase().replace(Regex("[\\s_\\-]+"), " ").trim()
        return it
    }

    fun ofMail(h: PcLink.Hit, construction: Boolean, waiting: Map<Long, Long>): Item {
        val it = Item(h, null)
        it.name = h.subject; it.date = h.date; it.project = h.project
        if (construction) {
            val text = " ${h.subject} ${h.att} "
            it.docNo = h.doc.first
            it.disc = discOf(h.doc.second, text); it.doc = docOf(h.doc.third, text); it.stage = stageOf(text)
        }
        waiting[h.id]?.let { since -> it.waitingDays = ((System.currentTimeMillis() - (if (since > 0) since else h.date)) / 86400000L).toInt().coerceAtLeast(0) }
        return it
    }

    private fun revRank(r: String): Int = if (r.isEmpty()) -1 else (if (r[0].isLetter()) (r[0] - 'A' + 1) * 100 else 0) + (r.filter { it.isDigit() }.toIntOrNull() ?: 0)

    /** The emails waiting for your reply (from the PC's last brief): id → date. */
    fun waiting(c: Context): Map<Long, Long> = try {
        val a = JSONObject(PcLink.json(c, "brief.json") ?: "{}").optJSONArray("needs")
        val m = HashMap<Long, Long>()
        if (a != null) for (i in 0 until a.length()) a.getJSONObject(i).let { m[it.optLong("i")] = it.optLong("d") }
        m
    } catch (_: Exception) { emptyMap() }

    /** Everything that matches the words (emails + files), with its attributes and score — the chips filter this list. */
    fun run(c: Context, p: Plan, project: String): List<Item> {
        val construction = Profile.isConstruction(Store(c))
        val out = ArrayList<Item>()
        val wait = waiting(c)
        val lvl = levelRegex(p.level)
        val wordRx = p.words.map { w -> Regex(w.trim('"').split(' ', '-', '_', '.').filter { it.isNotEmpty() }.joinToString("[\\s\\-_.]*") { Regex.escape(it) }, RegexOption.IGNORE_CASE) }
        val alsoRx = also.map { w -> Regex(Regex.escape(w), RegexOption.IGNORE_CASE) }
        val query = p.words.joinToString(" ")

        // ✉ Emails
        if (p.kind != "drawings" && p.kind != "files" || p.kind.isEmpty()) {
            val mails: List<PcLink.Hit> =
                if (p.waiting) {
                    val a = try { JSONObject(PcLink.json(c, "brief.json") ?: "{}").optJSONArray("needs") } catch (_: Exception) { null }
                    val l = ArrayList<PcLink.Hit>()
                    if (a != null) for (i in 0 until a.length()) a.getJSONObject(i).let { o ->
                        l.add(PcLink.Hit(o.optLong("i"), o.optLong("d"), o.optBoolean("s"), o.optString("fn"), o.optString("fa"), o.optString("to"), o.optString("cc"),
                            o.optString("sub"), o.optString("an"), o.optString("p"), "", why = o.optString("why"), status = o.optString("st"), first = o.optString("first")))
                    }
                    l.filter { h -> wordRx.all { it.containsMatchIn(h.subject + " " + h.att + " " + h.first) } }
                } else {
                    // The document word helps to find them ("rfi"), the rest is filtered by the chips.
                    val extra = if (construction && p.doc.isNotEmpty() && query.isBlank()) DOC[p.doc]!!.first.first().lowercase() else ""
                    PcLink.search(c, (query + " " + extra).trim(), project).first
                }
            for ((i, h) in mails.withIndex()) {
                val it = ofMail(h, construction, wait)
                it.score = 100.0 - i * 0.2 + (if (lvl != null && lvl.containsMatchIn(h.subject)) 6 else 0) + alsoRx.count { r -> r.containsMatchIn(h.subject) } * 1.5
                out.add(it)
            }
        }

        // 📂 Files and 📐 drawings
        if (p.kind != "emails" && !p.waiting) {
            // Every file whose NAME or folder has all the words, plus the files that have them inside (with the line found).
            val all = PcLink.allFiles(c, 60000)
            val files: List<PcLink.FileHit> = if (query.isBlank()) all else {
                val inside = try { PcLink.searchFiles(c, query, 1500) } catch (_: Exception) { emptyList() }
                val seen = inside.map { it.id }.toHashSet()
                inside + all.filter { f -> f.id !in seen && wordRx.all { it.containsMatchIn(f.path) } }
            }
            for (f in files) {
                val it = ofFile(f, construction)
                val stem = it.name.substringBeforeLast('.')
                var s = 0.0
                for (rx in wordRx) s += if (rx.containsMatchIn(stem)) 6.0 else if (rx.containsMatchIn(f.path)) 2.0 else 1.0     // in the NAME beats "mentioned inside"
                s += alsoRx.count { r -> r.containsMatchIn(stem) } * 2.0
                if (lvl != null) { if (lvl.containsMatchIn(stem)) { s += 8; it.levelHit = true } else if (lvl.containsMatchIn(f.path) || lvl.containsMatchIn(f.snippet)) { s += 2; it.levelHit = true } }
                if (it.drawing) s += 1.0
                if (Regex("register|log of|report", RegexOption.IGNORE_CASE).containsMatchIn(stem) && p.kind == "drawings") s -= 4
                it.score = s
                out.add(it)
            }
            // The latest revision of each family.
            out.filter { !it.isMail }.groupBy { it.base }.values.forEach { g -> g.maxByOrNull { revRank(it.rev) * 1e13 + it.date }?.latest = true }
        }
        return out
    }
}
