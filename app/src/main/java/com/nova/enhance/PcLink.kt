package com.nova.enhance

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.util.zip.GZIPInputStream
import java.util.zip.GZIPOutputStream
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 🖥 My PC (2.0): the phone keeps a copy of Nova's data from your PC — every email with full text, attachment text, brief,
 * email projects, the project-file catalogue, ⭐ starred files, and the same "brain" (profile, words, vocabulary, names).
 * Everything arrives encrypted (AES-256-GCM, key from the pairing QR code) through 📶 office Wi-Fi or 🔁 your private
 * GitHub relay, and is searched on the phone itself — offline. Designed by A.HAMROUNI
 */
object PcLink {
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_pc", Context.MODE_PRIVATE)
    fun dir(c: Context): File = File(c.filesDir, "MyPC").apply { mkdirs() }

    // ------------------------------------------------------------------ pairing

    fun paired(c: Context): Boolean = sp(c).getString("key", "").orEmpty().length > 20
    fun pcName(c: Context): String = sp(c).getString("pc", "").orEmpty()
    private fun key(c: Context): ByteArray = Base64.decode(sp(c).getString("key", ""), Base64.DEFAULT)
    private fun ips(c: Context): List<String> = sp(c).getString("ips", "").orEmpty().split(',').filter { it.isNotBlank() }
    private fun port(c: Context): Int = sp(c).getInt("port", 47323)
    private fun relay(c: Context): String = sp(c).getString("relay", "").orEmpty()
    private fun relayToken(c: Context): String = sp(c).getString("rt", "").orEmpty()
    fun hasRelay(c: Context) = relay(c).contains('/') && relayToken(c).length > 10

    /** The QR code text (novaenhance://pair?d=… or the JSON itself, pasted). */
    fun pair(c: Context, text: String): String {
        var t = text.trim()
        if (t.startsWith("novaenhance://", true)) {
            val d = android.net.Uri.parse(t).getQueryParameter("d") ?: throw Exception("This is not a Nova pairing code")
            t = String(Base64.decode(d, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING), Charsets.UTF_8)
        }
        val o = try { JSONObject(t) } catch (_: Exception) { throw Exception("This is not a Nova pairing code") }
        if (o.optInt("nova") < 2 || o.optString("key").length < 20) throw Exception("This is not a Nova pairing code")
        val ips = o.optJSONArray("ip")?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
        val old = sp(c).getString("key", "")
        sp(c).edit().putString("key", o.getString("key")).putString("pc", o.optString("pc")).putString("ips", ips.joinToString(","))
            .putInt("port", o.optInt("port", 47323)).putString("relay", o.optString("relay")).putString("rt", o.optString("rt"))
            .putLong("paired_at", System.currentTimeMillis()).apply()
        if (old != o.getString("key")) wipe(c)
        schedule(c)   // safe: never throws
        return "✓ Paired with ${o.optString("pc")}"
    }

    fun unpair(c: Context) { sp(c).edit().clear().apply(); wipe(c); (c.getSystemService(Context.JOB_SCHEDULER_SERVICE) as JobScheduler).cancel(JOB_ID) }

    private fun wipe(c: Context) {
        Db.close(); c.deleteDatabase("mypc.db"); dir(c).deleteRecursively(); dir(c)
        sp(c).edit().remove("got").remove("fullAt").remove("manifest").apply()
    }

    // ------------------------------------------------------------------ encryption: "NES1" + nonce(12) + tag(16) + data

    fun open(key: ByteArray, box: ByteArray): ByteArray {
        if (box.size < 32 || String(box, 0, 4, Charsets.US_ASCII) != "NES1") throw Exception("not a Nova sync file")
        val ci = Cipher.getInstance("AES/GCM/NoPadding")
        ci.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, box, 4, 12))
        val joined = ByteArray(box.size - 32 + 16)
        System.arraycopy(box, 32, joined, 0, box.size - 32)
        System.arraycopy(box, 16, joined, box.size - 32, 16)
        return ci.doFinal(joined)
    }

    fun seal(key: ByteArray, plain: ByteArray): ByteArray {
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val ci = Cipher.getInstance("AES/GCM/NoPadding")
        ci.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        val out = ci.doFinal(plain)                       // cipher + tag(16)
        val box = ByteArray(32 + out.size - 16)
        "NES1".toByteArray(Charsets.US_ASCII).copyInto(box, 0)
        nonce.copyInto(box, 4)
        out.copyInto(box, 16, out.size - 16, out.size)
        out.copyInto(box, 32, 0, out.size - 16)
        return box
    }

    fun gunzip(b: ByteArray): ByteArray = GZIPInputStream(ByteArrayInputStream(b)).use { it.readBytes() }
    fun gzip(b: ByteArray): ByteArray = ByteArrayOutputStream().also { o -> GZIPOutputStream(o).use { it.write(b) } }.toByteArray()
    private fun text(c: Context, box: ByteArray) = String(gunzip(open(key(c), box)), Charsets.UTF_8)

    private fun pairToken(c: Context): String {
        val mac = Mac.getInstance("HmacSHA256"); mac.init(SecretKeySpec(key(c), "HmacSHA256"))
        return mac.doFinal("nova-pair".toByteArray()).joinToString("") { "%02x".format(it) }.substring(0, 32)
    }

    // ------------------------------------------------------------------ transport

    private fun http(url: String, method: String = "GET", headers: Map<String, String> = emptyMap(), body: ByteArray? = null,
                     connectMs: Int = 15000, readMs: Int = 120000): Pair<Int, ByteArray> {
        val h = URL(url).openConnection() as HttpURLConnection
        h.requestMethod = method; h.connectTimeout = connectMs; h.readTimeout = readMs; h.instanceFollowRedirects = true
        for ((k, v) in headers) h.setRequestProperty(k, v)
        if (body != null) { h.doOutput = true; h.outputStream.use { it.write(body) } }
        val code = h.responseCode
        val data = (if (code in 200..299) h.inputStream else h.errorStream)?.use { it.readBytes() } ?: ByteArray(0)
        h.disconnect()
        return code to data
    }

    /** 📶 The PC on the office Wi-Fi, if the phone can reach it now. */
    private fun wifi(c: Context): String? {
        for (ip in ips(c)) {
            try {
                val (code, _) = http("http://$ip:${port(c)}/hello", headers = mapOf("X-Nova-Pair" to pairToken(c), "X-Nova-Phone" to android.os.Build.MODEL),
                    connectMs = 1500, readMs = 4000)
                if (code == 200) return "http://$ip:${port(c)}"
            } catch (_: Exception) { }
        }
        return null
    }

    private fun gh(c: Context, path: String, method: String = "GET", json: JSONObject? = null, raw: Boolean = false): Pair<Int, ByteArray> =
        http("https://api.github.com/repos/${relay(c)}/contents/$path", method,
            mapOf("Authorization" to "Bearer ${relayToken(c)}", "Accept" to if (raw) "application/vnd.github.raw" else "application/vnd.github+json",
                "X-GitHub-Api-Version" to "2022-11-28", "User-Agent" to "NovaEnhance-Android", "Content-Type" to "application/json"),
            json?.toString()?.toByteArray())

    // ------------------------------------------------------------------ the copy on the phone

    object Db {
        private var helper: Helper? = null
        fun get(c: Context): SQLiteDatabase = synchronized(this) { (helper ?: Helper(c.applicationContext).also { helper = it }).writableDatabase }
        fun close() = synchronized(this) { try { helper?.close() } catch (_: Exception) { }; helper = null }

        class Helper(c: Context) : SQLiteOpenHelper(c, "mypc.db", null, 1) {
            override fun onCreate(db: SQLiteDatabase) {
                db.execSQL("CREATE TABLE mail(id INTEGER PRIMARY KEY, e TEXT, d INTEGER, s INTEGER, f TEXT, fn TEXT, fa TEXT, too TEXT, cc TEXT, sub TEXT, an TEXT, p TEXT, b TEXT, at TEXT)")
                db.execSQL("CREATE INDEX mail_d ON mail(d)")
                db.execSQL("CREATE TABLE files(id INTEGER PRIMARY KEY, path TEXT, size INTEGER, m INTEGER, k TEXT, t TEXT)")
                fts(db, "mail_fts", "sub, b, fn, too, cc, an, at", "mail")
                fts(db, "files_fts", "path, t", "files")
            }
            private fun fts(db: SQLiteDatabase, name: String, cols: String, content: String) {
                try { db.execSQL("CREATE VIRTUAL TABLE $name USING fts4($cols, content=\"$content\", tokenize=unicode61)") }
                catch (_: Exception) { db.execSQL("CREATE VIRTUAL TABLE $name USING fts4($cols, content=\"$content\")") }
            }
            override fun onUpgrade(db: SQLiteDatabase, o: Int, n: Int) {}
        }
    }

    // ------------------------------------------------------------------ ⟳ sync (hourly job, "Update now", and right after pairing)

    @Volatile var busy = false
    fun lastSync(c: Context): Long = sp(c).getLong("last_sync", 0)
    fun lastWay(c: Context): String = sp(c).getString("last_way", "").orEmpty()
    fun lastNote(c: Context): String = sp(c).getString("last_note", "").orEmpty()
    fun manifest(c: Context): JSONObject? = try { JSONObject(sp(c).getString("manifest", "") ?: "") } catch (_: Exception) { null }

    fun sync(c: Context, progress: (String) -> Unit = {}): String {
        if (!paired(c)) return "Not paired — scan the QR code on your PC first"
        if (busy) return "Already updating…"
        busy = true
        try {
            progress("Looking for your PC on this Wi-Fi…")
            val base = wifi(c)
            val way = if (base != null) "📶 office Wi-Fi" else if (hasRelay(c)) "🔁 relay" else throw Exception("Your PC is not on this Wi-Fi and no relay is set up")
            fun fetch(name: String): ByteArray? =
                if (base != null) {
                    val (code, b) = http("$base/nes/" + name.split('/').joinToString("/") { java.net.URLEncoder.encode(it, "UTF-8").replace("+", "%20") },
                        headers = mapOf("X-Nova-Pair" to pairToken(c)), readMs = 300000)
                    if (code == 200) b else null
                } else {
                    val (code, b) = gh(c, "sync/$name", raw = true)
                    if (code == 200) b else null
                }
            progress("Reading what your PC has ($way)…")
            val man = JSONObject(text(c, fetch("manifest.nes") ?: throw Exception("Your PC has not prepared the phone copy yet — Nova on the PC: Settings → 📱 Phone link → ⟳ Update the phone now")))
            val p = sp(c)
            val got = try { JSONObject(p.getString("got", "{}")!!) } catch (_: Exception) { JSONObject() }
            if (p.getString("fullAt", "") != man.optString("fullAt")) {
                // A new full copy on the PC: start again (⭐ starred files stay until replaced).
                val db = Db.get(c)
                db.execSQL("DELETE FROM mail"); db.execSQL("INSERT INTO mail_fts(mail_fts) VALUES('rebuild')")
                db.execSQL("DELETE FROM files"); db.execSQL("INSERT INTO files_fts(files_fts) VALUES('rebuild')")
                got.keys().asSequence().toList().forEach { got.remove(it) }
            }
            val files = man.optJSONArray("files") ?: JSONArray()
            var done = 0; var missing = 0
            val todo = (0 until files.length()).map { files.getJSONObject(it) }
                .filter { got.optString(it.optString("n")) != "${it.optLong("b")}:${it.optLong("m")}" }
            for (f in todo) {
                val n = f.optString("n")
                progress("⬇ ${++done}/${todo.size} · $n")
                val box = fetch(n)
                if (box == null) { missing++; continue }
                try { importFile(c, n, box) } catch (e: Exception) { ErrorLog.add(c, "My PC", "sync", n, 0, e.message ?: "", "") ; continue }
                got.put(n, "${f.optLong("b")}:${f.optLong("m")}")
                p.edit().putString("got", got.toString()).apply()
            }
            p.edit().putString("fullAt", man.optString("fullAt")).putString("manifest", man.toString()).putLong("last_sync", System.currentTimeMillis())
                .putString("last_way", way)
                .putString("last_note", if (missing > 0) "$missing file(s) wait for the office Wi-Fi (the full copy is too big for the relay)" else "").apply()
            return "✓ Updated · ${todo.size - missing} file(s) via $way" + (if (missing > 0) " · $missing wait for the office Wi-Fi" else "")
        } catch (e: Exception) {
            sp(c).edit().putString("last_note", "⚠ " + (e.message ?: "failed")).apply()
            return "⚠ " + (e.message ?: "failed")
        } finally { busy = false }
    }

    private fun importFile(c: Context, name: String, box: ByteArray) {
        val d = dir(c)
        when {
            name.startsWith("email/") -> importLines(c, box) { db, o ->
                val id = o.optLong("i")
                db.execSQL("DELETE FROM mail_fts WHERE docid=$id")
                val v = ContentValues().apply {
                    put("id", id); put("e", o.optString("e")); put("d", o.optLong("d")); put("s", if (o.optBoolean("s")) 1 else 0); put("f", o.optString("f"))
                    put("fn", o.optString("fn")); put("fa", o.optString("fa")); put("too", o.optString("to")); put("cc", o.optString("cc")); put("sub", o.optString("sub"))
                    put("an", o.optString("an")); put("p", o.optString("p")); put("b", o.optString("b")); put("at", o.optString("at"))
                }
                db.insertWithOnConflict("mail", null, v, SQLiteDatabase.CONFLICT_REPLACE)
                db.execSQL("INSERT INTO mail_fts(docid, sub, b, fn, too, cc, an, at) SELECT id, sub, b, fn, too, cc, an, at FROM mail WHERE id=$id")
            }
            name.startsWith("files/") -> importLines(c, box) { db, o ->
                val id = o.optLong("i")
                db.execSQL("DELETE FROM files_fts WHERE docid=$id")
                val v = ContentValues().apply { put("id", id); put("path", o.optString("path")); put("size", o.optLong("size")); put("m", o.optLong("m")); put("k", o.optString("k")); put("t", o.optString("t")) }
                db.insertWithOnConflict("files", null, v, SQLiteDatabase.CONFLICT_REPLACE)
                db.execSQL("INSERT INTO files_fts(docid, path, t) SELECT id, path, t FROM files WHERE id=$id")
            }
            name == "projects.nes" -> {
                val o = JSONObject(text(c, box))
                File(d, "projects.json").writeText(o.optJSONArray("projects")?.toString() ?: "[]")
                val db = Db.get(c)
                db.beginTransaction()
                try {
                    for (line in o.optString("map").split('\n')) {
                        val t = line.indexOf('\t'); if (t <= 0) continue
                        db.execSQL("UPDATE mail SET p=? WHERE id=?", arrayOf<Any>(line.substring(t + 1), line.substring(0, t).toLong()))
                    }
                    db.setTransactionSuccessful()
                } finally { db.endTransaction() }
            }
            name == "brief.nes" || name == "brain.nes" || name == "starred.nes" -> File(d, name.removeSuffix(".nes") + ".json").writeText(text(c, box))
            name.startsWith("starred/") -> {
                // The real file: its name comes from the ⭐ list.
                val idx = try { JSONArray(File(d, "starred.json").readText()) } catch (_: Exception) { JSONArray() }
                val meta = (0 until idx.length()).map { idx.getJSONObject(it) }.firstOrNull { it.optString("name") == name }
                val file = (meta?.optString("file") ?: name.substringAfterLast('/')).replace(Regex("[\\\\/:*?\"<>|]"), "_")
                File(d, "starred").mkdirs()
                File(File(d, "starred"), file).writeBytes(gunzip(open(key(c), box)))
            }
        }
    }

    private fun importLines(c: Context, box: ByteArray, row: (SQLiteDatabase, JSONObject) -> Unit) {
        val db = Db.get(c)
        db.beginTransaction()
        try {
            text(c, box).lineSequence().filter { it.isNotBlank() }.forEach { l -> try { row(db, JSONObject(l)) } catch (_: Exception) { } }
            db.setTransactionSuccessful()
        } finally { db.endTransaction() }
    }

    // ------------------------------------------------------------------ counts and sizes

    fun count(c: Context, table: String): Long = try { Db.get(c).compileStatement("SELECT COUNT(*) FROM $table").simpleQueryForLong() } catch (_: Exception) { 0 }
    fun oldest(c: Context): Long = try { Db.get(c).compileStatement("SELECT IFNULL(MIN(d),0) FROM mail").simpleQueryForLong() } catch (_: Exception) { 0 }
    fun sizeOnPhone(c: Context): Long = (c.getDatabasePath("mypc.db").length() + dir(c).walkTopDown().filter { it.isFile }.sumOf { it.length() })
    fun json(c: Context, name: String): String? = File(dir(c), name).takeIf { it.exists() }?.readText()

    // ------------------------------------------------------------------ 🔍 search on the phone (the same ideas as the PC)

    data class Hit(val id: Long, val date: Long, val sent: Boolean, val from: String, val fromAddr: String, val to: String, val cc: String,
                   val subject: String, val att: String, val project: String, val folder: String, var why: String = "", var body: String = "",
                   var status: String = "", var first: String = "") {
        val doc: Triple<String, String, String> by lazy { DocCodes.of(subject.ifBlank { att }) }
    }

    private val instruction = Regex("^(show|find|search|look|check|list|give|get|bring|display|all|every|each|any|old|older|oldest|earliest|new|newer|newest|latest|recent|recently|approved|approve|approval|approvals|accepted|please|me|my|kindly|history|the|a|an|of|in|for|from|to|and|with|on|about|project|emails?|mails?|which|that|is|are|was|were|i|need|want|do|did|has|have)$", RegexOption.IGNORE_CASE)
    private val approval = Regex("\\bcode\\s*[-:#]?\\s*(1|2|a|b)\\b|\\bapproved\\b|\\bapproval\\b|no objection|signed\\s+(sample\\s+|material\\s+)?submittal|with\\s+(the\\s+)?(client|engineer|consultant)'?s?\\s+comments", RegexOption.IGNORE_CASE)

    /** Known short forms: one idea = any of these (the PC uses the same list). */
    private val abbrev = mapOf(
        "fhc" to listOf("fhc", "\"fire hose cabinet\"", "\"fire hose reel\"", "\"hose reel\"", "fhrc"),
        "ssm" to listOf("ssm", "sample", "samples"), "sds" to listOf("sds", "\"shop drawing\"", "\"shop drawings\""),
        "rfi" to listOf("rfi", "\"request for information\""), "mar" to listOf("mar", "\"material approval\"", "\"material submittal\""),
        "mir" to listOf("mir", "\"material inspection\""), "wir" to listOf("wir", "\"work inspection\"", "\"inspection request\""),
        "mss" to listOf("mss", "\"method statement\""), "ncr" to listOf("ncr", "\"non conformance\""), "ls" to listOf("landscape", "ls"),
        "id" to listOf("\"interior design\"", "id"), "ar" to listOf("architectural", "architecture", "ar"))

    fun search(c: Context, question: String, project: String = ""): Pair<List<Hit>, String> {
        val q = question.lowercase()
        val wantsApproved = Regex("\\bapprov").containsMatchIn(q)
        val wantsOld = Regex("\\b(old|older|oldest|earliest|first|history)\\b").containsMatchIn(q)
        val phrases = Regex("\"([^\"]+)\"").findAll(question).map { it.groupValues[1].trim() }.filter { it.isNotEmpty() }.toList()
        var rest = question.replace(Regex("\"[^\"]*\""), " ")
        // "fire hose cabinet(s)" anywhere = the FHC idea
        if (Regex("fire\\s+hose", RegexOption.IGNORE_CASE).containsMatchIn(rest)) rest = rest.replace(Regex("fire\\s+hose(\\s+reel)?(\\s+cabinets?)?", RegexOption.IGNORE_CASE), " fhc ")
        if (Regex("shop\\s+drawings?", RegexOption.IGNORE_CASE).containsMatchIn(rest)) rest = rest.replace(Regex("shop\\s+drawings?", RegexOption.IGNORE_CASE), " sds ")
        val words = Regex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\-_.]*").findAll(rest).map { it.value }
            .filter { it.length > 1 && !instruction.matches(it) && !it.equals(project, true) }.distinct().take(8).toList()
        val groups = ArrayList<List<String>>()
        phrases.forEach { groups.add(listOf("\"" + it.replace("\"", "") + "\"")) }
        for (w in words) {
            val lw = w.lowercase().trimEnd('s').let { if (abbrev.containsKey(it)) it else w.lowercase() }
            groups.add(abbrev[lw] ?: listOf(if (w.contains('-') || w.contains('_') || w.contains('.')) "\"" + w.replace(Regex("[\\-_.]"), " ") + "\"" else "$w*"))
        }
        val db = Db.get(c)
        val where = ArrayList<String>(); val args = ArrayList<String>()
        for (g in groups) { where.add("m.id IN (SELECT docid FROM mail_fts WHERE mail_fts MATCH ?)"); args.add(g.joinToString(" OR ")) }
        if (project.isNotBlank()) { where.add("(m.p = ? OR m.p = '' OR m.p IS NULL)"); args.add(project) }
        val sql = "SELECT id, d, s, fn, fa, too, cc, sub, an, p, f, substr(b,1,600) FROM mail m" + (if (where.isEmpty()) "" else " WHERE " + where.joinToString(" AND ")) + " ORDER BY d DESC LIMIT 3000"
        val hits = ArrayList<Pair<Hit, Double>>()
        // A phrase may have up to 2 words in between ("fire hose cabinet" finds "fire hose reel cabinet").
        val rx = groups.map { g -> g.map { t -> Regex(t.trim('"', '*').split(' ').filter { it.isNotEmpty() }.joinToString("[\\s\\-_]*(?:\\w+[\\s\\-_]+){0,2}") { Regex.escape(it) }, RegexOption.IGNORE_CASE) } }
        db.rawQuery(sql, args.toTypedArray()).use { cu ->
            while (cu.moveToNext()) {
                val h = Hit(cu.getLong(0), cu.getLong(1), cu.getInt(2) != 0, cu.getString(3) ?: "", cu.getString(4) ?: "", cu.getString(5) ?: "", cu.getString(6) ?: "",
                    cu.getString(7) ?: "", cu.getString(8) ?: "", cu.getString(9) ?: "", cu.getString(10) ?: "", first = cu.getString(11) ?: "")
                var score = 0.0; val why = ArrayList<String>()
                for ((gi, g) in rx.withIndex()) {
                    val inSub = g.any { it.containsMatchIn(h.subject) }; val inAtt = g.any { it.containsMatchIn(h.att) }
                    score += if (inSub) 3.0 else if (inAtt) 2.5 else 0.5
                    if (inSub || inAtt) why.add("${groups[gi][0].trim('"', '*')} in ${if (inSub) "subject" else "attachment"}")
                }
                if (wantsApproved && approval.containsMatchIn(h.subject + " " + h.att + " " + h.first)) { score += 1.5; why.add("approval") }
                h.why = why.joinToString(" · ")
                hits.add(h to score)
            }
        }
        var list = hits.sortedWith(compareByDescending<Pair<Hit, Double>> { it.second }.thenByDescending { it.first.date }).take(200).map { it.first }
        if (wantsOld) list = list.sortedBy { it.date }
        val explain = "✦ " + (groups.joinToString(" · ") { it.first().trim('"', '*') }.ifBlank { "everything" }) +
            (if (wantsApproved) " · approved (Code 1/2…)" else "") + (if (project.isNotBlank()) " · $project" else "")
        return list to explain
    }

    fun mail(c: Context, id: Long): Hit? = Db.get(c).rawQuery("SELECT id, d, s, fn, fa, too, cc, sub, an, p, f, b, at FROM mail WHERE id=?", arrayOf(id.toString())).use { cu ->
        if (!cu.moveToFirst()) null else Hit(cu.getLong(0), cu.getLong(1), cu.getInt(2) != 0, cu.getString(3) ?: "", cu.getString(4) ?: "", cu.getString(5) ?: "",
            cu.getString(6) ?: "", cu.getString(7) ?: "", cu.getString(8) ?: "", cu.getString(9) ?: "", cu.getString(10) ?: "", body = cu.getString(11) ?: "")
    }

    data class FileHit(val id: Long, val path: String, val size: Long, val m: Long, val snippet: String)

    fun searchFiles(c: Context, question: String): List<FileHit> {
        val words = Regex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\-_.]*").findAll(question).map { it.value }.filter { it.length > 1 && !instruction.matches(it) }.take(8).toList()
        if (words.isEmpty()) return emptyList()
        val match = words.joinToString(" ") { if (it.contains('-') || it.contains('_') || it.contains('.')) "\"" + it.replace(Regex("[\\-_.]"), " ") + "\"" else "$it*" }
        return Db.get(c).rawQuery("SELECT f.id, f.path, f.size, f.m, snippet(files_fts, '[', ']', '…', -1, 12) FROM files_fts JOIN files f ON f.id = files_fts.docid WHERE files_fts MATCH ? LIMIT 200",
            arrayOf(match)).use { cu -> generateSequence { if (cu.moveToNext()) FileHit(cu.getLong(0), cu.getString(1) ?: "", cu.getLong(2), cu.getLong(3), cu.getString(4) ?: "") else null }.toList() }
    }

    // ------------------------------------------------------------------ 🔁 live questions to the PC (encrypted, through the relay)

    /** op: search · brief · mail · handled · fetch · sync. The PC answers within about 30–60 s. */
    fun ask(c: Context, op: String, args: JSONObject, progress: (String) -> Unit = {}): JSONObject {
        if (!hasRelay(c)) throw Exception("The relay is not set up on your PC (🔌 Test connector → GitHub relay)")
        val id = java.text.SimpleDateFormat("yyyyMMdd-HHmmss", java.util.Locale.US).format(java.util.Date()) + "-phone-" + (1000..9999).random()
        val enc = Base64.encodeToString(seal(key(c), JSONObject().put("op", op).put("args", args).toString().toByteArray()), Base64.NO_WRAP)
        val req = JSONObject().put("tool", "phone").put("enc", enc).toString()
        val (code, _) = gh(c, "requests/$id.json", "PUT", JSONObject().put("message", "phone $op").put("content", Base64.encodeToString(req.toByteArray(), Base64.NO_WRAP)))
        if (code !in 200..299) throw Exception("GitHub $code — the relay key may have expired")
        val t0 = System.currentTimeMillis()
        while (System.currentTimeMillis() - t0 < 180_000) {
            Thread.sleep(8000)
            progress("⏳ waiting for your PC… ${(System.currentTimeMillis() - t0) / 1000} s")
            val (rc, b) = gh(c, "responses/$id.md", raw = true)
            if (rc == 200) {
                val s = String(b, Charsets.UTF_8).trim()
                if (!s.startsWith("NES:")) throw Exception(s.substringAfter("\n\n").take(300))
                return JSONObject(String(gunzip(open(key(c), Base64.decode(s.substring(4), Base64.DEFAULT))), Charsets.UTF_8))
            }
        }
        throw Exception("Your PC did not answer within 3 minutes (is it on, with Nova running?)")
    }

    // ------------------------------------------------------------------ 🧠 the same brain as the PC (for every correction)

    private var brainCache: Pair<Long, JSONObject?>? = null
    private fun brain(c: Context): JSONObject? {
        val f = File(dir(c), "brain.json")
        val m = f.lastModified()
        brainCache?.let { if (it.first == m) return it.second }
        val o = try { JSONObject(f.readText()) } catch (_: Exception) { null }
        brainCache = m to o
        return o
    }

    /** Words the PC knows (your terms, project vocabulary, names from your emails, learned words) — kept exactly as written. */
    fun brainNote(c: Context, text: String = ""): String {
        val b = brain(c) ?: return ""
        val keep = LinkedHashSet<String>()
        b.optString("terms").split(',', ';', '\n').map { it.trim() }.filter { it.isNotEmpty() }.forEach { keep.add(it) }
        b.optString("myWords").split(',', ';', '\n').map { it.trim() }.filter { it.isNotEmpty() }.forEach { keep.add(it) }
        b.optJSONObject("vocab")?.keys()?.asSequence()?.take(150)?.forEach { keep.add(it) }
        if (text.isNotBlank()) {
            val low = text.lowercase()
            for (arr in listOf(b.optJSONArray("names"), b.optJSONArray("learned"))) {
                if (arr == null) continue
                for (i in 0 until arr.length()) { val w = arr.optString(i); if (w.length > 2 && low.contains(w.lowercase())) keep.add(w); if (keep.size > 260) break }
            }
        }
        if (keep.isEmpty()) return ""
        return "\nThe writer's own words (from their PC) — keep them exactly as written: " + keep.take(260).joinToString(", ") +
            ".\nNever change document numbers, revisions (Rev01, R00), approval codes (Code 1, Code 2), dates, numbers, e-mail addresses or project codes."
    }

    fun myWords(c: Context): Set<String> {
        val b = brain(c) ?: return emptySet()
        val s = HashSet<String>()
        fun add(x: String) { x.split(Regex("[\\s,;/]+")).filter { it.isNotBlank() }.forEach { s.add(it.lowercase()) } }
        add(b.optString("terms")); add(b.optString("myWords"))
        b.optJSONObject("vocab")?.keys()?.forEach { add(it) }
        for (arr in listOf(b.optJSONArray("names"), b.optJSONArray("learned"))) if (arr != null) for (i in 0 until arr.length()) add(arr.optString(i))
        return s
    }

    // ------------------------------------------------------------------ ⏰ every hour

    const val JOB_ID = 7720

    /** Never closes a screen: if Android refuses the job, "⟳ Update now" still works. */
    fun schedule(c: Context) {
        if (!paired(c)) return
        try { scheduleJob(c) } catch (e: Exception) {
            sp(c).edit().putString("last_note", "Hourly update not available on this phone (${e.message?.take(80)}) — use ⟳").apply()
        }
    }

    private fun scheduleJob(c: Context) {
        val js = c.getSystemService(Context.JOB_SCHEDULER_SERVICE) as JobScheduler
        val every = c.getSharedPreferences("nova_pc", Context.MODE_PRIVATE).getInt("every_min", 60).coerceIn(15, 720)
        js.schedule(JobInfo.Builder(JOB_ID, ComponentName(c, PcSyncJob::class.java))
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY).setPeriodic(every * 60_000L).setPersisted(false).build())
    }
}

/** ⏰ The hourly update of 🖥 My PC. */
class PcSyncJob : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        Thread { try { PcLink.sync(applicationContext) } finally { jobFinished(params, false) } }.start()
        return true
    }
    override fun onStopJob(params: JobParameters): Boolean = true
}

/** Discipline and type from a document number (SLV-ZUC-MS-SSM-00012 → MS · Mechanical, SSM · Sample) — as on the PC. */
object DocCodes {
    val disciplines = mapOf("AR" to "Architecture", "ID" to "Interior design", "LS" to "Landscape", "ST" to "Structure", "MS" to "Mechanical",
        "ME" to "MEP", "MEP" to "MEP", "EL" to "Electrical", "PL" to "Plumbing", "FF" to "Firefighting", "FS" to "Fire safety", "HV" to "HVAC",
        "CW" to "Civil works", "CV" to "Civil", "IN" to "Infrastructure", "PM" to "Project management", "QA" to "Quality", "QC" to "Quality",
        "HS" to "HSE", "FA" to "Façade", "LT" to "Lighting", "SG" to "Signage", "VT" to "Vertical transport")
    val types = mapOf("SSM" to "Sample", "MAR" to "Material approval", "MAT" to "Material", "MIR" to "Material inspection", "WIR" to "Work inspection",
        "RFI" to "RFI", "SDS" to "Shop drawing", "DRW" to "Drawing", "MSS" to "Method statement", "NCR" to "Non-conformance", "TQ" to "Technical query",
        "LTR" to "Letter", "TRN" to "Transmittal", "SI" to "Site instruction", "RPT" to "Report", "MOM" to "Minutes", "DET" to "Detail")
    private val num = Regex("\\b[A-Z][A-Z0-9]{1,5}(?:-[A-Z0-9]{1,6}){2,7}-\\d{3,6}")

    /** (number, discipline code, type code) — "" when none. */
    fun of(s: String): Triple<String, String, String> {
        val m = num.find(s.uppercase()) ?: return Triple("", "", "")
        val parts = m.value.split('-')
        val type = if (Regex("^[A-Z]{2,4}$").matches(parts[parts.size - 2])) parts[parts.size - 2] else ""
        val disc = parts.take(parts.size - 2).reversed().firstOrNull { disciplines.containsKey(it) } ?: ""
        return Triple(m.value, disc, type)
    }
    fun discLabel(c: String) = if (c.isEmpty()) "Other" else disciplines[c]?.let { "$c · $it" } ?: c
    fun typeLabel(c: String) = if (c.isEmpty()) "Other" else types[c]?.let { "$c · $it" } ?: c
}
