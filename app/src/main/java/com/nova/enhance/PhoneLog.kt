package com.nova.enhance

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * 📋 Submittal log on the phone — the same rules as the PC (🧠 Nova Brain settings come with the hourly update):
 * WIR · MIR · MAR · MSS · SDS · SSM · TSR · PQS · RFI · RPT · HSE · CAL · RTS · HOV · PLN · LTR · PAY · DSR · VO · AUTH · SOR · NCR · EI · MOM,
 * Thinkproject or plain email, the MZA reply on the same thread, tower · zone · level, overdue, registers, ⬇ Excel. Works with the PC off.
 * Designed by A.HAMROUNI
 */
object PhoneLog {

    class Options {
        var projects = ArrayList<String>(); var types = ArrayList<String>()
        var from: Long = System.currentTimeMillis() - 30L * 86_400_000; var to: Long = System.currentTimeMillis() + 86_400_000
        var tower = ""; var zone = ""; var disc = ""; var status = ""; var levelFrom = -99; var levelTo = 999
        fun copy() = Options().also { o -> o.projects = ArrayList(projects); o.types = ArrayList(types); o.from = from; o.to = to; o.tower = tower; o.zone = zone; o.disc = disc; o.status = status; o.levelFrom = levelFrom; o.levelTo = levelTo }
    }

    class Item(val type: String, var project: String) {
        var no = ""; var rev = ""; var revNum = 0; var num = -1; var series = ""; var disc = ""; var title = ""; var via = ""; var ref = ""
        var tower = ""; var zone = ""; var level = ""; var apt = ""; var levelNum: Int? = null
        var sub: PcLink.Hit? = null; var rep: PcLink.Hit? = null; var deadline: Long? = null
        var code = ""; var status = ""; var why = ""; var check = ""; var by = ""
        val mails = ArrayList<PcLink.Hit>()
        val submitted get() = sub?.date
        val reply get() = rep?.date
    }

    class Result(val items: List<Item>, val mails: Int, val gaps: List<String>, val opt: Options, val secs: Double)

    data class Def(val code: String, val label: String, val find: String, val close: String, val overdue: Int)

    val TYPES = listOf(
        Def("WIR", "Work inspection request", "code", "code", 2), Def("MIR", "Material inspection request", "code", "code", 2),
        Def("MAR", "Material approval request", "code", "code", 14), Def("MSS", "Method statement", "code", "code", 14),
        Def("SDS", "Shop drawing", "code", "code", 14), Def("SSM", "Sample", "code", "code", 14), Def("TSR", "Test report", "code", "code", 14),
        Def("PQS", "Prequalification", "code", "code", 14), Def("RFI", "Request for information", "code", "code", 7),
        Def("RPT", "Report", "code", "code", 14), Def("HSE", "HSE submittal", "code", "code", 14), Def("CAL", "Calculation", "code", "code", 14),
        Def("RTS", "Request to start", "code", "code", 7), Def("HOV", "Handover", "code", "code", 14), Def("PLN", "Planning", "code", "code", 14),
        Def("LTR", "Letter", "code", "reply", 14), Def("PAY", "Payment application", "words", "reply", 14), Def("DSR", "Daily report", "code", "received", 0),
        Def("VO", "Variation", "words", "reply", 14), Def("AUTH", "Authority submission", "words", "anyreply", 14),
        Def("SOR", "Site observation report (MZA)", "code", "mzaconfirm", 7), Def("NCR", "Non-conformance report (MZA)", "code", "mzaconfirm", 14),
        Def("EI", "Engineer's instruction (MZA)", "words", "contractor", 7), Def("MOM", "Minutes of meeting", "words", "final", 7))
    fun def(code: String) = TYPES.firstOrNull { it.code == code }
    private val ALIAS = mapOf("SHD" to "SDS", "REP" to "RPT", "VOR" to "VO", "PAYMENT" to "PAY", "AUTHORITY" to "AUTH")

    private val I = RegexOption.IGNORE_CASE
    private val replyRx = Regex("^\\s*((re|aw|antw)\\s*:\\s*|\\[external]\\s*)+", I)
    private val fwdRx = Regex("^\\s*((fw|fwd|tr)\\s*:|automatic reply|auto(matic)?[- ]reply|out of office)", I)
    private val strip = Regex("^\\s*((re|aw|antw|fw|fwd|tr)\\s*:\\s*|\\[external]\\s*|\\[ext]\\s*)+", I)
    private val eiRx = Regex("Engineer\\W{0,2}s?\\s+Instruction|\\bEI\\s*No\\.?\\s*\\d+", I)
    private val eiNum = Regex("(?:Instruction|\\bEI)\\s*(?:no\\.?|number|#)?\\s*0*(\\d{1,4})\\b", I)
    private val detRx = Regex("Engineer\\W{0,2}s?\\s+Determination", I)
    private val momRx = Regex("\\bMoM\\b|Minutes of|Meeting\\s*No\\.?\\s*\\d+|Kick[\\s-]?Off\\s+Meeting|-MOM-", I)
    private val payRx = Regex("payment|\\bIPC\\b|\\bIPA\\b|statement of account|interim payment", I)
    private val voRx = Regex("\\bvariations?\\b|\\bVOR?\\b|\\bV\\.O\\.", I)
    private val authRx = Regex("\\bADCD\\b|civil defen[cs]e|\\bADM\\b|municipality|\\bDMT\\b|\\bAldar\\b|\\bMiral\\b|\\bNOC\\b|\\bADDC\\b", I)
    private val letterRef = Regex("\\b(?:[A-Z]{2,5}-){2,4}\\d{2}-\\d{3,4}-L-\\d{4}-\\d{2,4}\\b|\\b[A-Z0-9]{2,6}-[A-Z]{2,5}-PM-LTR-\\d{3,6}\\b")

    private fun numRx(codes: List<String>) = Regex("(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,5}(?:-[A-Z0-9]{1,6}){1,4}?-(" + codes.joinToString("|") { Regex.escape(it) } + ")-(\\d{2,6}))(?:[_\\- ]*(?i:Rev|R)\\.?\\s?(\\d{1,2}))?")

    fun enabled(c: Context) = PcLink.settingList(c, "log.all", TYPES.joinToString(", ") { it.code }).map { ALIAS[it.uppercase()] ?: it.uppercase() }.distinct()
    private fun norm(s: String) = strip.replace(s, "").lowercase().replace(Regex("[^\\p{L}\\p{N}]+"), " ").trim()
    private val day = SimpleDateFormat("dd MMM HH:mm", Locale.UK)
    private val dayY = SimpleDateFormat("dd MMM yyyy", Locale.UK)
    fun d(ms: Long?) = if (ms == null) "" else day.format(java.util.Date(ms))
    fun dy(ms: Long?) = if (ms == null) "" else dayY.format(java.util.Date(ms))

    private fun mails(c: Context, subj: List<String>, att: List<String>, after: Long): List<PcLink.Hit> {
        val ors = ArrayList<String>(); val args = ArrayList<String>()
        subj.distinct().forEach { ors.add("sub LIKE ?"); args.add(it) }
        att.distinct().forEach { ors.add("an LIKE ?"); args.add(it) }
        if (ors.isEmpty()) return emptyList()
        args.add(after.toString())
        val out = ArrayList<PcLink.Hit>()
        PcLink.Db.get(c).rawQuery("SELECT id, d, s, fn, fa, too, cc, sub, an, p, f, substr(b,1,3000) FROM mail WHERE (${ors.joinToString(" OR ")}) AND d >= ? ORDER BY d LIMIT 60000", args.toTypedArray()).use { cu ->
            while (cu.moveToNext()) out.add(PcLink.Hit(cu.getLong(0), cu.getLong(1), cu.getInt(2) != 0, cu.getString(3) ?: "", cu.getString(4) ?: "", cu.getString(5) ?: "", cu.getString(6) ?: "",
                cu.getString(7) ?: "", cu.getString(8) ?: "", cu.getString(9) ?: "", cu.getString(10) ?: "", body = cu.getString(11) ?: ""))
        }
        return out.distinctBy { Triple(it.subject, it.date, it.fromAddr) }
    }

    fun run(c: Context, o: Options): Result {
        val t0 = System.currentTimeMillis()
        val want = (if (o.types.isNotEmpty()) o.types.map { ALIAS[it.uppercase()] ?: it.uppercase() } else enabled(c)).toSet()
        val mza = PcLink.settingList(c, "mza.match", "MZ ARCHITECTS, mz-architects.com")
        val closedFiles = PcLink.settingList(c, "closed.files", "signed, -C1, -C2, -C3, -C4, reply")
        val codeTypes = TYPES.filter { it.find == "code" && it.code in want }.map { it.code }.toMutableList()
        if ("HSE" in want) codeTypes.add("GEN")
        if ("PAY" in want && "LTR" !in codeTypes) codeTypes.add("LTR")
        val rx = if (codeTypes.isNotEmpty()) numRx(codeTypes) else null
        val subj = codeTypes.map { "%-$it-%" }.toMutableList()
        if ("EI" in want) subj += listOf("%Engineer%Instruction%", "%EI No%", "%Engineer%Determination%")
        if ("MOM" in want) subj += listOf("%Minutes%", "%MOM%", "%Meeting No%", "%Kick%Off%")
        if ("VO" in want) subj += listOf("%Variation%", "% VO %", "% VO-%", "%VOR%", "%V.O.%")
        if ("AUTH" in want) subj += listOf("%ADCD%", "%Civil Defen%", "%ADM %", "%Municipality%", "%DMT%", "%Aldar%", "%Miral%", "%NOC%", "%ADDC%")
        if ("PAY" in want) subj += listOf("%Payment%", "%IPC%", "%IPA%", "%Statement of Account%")
        val ms = mails(c, subj, if ("MOM" in want) listOf("%MOM%") else emptyList(), o.from - 3 * 86_400_000L)
        val known = HashSet<String>().apply { add("SLV"); add("RS5"); ms.forEach { if (it.project.isNotBlank()) add(it.project.uppercase()) } }
        fun isMza(h: PcLink.Hit) = h.sent || mza.any { h.from.contains(it, true) || h.fromAddr.contains(it, true) }
        fun projFrom(s: String): String {
            Regex("(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,6})-[A-Z0-9]").findAll(s).forEach { if (it.groupValues[1] in known) return it.groupValues[1] }
            if (Regex("Sea\\s*La\\s*Vie|\\bSLV\\b", I).containsMatchIn(s)) return "SLV"
            if (Regex("\\bRS5\\b", I).containsMatchIn(s)) return "RS5"
            return ""
        }
        val groups = LinkedHashMap<String, Item>()
        val determ = HashMap<String, PcLink.Hit>()
        fun g(key: String, make: () -> Item) = groups.getOrPut(key, make)

        for (h in ms.sortedBy { it.date }) {
            val s = h.subject; val clean = strip.replace(s, "")
            if ("EI" in want && (eiRx.containsMatchIn(s) || detRx.containsMatchIn(s))) {
                val n = eiNum.find(s)
                if (n != null) {
                    val p = projFrom(s).ifBlank { h.project }
                    val num = n.groupValues[1].toInt()
                    if (detRx.containsMatchIn(s)) { determ.putIfAbsent("$p|$num", h); continue }
                    val it = g("EI|$p|$num") { Item("EI", p).also { x -> x.num = num; x.no = "EI %03d".format(num); x.via = "Letter" } }
                    it.mails.add(h)
                    if (it.title.isEmpty() || !replyRx.containsMatchIn(s)) it.title = clean.replace(Regex("^.*?(?:Instruction|EI)\\s*(?:no\\.?|#)?\\s*0*\\d{1,4}\\s*[-–:]*\\s*", I), "").replace(Regex("\\s*//.*$"), "").trim()
                    if (it.ref.isEmpty()) letterRef.find(s)?.let { r -> it.ref = r.value }
                    continue
                }
            }
            if ("MOM" in want && (momRx.containsMatchIn(s) || Regex("-MOM-|\\bMoM\\b", I).containsMatchIn(h.att)) && !(rx?.containsMatchIn(s) == true && !s.contains("-MOM-"))) {
                val p = projFrom(s + " " + h.att).ifBlank { h.project }
                val (series, num, held) = momOf(clean)
                val key = if (num >= 0) "MOM|$p|$series|$num" else "MOM|$p|" + norm(clean.replace(Regex("draft", I), ""))
                val it = g(key) { Item("MOM", p).also { x -> x.num = num; x.series = series; x.no = if (num >= 0) "MOM %03d".format(num) else "MOM"; x.via = "Meeting" } }
                it.mails.add(h)
                if (it.title.isEmpty() || (!replyRx.containsMatchIn(s) && !s.contains("draft", true))) it.title = series + (if (held.isNotEmpty()) " · held $held" else "")
                continue
            }
            val m = rx?.find(s)
            var handled = false
            if (m != null) {
                var no = m.groupValues[1].uppercase()
                var type = m.groupValues[2].uppercase()
                var ok = true
                if (type == "GEN") { if (!no.contains("-HSE-GEN-")) ok = false else type = "HSE" }
                if (ok && type == "LTR" && payRx.containsMatchIn(s)) type = "PAY"
                if (ok && type in want) {
                    var proj = no.substringBefore('-')
                    if (proj !in known) {
                        val k = known.firstOrNull { proj.endsWith(it) && it.length >= 2 }
                        if (k != null) { no = k + no.substring(proj.length); proj = k } else if (h.project.isNotBlank()) proj = h.project
                    }
                    val rev = m.groupValues[4].toIntOrNull() ?: 0
                    val parts = no.split('-')
                    val it = g("$type|$no|$rev") { Item(type, proj).also { x -> x.no = no; x.revNum = rev; x.disc = if (parts.size >= 4) parts[parts.size - 3] else "" } }
                    it.mails.add(h)
                    val title = s.substring(m.range.last + 1).replace(Regex("^[\\s_\\-–:]+"), "").replace(Regex("^\\(updated\\)[_\\s]*", I), "").replace(Regex("_with MZA Response\\s*$", I), "").trim(' ', '_', '-')
                    if (it.title.isEmpty() || (!replyRx.containsMatchIn(s) && !fwdRx.containsMatchIn(s))) it.title = title.ifBlank { it.title }
                    handled = true
                }
            }
            if (!handled) for ((code, wrx) in listOf("PAY" to payRx, "VO" to voRx, "AUTH" to authRx)) {
                if (code !in want || !wrx.containsMatchIn(s)) continue
                val p = projFrom(s).ifBlank { h.project }
                val it = g("$code|" + norm(s)) { Item(code, p).also { x -> x.no = code; x.via = "Email" } }
                it.mails.add(h)
                if (it.title.isEmpty() || !replyRx.containsMatchIn(s)) it.title = clean.trim()
                break
            }
        }
        for ((k, dh) in determ) groups["EI|$k"]?.let { it.why = "Engineer’s Determination " + dy(dh.date) }

        val now = System.currentTimeMillis()
        for (it in groups.values) {
            val df = def(it.type) ?: TYPES[0]
            val list = it.mails.sortedBy { x -> x.date }
            val tp = list.any { x -> x.fromAddr.contains("thinkproject", true) || x.body.contains("thinkproject", true) }
            it.via = if (tp) "Thinkproject" else it.via.ifBlank { "Outlook" }
            fun plain(x: PcLink.Hit) = !replyRx.containsMatchIn(x.subject) && !fwdRx.containsMatchIn(x.subject)
            val mzaIssues = df.close in setOf("mzaconfirm", "contractor") || (df.code in setOf("LTR", "VO", "AUTH", "MOM") && list.firstOrNull { x -> plain(x) }?.let { x -> isMza(x) } == true)
            it.sub = list.firstOrNull { x -> plain(x) && (mzaIssues || df.close == "received" || df.code in setOf("AUTH", "MOM") || !isMza(x)) }
                ?: if (df.code in setOf("EI", "MOM", "AUTH", "VO", "PAY", "LTR")) list.firstOrNull { x -> !fwdRx.containsMatchIn(x.subject) } else null
            if (tp) it.sub?.let { sh -> it.deadline = tpDeadline(sh.body) }
            val after = list.filter { x -> it.sub == null || x.date > it.sub!!.date }
            var waiting = ""
            fun close(x: PcLink.Hit, code: String) { it.rep = x; it.by = if (x.sent) "MZA (me)" else x.from; if (code.isNotEmpty()) it.code = code }
            when (df.close) {
                "code" -> for (x in after) {
                    val fromMza = isMza(x); val withResp = x.subject.contains("with MZA Response", true)
                    if (!fromMza && !withResp) continue
                    val done = withResp || if (tp) Regex("Approval completed[\\s\\S]{0,600}?\\byes\\b", I).containsMatchIn(x.body) || x.body.contains("Final step", true) || Regex("-C[1-5]\\.pdf", I).containsMatchIn(x.body + " " + x.att)
                        else closedFiles.any { f -> x.att.contains(f, true) } || Regex("code\\s*-?\\s*[1-5]", I).containsMatchIn(x.subject + " " + x.att) ||
                            (replyRx.containsMatchIn(x.subject) && (x.sent || Regex("\\bcode\\s*[1-5]\\b|approved|no objection|rejected|revise and resubmit", I).containsMatchIn(x.body.take(1500))))
                    if (done) { close(x, codeOf(x.body, x.att, x.subject)); break }
                    if (replyRx.containsMatchIn(x.subject) && fromMza) waiting = "Reviewing"
                }
                "reply" -> { val issuer = it.sub?.let { sh -> isMza(sh) } ?: false; after.firstOrNull { x -> isMza(x) != issuer && !fwdRx.containsMatchIn(x.subject) }?.let { x -> close(x, codeOf(x.body, x.att, x.subject)) } }
                "anyreply" -> after.firstOrNull { x -> !fwdRx.containsMatchIn(x.subject) && (it.sub == null || !x.fromAddr.equals(it.sub!!.fromAddr, true)) }?.let { x -> close(x, "") }
                "contractor" -> after.firstOrNull { x -> !isMza(x) && !fwdRx.containsMatchIn(x.subject) }?.let { x -> close(x, "") }
                "mzaconfirm" -> {
                    val cr = after.firstOrNull { x -> !isMza(x) && !fwdRx.containsMatchIn(x.subject) }
                    if (cr != null) {
                        val conf = after.firstOrNull { x -> x.date > cr.date && isMza(x) && Regex("\\bclosed?\\b|closure|accepted|verified|rectified", I).containsMatchIn(x.subject + " " + x.att + " " + x.body.take(1200)) }
                        if (conf != null) close(conf, "") else { waiting = "Replied"; it.why = "contractor replied ${d(cr.date)} · waiting for MZA to close it" }
                    }
                }
                "final" -> {
                    val fin = list.firstOrNull { x -> !Regex("draft", I).containsMatchIn(x.subject + " " + x.att) && !replyRx.containsMatchIn(x.subject) }
                    if (it.sub == null) it.sub = list.firstOrNull()
                    if (fin != null) { it.rep = fin; it.by = if (fin.sent) "MZA (me)" else fin.from } else waiting = "Draft"
                }
            }
            val pl = SubmittalPlace.of(it.title)
            it.tower = pl.tower; it.zone = pl.zone; it.level = pl.level; it.apt = pl.apt; it.levelNum = pl.levelNum
            val lim = PcLink.setting(c, "overdue.${it.type.lowercase()}", df.overdue.toString()).toIntOrNull() ?: df.overdue
            it.status = when {
                df.close == "received" -> "Received"
                it.rep != null -> "Closed"
                it.sub == null -> "?"
                else -> {
                    val due = it.deadline ?: (it.submitted!! + lim * 86_400_000L)
                    if (it.deadline == null) it.deadline = due
                    if (lim > 0 && now > due && waiting != "Draft") "Overdue" else waiting.ifEmpty { "Open" }
                }
            }
            val why = ArrayList<String>()
            it.sub?.let { sh -> why.add("${if (df.close in setOf("mzaconfirm", "contractor")) "issued" else "submitted"} ${d(sh.date)} by ${sh.from} (${it.via})") }
            if (it.ref.isNotEmpty()) why.add("ref " + it.ref)
            if (it.rep != null) why.add("${if (df.close == "final") "final minutes" else "closed"} ${d(it.reply)} by ${it.by}" + (if (it.code.isNotEmpty()) " · ${it.code}" else ""))
            else if (it.status == "Draft") why.add("draft only — final minutes not issued yet")
            else if (it.why.isEmpty() && it.sub != null && df.close != "received") why.add("no reply yet · due ${dy(it.deadline)}")
            if (it.why.isNotEmpty()) why.add(it.why)
            it.why = why.joinToString(" · ")
        }
        for ((_, gl) in groups.values.filter { def(it.type)?.find == "code" }.groupBy { it.type + "|" + it.no }) {
            val top = gl.maxOf { it.revNum }
            for (it in gl) {
                it.rev = (if (it.via == "Thinkproject" || it.project.equals("SLV", true)) "Rev" else "R") + "%02d".format(it.revNum)
                if (it.revNum < top && it.status in setOf("Open", "Overdue", "Reviewing")) { it.status = "Superseded"; it.why += " · a later revision exists" }
            }
        }
        val gaps = ArrayList<String>()
        for ((_, gl) in groups.values.filter { it.num >= 0 && it.type in setOf("EI", "MOM") }.groupBy { it.type + "|" + it.project + "|" + it.series }) {
            val nums = gl.map { it.num }.distinct().sorted()
            if (nums.size < 2) continue
            val miss = (nums.first()..nums.last()).filter { it !in nums }.take(20)
            if (miss.isNotEmpty()) gaps.add("${gl[0].project} ${if (gl[0].type == "EI") "EI" else gl[0].series}: no email for no. ${miss.joinToString(", ")}")
        }
        val out = ArrayList<Item>()
        for (it in groups.values) {
            val chk = ArrayList<String>()
            if (it.sub == null) chk.add("its first email is not on the phone for this period")
            if (it.type in setOf("WIR", "MIR") && (it.tower + it.zone + it.level).isEmpty()) chk.add("no tower / zone / level in the title")
            it.check = chk.joinToString("; ")
            val w = it.submitted ?: it.reply ?: continue
            if (w < o.from || w > o.to) continue
            if (o.projects.isNotEmpty() && o.projects.none { p -> p.equals(it.project, true) }) continue
            if (o.tower.isNotEmpty() && !it.tower.equals(o.tower, true) && !(o.tower == "TH" && it.tower.startsWith("TH"))) continue
            if (o.zone.isNotEmpty() && !it.zone.startsWith(o.zone, true)) continue
            if (o.disc.isNotEmpty() && !it.disc.equals(o.disc, true)) continue
            if ((o.levelFrom > -99 || o.levelTo < 999) && (it.levelNum == null || it.levelNum!! < o.levelFrom || it.levelNum!! > o.levelTo)) continue
            if (o.status.isNotEmpty() && !when (o.status) {
                    "Code3/4" -> it.code == "Code 3" || it.code == "Code 4"
                    "Open" -> it.status in setOf("Open", "Reviewing", "Replied", "Draft", "Overdue")
                    else -> it.status.equals(o.status, true)
                }) continue
            out.add(it)
        }
        out.sortByDescending { it.submitted ?: it.reply }
        return Result(out, ms.size, gaps, o, (System.currentTimeMillis() - t0) / 1000.0)
    }

    private fun tpDeadline(body: String): Long? {
        val i = body.indexOf("Deadline", ignoreCase = true); if (i < 0) return null
        val seg = body.substring(i, minOf(body.length, i + 1500))
        val ds = Regex("\\b(\\d{2})/(\\d{2})/(\\d{4})\\b").findAll(seg).toList()
        if (ds.isEmpty() || (ds.size == 1 && seg.contains("\t-/-\t"))) return null
        val g = ds[0].groupValues
        return Calendar.getInstance().apply { set(g[3].toInt(), g[2].toInt() - 1, g[1].toInt(), 23, 59, 0) }.timeInMillis
    }

    private fun codeOf(body: String, att: String, subj: String): String {
        val m = Regex("Approval result[\\s\\S]{0,300}?\\bCode\\s*([1-5])\\b", I).find(body)
            ?: Regex("[-_ ]C([1-5])(?:[-_ .]|\\.pdf|$)", I).find("$att $subj")
            ?: Regex("\\bCode\\s*[-:]?\\s*([1-5])\\b", I).find("$subj $att ${body.take(800)}")
        return if (m != null) "Code " + m.groupValues[1] else ""
    }

    private fun momOf(s: String): Triple<String, Int, String> {
        val held = Regex("held on\\s*(\\d{1,2}[-\\s][A-Za-z]{3,9}[-\\s]\\d{4})", I).find(s)?.groupValues?.get(1) ?: Regex("\\b(\\d{2}-\\d{2}-\\d{4})\\b").find(s)?.groupValues?.get(1) ?: ""
        val b = Regex("-MOM-0*(\\d{1,4})", I).find(s)
        val a = Regex("(?:^|\\|)\\s*([^|]*?)\\s*\\|?\\s*([A-Za-z/ &]*?)\\s*Meeting\\s*No\\.?\\s*0*(\\d{1,4})", I).find(s)
        val cc = Regex("\\bMoM\\s*(?:no\\.?|#)\\s*0*(\\d{1,4})", I).find(s)
        val k = Regex("(.*?)\\|?\\s*Kick[\\s-]?Off\\s+Meeting", I).find(s)
        var series: String; var num = -1
        when {
            b != null -> { num = b.groupValues[1].toInt(); series = Regex("Minutes of\\s+(.+?)\\s+Meeting", I).find(s)?.groupValues?.get(1)?.trim() ?: "Weekly Progress" }
            a != null -> { num = a.groupValues[3].toInt(); val w = a.groupValues[1].trim().trim('|').trim(); val kd = a.groupValues[2].trim(); series = (if (w.isNotEmpty() && w.length < 60) "$w · " else "") + kd.ifEmpty { "Meeting" } }
            cc != null -> { num = cc.groupValues[1].toInt(); series = Regex("\\|\\s*([^|]*?)\\s*(?:DRAFT\\s*)?MoM", I).find(s)?.groupValues?.get(1)?.trim() ?: "Meeting" }
            k != null -> { val w = k.groupValues[1].trim().trim('|').trim(); series = (if (w.isNotEmpty() && w.length < 70) "$w · " else "") + "Kick-off" }
            else -> series = s.split('|').map { it.trim() }.firstOrNull { it.isNotEmpty() && !Regex("draft|mom|minutes", I).containsMatchIn(it) } ?: "Meeting"
        }
        series = series.replace(Regex("\\s{2,}"), " ").trim(' ', '-', '·')
        return Triple(series.ifEmpty { "Meeting" }, num, held)
    }

    // ---------------------------------------------------------------- your words → a log (same as the PC)

    val TYPE_WORDS: List<Pair<String, Regex>> = listOf(
        "WIR" to "\\bWIRs?\\b|work inspections?", "MIR" to "\\bMIRs?\\b|material inspections?", "MAR" to "\\bMARs?\\b|material (approvals?|submittals?)",
        "MSS" to "\\bMSS\\b|method statements?", "SDS" to "\\bSDS\\b|\\bSHD\\b|shop ?drawings?", "SSM" to "\\bSSM\\b|\\bsamples?\\b", "TSR" to "\\bTSR\\b|test reports?",
        "PQS" to "\\bPQS\\b|pre-?qualifications?", "RFI" to "\\bRFIs?\\b|requests? for information", "RPT" to "\\bRPT\\b|\\bREP\\b|(weekly|monthly|progress) reports?",
        "HSE" to "\\bHSE\\b", "CAL" to "\\bCAL\\b|calculations?", "RTS" to "\\bRTS\\b|requests? to start", "HOV" to "\\bHOV\\b|hand-?over",
        "PLN" to "\\bPLN\\b|planning|programmes?", "LTR" to "\\bLTRs?\\b|\\bletters?\\b", "PAY" to "\\bpayments?\\b|\\bIPCs?\\b|\\bIPAs?\\b",
        "DSR" to "\\bDSRs?\\b|daily (site )?reports?", "VO" to "\\bVOs?\\b|\\bvariations?\\b",
        "AUTH" to "authorit(y|ies)|\\bADCD\\b|civil defen[cs]e|\\bNOCs?\\b|municipality|\\bAldar\\b|\\bMiral\\b",
        "SOR" to "\\bSORs?\\b|site observations?", "NCR" to "\\bNCRs?\\b|non-?conformances?", "EI" to "\\bEIs?\\b|engineer\\W{0,2}s? instructions?",
        "MOM" to "\\bMOMs?\\b|\\bminutes\\b|\\bmeetings?\\b|kick-?off").map { it.first to Regex(it.second, I) }
    private val listWords = Regex("\\b(log|register|list|show|which|how many|open|closed|overdue|pending|status|still|outstanding|issued|submitted|replied|not (yet )?(closed|replied|answered)|this (week|month)|last (week|month|\\d+ days)|all (the )?)\\b", I)
    private val exactDoc = Regex("\\b[A-Z0-9]{2,6}(?:-[A-Z0-9]{1,6}){2,5}-\\d{3,6}\\b")

    fun wanted(q: String): Boolean {
        if (exactDoc.containsMatchIn(q)) return false
        if (Regex("\\bsubmittal log\\b|\\b(WIR|MIR|MAR|RFI|EI|MOM|NCR|SOR) (log|register)\\b", I).containsMatchIn(q)) return true
        return TYPE_WORDS.any { it.second.containsMatchIn(q) } && listWords.containsMatchIn(q)
    }

    fun fromWords(q: String, project: String): Options {
        val o = Options()
        TYPE_WORDS.forEach { (code, rx) -> if (rx.containsMatchIn(q) && code !in o.types) o.types.add(code) }
        if ("RPT" in o.types && "DSR" in o.types && !Regex("\\b(weekly|monthly|progress)\\b", I).containsMatchIn(q)) o.types.remove("RPT")
        if (project.isNotBlank()) o.projects.add(project)
        listOf("SLV", "RS5").forEach { if (Regex("\\b$it\\b", I).containsMatchIn(q) && it !in o.projects) o.projects.add(it) }
        val pl = SubmittalPlace.of(q)
        o.tower = pl.tower; o.zone = pl.zone
        val lv = Regex("\\b(?:Level|L)\\s?(\\d{1,2})\\s*(?:to|-|–)\\s*(\\d{1,2})\\b", I).find(q)
        if (lv != null) { o.levelFrom = lv.groupValues[1].toInt(); o.levelTo = lv.groupValues[2].toInt() } else pl.levelNum?.let { o.levelFrom = it; o.levelTo = it }
        o.status = when {
            Regex("\\boverdue\\b", I).containsMatchIn(q) -> "Overdue"
            Regex("\\bopen\\b|pending|outstanding|still open|not (yet )?(closed|replied)", I).containsMatchIn(q) -> "Open"
            Regex("\\bclosed\\b", I).containsMatchIn(q) -> "Closed"
            else -> ""
        }
        val cal = Calendar.getInstance()
        val today0 = Calendar.getInstance().apply { set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0); set(Calendar.SECOND, 0) }.timeInMillis
        when {
            Regex("last month", I).containsMatchIn(q) -> { cal.timeInMillis = today0; cal.set(Calendar.DAY_OF_MONTH, 1); o.to = cal.timeInMillis - 1; cal.add(Calendar.MONTH, -1); o.from = cal.timeInMillis }
            Regex("this month", I).containsMatchIn(q) -> { cal.timeInMillis = today0; cal.set(Calendar.DAY_OF_MONTH, 1); o.from = cal.timeInMillis }
            Regex("this week", I).containsMatchIn(q) -> { cal.timeInMillis = today0; cal.set(Calendar.DAY_OF_WEEK, Calendar.MONDAY); o.from = cal.timeInMillis }
            Regex("last (\\d{1,3}) days", I).find(q) != null -> o.from = today0 - Regex("last (\\d{1,3}) days", I).find(q)!!.groupValues[1].toInt() * 86_400_000L
        }
        return o
    }

    /** A follow-up (“only the overdue ones”, “now tower 2”, “level 5 to 8”, “this month”) applied to the last log; null when it is not one. */
    fun follow(q: String, last: Options): Options? {
        if (q.trim().split(Regex("\\s+")).size > 10 || TYPE_WORDS.any { it.second.containsMatchIn(q) }) return null
        if (!Regex("\\b(only|now|just|instead|also|filter|overdue|closed|open|pending|tower|zone|level|floor|townhouses?|this (week|month)|last (week|month|\\d+ days)|code)\\b|\\bT\\d\\b|\\bL\\d{1,2}\\b", I).containsMatchIn(q)) return null
        val o = last.copy(); val n = fromWords(q, "")
        if (n.tower.isNotEmpty()) o.tower = n.tower
        if (n.zone.isNotEmpty()) o.zone = n.zone
        if (n.levelFrom > -99) { o.levelFrom = n.levelFrom; o.levelTo = n.levelTo }
        if (n.status.isNotEmpty()) o.status = n.status
        if (Regex("all towers|any tower", I).containsMatchIn(q)) o.tower = ""
        if (Regex("this (week|month)|last (week|month|\\d+ days)", I).containsMatchIn(q)) { o.from = n.from; o.to = n.to }
        return o
    }

    fun describe(o: Options): String {
        val p = ArrayList<String>()
        p.add(if (o.types.isEmpty()) "all types" else o.types.joinToString(", "))
        if (o.projects.isNotEmpty()) p.add(o.projects.joinToString("+"))
        if (o.tower.isNotEmpty()) p.add(o.tower); if (o.zone.isNotEmpty()) p.add(o.zone)
        if (o.levelFrom > -99) p.add(if (o.levelFrom == o.levelTo) "L${o.levelFrom}" else "L${o.levelFrom}–${o.levelTo}")
        if (o.status.isNotEmpty()) p.add(o.status.lowercase())
        p.add(dy(o.from) + " – " + dy(o.to))
        return p.joinToString(" · ")
    }

    // ---------------------------------------------------------------- ⬇ Excel (Log · Open & overdue · Check · Summary · About)

    fun excel(c: Context, r: Result): File {
        val dir = File(PcLink.dir(c), "fetched").apply { mkdirs() }
        val title = (if (r.opt.types.size in 1..4) r.opt.types.joinToString("-") else "Submittal") + " log " + (if (r.opt.projects.isEmpty()) "all projects" else r.opt.projects.joinToString("+"))
        val f = File(dir, "$title — ${SimpleDateFormat("dd MMM yyyy HHmm", Locale.UK).format(java.util.Date())}.xlsx")
        val head = listOf("Project", "Via", "Type", "Document no.", "Rev", "Disc.", "Title", "Tower", "Zone", "Level", "Apt / TH", "Submitted", "Deadline", "Reply", "Replied by", "Code", "Status", "Why")
        fun row(i: Item) = listOf(i.project, i.via, i.type, i.no, i.rev, i.disc, i.title, i.tower, i.zone, i.level, i.apt, d(i.submitted), dy(i.deadline), d(i.reply), i.by, i.code, i.status, i.why)
        val note = describe(r.opt) + " · ${r.items.size} item(s) from ${r.mails} emails on the phone · Designed by A.HAMROUNI"
        val sheets = ArrayList<Triple<String, List<String>?, List<List<String>>>>()
        sheets.add(Triple("Log", head, r.items.map { row(it) }))
        sheets.add(Triple("Open & overdue", head, r.items.filter { it.status in setOf("Open", "Overdue", "Reviewing", "Replied", "Draft") }.sortedBy { it.submitted ?: 0L }.map { row(it) }))
        sheets.add(Triple("Check", head + "Why it is here", r.gaps.map { g -> listOf("", "", "", "", "", "", "⚠ $g") + List(head.size - 7) { "" } + "missing number in the register" } +
            r.items.filter { it.check.isNotEmpty() }.map { row(it) + it.check }))
        val sum = ArrayList<List<String>>()
        val sts = listOf("Closed", "Open", "Reviewing", "Replied", "Draft", "Overdue", "Superseded", "Received")
        sum.add(listOf("") + sts + "Total")
        r.items.groupBy { "${it.project} · ${it.type}" }.toSortedMap().forEach { (k, l) -> sum.add(listOf(k) + sts.map { s -> l.count { it.status == s }.let { n -> if (n > 0) n.toString() else "" } } + l.size.toString()) }
        sheets.add(Triple("Summary", null, sum))
        sheets.add(Triple("About", null, listOf(listOf("Made by", "Nova Enhance on the phone · 📋 Submittal log · Designed by A.HAMROUNI"), listOf("Your choices", describe(r.opt)),
            listOf("Rules", "🧠 Nova Brain on your PC (📋 Submittal logs), received with the hourly update"))))
        writeBook(f, title, note, sheets)
        return f
    }

    private fun x(s: String) = s.replace(Regex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]"), "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")
    private fun col(i0: Int): String { var i = i0 + 1; var s = ""; while (i > 0) { val m = (i - 1) % 26; s = ('A' + m) + s; i = (i - 1) / 26 }; return s }

    private fun writeBook(f: File, title: String, note: String, sheets: List<Triple<String, List<String>?, List<List<String>>>>) {
        val h = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        ZipOutputStream(f.outputStream()).use { z ->
            fun w(name: String, xml: String) { z.putNextEntry(ZipEntry(name)); z.write(xml.toByteArray(Charsets.UTF_8)); z.closeEntry() }
            w("[Content_Types].xml", h + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                sheets.indices.joinToString("") { "<Override PartName=\"/xl/worksheets/sheet${it + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" } + "</Types>")
            w("_rels/.rels", h + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")
            w("xl/_rels/workbook.xml.rels", h + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                sheets.indices.joinToString("") { "<Relationship Id=\"rId${it + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet${it + 1}.xml\"/>" } +
                "<Relationship Id=\"rId${sheets.size + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>")
            w("xl/workbook.xml", h + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                sheets.mapIndexed { i, s -> "<sheet name=\"${x(s.first)}\" sheetId=\"${i + 1}\" r:id=\"rId${i + 1}\"/>" }.joinToString("") + "</sheets></workbook>")
            w("xl/styles.xml", h + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"4\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><color rgb=\"FF2B2F3A\"/><name val=\"Calibri\"/></font><font><i/><sz val=\"9\"/><color rgb=\"FF6B7385\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2B2F3A\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                "<cellXfs count=\"4\"><xf/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/><xf fontId=\"2\" applyFont=\"1\"/><xf fontId=\"3\" applyFont=\"1\"/></cellXfs></styleSheet>")
            sheets.forEachIndexed { si, s ->
                val sb = StringBuilder(h + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">")
                if (s.second != null) sb.append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"4\" topLeftCell=\"A5\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>")
                sb.append("<sheetData>")
                fun r(n: Int, cells: List<String>, st: Int) {
                    sb.append("<row r=\"$n\">"); cells.forEachIndexed { ci, v -> sb.append("<c r=\"${col(ci)}$n\" t=\"inlineStr\" s=\"$st\"><is><t xml:space=\"preserve\">${x(v)}</t></is></c>") }; sb.append("</row>")
                }
                r(1, listOf(if (si == 0) title else s.first), 2); r(2, listOf(note), 3)
                var n = 4
                s.second?.let { r(n++, it, 1) }
                s.third.forEach { r(n++, it, 0) }
                sb.append("</sheetData>")
                s.second?.let { sb.append("<autoFilter ref=\"A4:${col(it.size - 1)}${maxOf(5, n - 1)}\"/>") }
                sb.append("</worksheet>")
                w("xl/worksheets/sheet${si + 1}.xml", sb.toString())
            }
        }
    }
}

/** Tower · zone · level · apartments read from a title (the same reading as the PC). */
object SubmittalPlace {
    data class P(val tower: String, val zone: String, val level: String, val apt: String, val levelNum: Int?)
    private val I = RegexOption.IGNORE_CASE
    fun of(title: String): P {
        val s = " " + title.replace('_', ' ') + " "
        var t = ""; var z = ""; var l = ""; var a = ""; var n: Int? = null
        Regex("\\b(?:Tower|T)\\s?[-_]?\\s?(\\d)\\b", I).find(s)?.let { t = "T" + it.groupValues[1] }
            ?: Regex("\\bTH\\s?([\\d ,&]*\\d)").find(s)?.let { t = "TH " + it.groupValues[1].replace(Regex("\\s"), "") }
        Regex("\\b(?:Zone|Z)\\s?[-_]?\\s?0?(\\d)(?:\\s?&\\s?(\\d))?\\b", I).find(s)?.let { z = "Z" + it.groupValues[1] + (if (it.groupValues[2].isNotEmpty()) "–" + it.groupValues[2] else "") }
        val y = Regex("\\b(?:Level|L)\\s?[-_]?\\s?(\\d{1,2})\\s*(?:to|-|–)\\s*(\\d{1,2})\\b", I).find(s)
        if (y != null) { l = "L${y.groupValues[1].toInt()}–${y.groupValues[2].toInt()}"; n = y.groupValues[1].toInt() }
        else {
            val x = Regex("\\b(?:Level|L)\\s?[-_]?\\s?(\\d{1,2}|GF|FF|UG|B\\d)\\b", I).find(s) ?: Regex("\\b(\\d{1,2})(?:st|nd|rd|th)\\s+Floor", I).find(s)
            if (x != null) {
                val v = x.groupValues[1].uppercase()
                v.toIntOrNull()?.let { l = "L$it"; n = it } ?: run { l = v; n = when (v) { "GF" -> 0; "FF" -> 1; "UG" -> -1; else -> if (v.startsWith("B")) v.drop(1).toIntOrNull()?.let { -it } else null } }
            } else for ((w, v, k) in listOf(Triple("ground floor", "GF", 0), Triple("first floor", "L1", 1), Triple("attic", "Attic", 99), Triple("roof", "Roof", 100), Triple("podium", "Podium", 1), Triple("basement", "B", -1)))
                if (s.contains(w, true)) { l = v; n = k; break }
        }
        if (s.contains("podium", true) && l.isNotEmpty() && l != "Podium") l += " podium"
        Regex("\\b(?:apt|apartment)s?\\.?\\s*-?\\s*(\\d{3,4}(?:\\s*(?:,|&|-|to)\\s*\\d{3,4})*)", I).find(s)?.let { a = it.groupValues[1].replace(Regex("\\s+"), " ").trim() }
        return P(t, z, l, a, n)
    }
}
