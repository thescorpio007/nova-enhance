package com.nova.enhance

import android.content.ClipboardManager
import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import android.view.View
import android.view.inputmethod.EditorInfo
import android.webkit.MimeTypeMap
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 🖥 My PC: Nova's data from your PC, on the phone — 🌅 Brief · 🔍 Search (offline) · 📁 Projects · 📂 Project files · ⭐ Starred,
 * plus 🖥 questions to the PC through your relay. Designed by A.HAMROUNI
 */
class MyPcActivity : BaseActivity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var body: LinearLayout
    private lateinit var status: TextView
    private val fmt = SimpleDateFormat("EEE dd MMM yyyy · HH:mm", Locale.UK)
    private var page = "home"
    private var lastQuery = ""
    private var project = ""
    private var hits: List<PcLink.Hit> = emptyList()
    private var explain = ""
    private var sortBy = "best"
    private var sortAsc = false
    private var filter = ""
    private var fromPc = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        val root = Ui.col(c, 16)
        val head = Ui.row(c)
        head.addView(Ui.title(c, "🖥 My PC").apply { textSize = 21f }, Ui.lp(0, weight = 1f))
        head.addView(Ui.smallButton(c, "🕘") { show("history") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32), end = Ui.dp(c, 8)))
        head.addView(Ui.smallButton(c, "⌂") { show("home") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32), end = Ui.dp(c, 8)))
        head.addView(Ui.smallButton(c, "⟳") { syncNow() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32)))
        root.addView(head)
        status = Ui.text(c, "", 12.5f, Ui.MUTED)
        root.addView(status)
        sourceBar = Ui.col(c)
        root.addView(sourceBar)
        val sv = ScrollView(c).apply { isFillViewport = true }
        body = Ui.col(c)
        sv.addView(body)
        root.addView(sv, Ui.lp(h = 0, weight = 1f))
        root.setBackgroundColor(Ui.BG)
        setContentView(root)
        handle(intent)
        if (PcLink.paired(c)) PcLink.schedule(c)   // the hourly update (again after a restart of the phone)
        show(if (PcLink.paired(c)) "home" else "pair")
        pendingFile?.let { pendingFile = null; openShared(it) }
        // 🧊 Did Nova freeze last time? Android keeps the report: saved here, under ⚙ → Last freeze report.
        Thread { Freeze.check(applicationContext)?.let { t -> main.post { status.text = "⚠ Nova froze at $t — the report is saved (⚙ → Last freeze report)" } } }.start()
    }

    /** 📷 Nova's own QR scanner. */
    private fun scan() = startActivityForResult(Intent(this, QrScanActivity::class.java), 31)

    @Deprecated("Activity result")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != 31 || resultCode != RESULT_OK) return
        val text = data?.getStringExtra(QrScanActivity.RESULT_TEXT) ?: return
        val r = runCatching { PcLink.pair(this, text) }
        Ui.toast(this, r.getOrElse { it.message ?: "Failed" })
        if (r.isSuccess) { show("home"); syncNow() }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent); handle(intent); show(if (PcLink.paired(this)) "home" else "pair")
        pendingFile?.let { pendingFile = null; openShared(it) }
    }

    /** The pairing QR (novaenhance://pair?d=…) opened from the camera. */
    private fun handle(i: Intent?) {
        // ✉ An Outlook file opened with Nova or shared to it (WhatsApp, Files, Gmail…).
        val shared: Uri? = when {
            i?.action == Intent.ACTION_SEND -> i?.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
            i?.action == Intent.ACTION_VIEW && i?.data?.scheme in listOf("content", "file") -> i?.data
            else -> null
        }
        if (shared != null) { pendingFile = shared; return }
        val d = i?.data ?: return
        if (d.scheme != "novaenhance") return
        val r = runCatching { PcLink.pair(this, d.toString()) }
        Ui.toast(this, r.getOrElse { it.message ?: "Failed" })
        if (r.isSuccess) syncNow()
    }

    override fun onBackPressed() {
        if (page == "web" && lastMail != null) { openMail(lastMail!!); return }
        if (page != "home" && page != "pair") show(if (page == "mail" && items.isNotEmpty()) "find" else if (page == "mail" && hits.isNotEmpty()) "search" else "home") else super.onBackPressed()
    }
    private var lastMail: PcLink.Hit? = null

    private lateinit var sourceBar: LinearLayout

    /** ☁ / 📶 / 🔁 Where the data comes from — at the top of every My PC page. */
    private fun drawSource() {
        val c = this
        sourceBar.removeAllViews()
        if (!PcLink.paired(c)) return
        val (scroll, row) = Ui.chipRow(c)
        row.addView(Ui.text(c, "Get my data from", 12f, Ui.MUTED), Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        val cur = GDrive.source(c)
        for ((v, l) in listOf("auto" to "Auto", "drive" to "☁ Google Drive", "wifi" to "📶 Office Wi-Fi", "relay" to "🔁 Relay"))
            row.addView(Ui.chip(c, l, cur == v) {
                GDrive.setSource(c, v); drawSource()
                if (v == "drive" && !GDrive.signedIn(c)) googleSignIn() else syncNow()
            })
        sourceBar.addView(scroll, Ui.lp(top = Ui.dp(c, 6)))
        if ((cur == "auto" || cur == "drive") && !GDrive.signedIn(c))
            sourceBar.addView(
                if (GDrive.configured(c)) Ui.button(c, "☁ Sign in with Google — then the PC can be off") { googleSignIn() }
                else Ui.text(c, "☁ Google Drive: this copy of Nova has no Google sign-in built in yet.", 12f, Ui.MUTED),
                Ui.lp(top = Ui.dp(c, 6)))
        // The last sign-in error stays here until the next try.
        if (!GDrive.signedIn(c) && !GDrive.pending && GDrive.lastError(c).isNotEmpty())
            sourceBar.addView(Ui.text(c, "⚠ Last try: " + GDrive.lastError(c), 12f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 4)))
    }

    private fun googleSignIn() {
        val c = this
        if (GDrive.pending) { say("⏳ Sign-in already open in the browser — finish it there"); return }
        // Android 13+: the small "signing in" notification needs the permission (the sign-in works without it too).
        if (android.os.Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            try { requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 41) } catch (_: Exception) { }
        GDrive.progressNote = "⏳ Opening Google's sign-in page…"
        GdSignInService.start(c)
        watchSignIn(0)
    }

    /** Follows the sign-in (it runs in GdSignInService, so it survives while the browser is in front). */
    private fun watchSignIn(n: Int) {
        main.postDelayed({
            if (isFinishing) return@postDelayed
            if (GDrive.pending || n < 3 && !GDrive.signedIn(this) && GDrive.lastError(this).isEmpty()) { status.text = GDrive.progressNote; watchSignIn(n + 1); return@postDelayed }
            drawSource(); if (page == "settings") show("settings")
            if (GDrive.signedIn(this)) { status.text = "✓ Google Drive connected" + GDrive.account(this).let { if (it.isNotBlank()) " ($it)" else "" } + " — updating…"; syncNow() }
            else status.text = "⚠ Google sign-in: " + GDrive.lastError(this).ifBlank { "not completed" }
        }, 1000)
    }

    private var pendingFile: Uri? = null

    /** Copies the shared file into Nova's folder and opens it. */
    private fun openShared(u: Uri) {
        try {
            var name = ""
            try { contentResolver.query(u, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) name = it.getString(0) ?: "" } } catch (_: Exception) { }
            if (name.isBlank()) name = u.lastPathSegment?.substringAfterLast('/') ?: "file"
            val bytes = contentResolver.openInputStream(u)?.use { it.readBytes() } ?: throw Exception("the file could not be read")
            if (!name.contains('.') || name.substringAfterLast('.').lowercase() !in OutlookFiles.EXT)
                name += if (bytes.size > 8 && bytes[0] == 0xD0.toByte() && bytes[1] == 0xCF.toByte()) ".msg" else if (String(bytes, 0, minOf(bytes.size, 20)).startsWith("BEGIN:VCARD")) ".vcf" else if (String(bytes, 0, minOf(bytes.size, 20)).startsWith("BEGIN:VCALENDAR")) ".ics" else ".eml"
            val out = File(File(PcLink.dir(this), "fetched").apply { mkdirs() }, name.replace(Regex("[\\\\/:*?\"<>|]"), "_"))
            out.writeBytes(bytes)
            openFile(out)
        } catch (e: Exception) { Ui.toast(this, "Could not open the file: " + e.message) }
    }

    private fun say(s: String) = main.post { status.text = s }

    private fun headerStatus() {
        val c = this
        if (!PcLink.paired(c)) { status.text = "Not paired yet"; return }
        val t = PcLink.lastSync(c)
        status.text = (if (t > 0) "✓ Updated ${SimpleDateFormat("dd MMM HH:mm", Locale.UK).format(Date(t))} via ${PcLink.lastWay(c)}" else "Not updated yet — press ⟳") +
            " · ${PcLink.pcName(c)}" + PcLink.lastNote(c).let { if (it.isNotBlank()) "\n$it" else "" }
    }

    private fun syncNow() {
        if (!PcLink.paired(this)) return
        say("⏳ Updating from your PC…")
        Thread {
            val r = PcLink.sync(applicationContext) { say(it) }
            main.post { headerStatus(); status.text = r + "\n" + status.text; if (page == "home") show("home") }
            if (PcLink.queued(applicationContext).length() > 0) { val n = PcLink.flushQueue(applicationContext); if (n > 0) say("✓ $n “handled” mark(s) sent to your PC") }
        }.start()
    }

    // ------------------------------------------------------------------ pages

    private fun show(p: String) {
        page = p
        body.removeAllViews()
        headerStatus()
        drawSource()
        when (p) {
            "pair" -> pairPage()
            "home" -> homePage()
            "search" -> searchPage()
            "find" -> findPage()
            "deep" -> deepPage()
            "log" -> logPage()
            "found" -> foundPage()
            "history" -> historyPage()
            "brief" -> briefPage()
            "files" -> filesPage()
            "starred" -> starredPage()
            "settings" -> settingsPage()
        }
        body.addView(Ui.signature(this))
    }

    private fun card(title: String, sub: String, right: String = "", click: () -> Unit) {
        val c = this
        val k = Ui.card(c).apply { setOnClickListener { click() } }
        val r = Ui.row(c)
        val t = Ui.col(c)
        t.addView(Ui.text(c, title, 16f, Ui.TEXT, true))
        if (sub.isNotBlank()) t.addView(Ui.text(c, sub, 12.5f, Ui.MUTED))
        r.addView(t, Ui.lp(0, weight = 1f))
        if (right.isNotBlank()) r.addView(Ui.text(c, right, 12f, Ui.MUTED))
        k.addView(r)
        body.addView(k, Ui.lp(top = Ui.dp(c, 8)))
    }

    private fun mb(b: Long) = if (b >= 1_000_000_000) "%.2f GB".format(b / 1e9) else "%.0f MB".format(b / 1e6)

    private fun pairPage() {
        val c = this
        body.addView(Ui.text(c, "Pair this phone with Nova on your PC — once.", 16f, Ui.TEXT, true))
        body.addView(Ui.button(c, "📷 Scan the QR code on your PC", true) { scan() }, Ui.lp(top = Ui.dp(c, 10)))
        body.addView(Ui.text(c, "1. On the PC: Nova → Settings → 📱 Phone link → 🔳 Show the pairing QR code.\n" +
            "2. Press 📷 Scan above and point the phone at the screen.\n" +
            "   (Or paste the code below.)\n\n" +
            "Everything is encrypted with the key in the QR code; 🔒 private items never leave your PC.", 14f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 8)))
        val box = Ui.input(c, "…or paste the pairing code here", true, 3)
        body.addView(box, Ui.lp(top = Ui.dp(c, 12)))
        val r = Ui.row(c)
        r.addView(Ui.button(c, "📋 Paste") {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            box.setText(cm.primaryClip?.getItemAt(0)?.coerceToText(c)?.toString() ?: "")
        }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        r.addView(Ui.button(c, "✓ Pair", true) {
            val res = runCatching { PcLink.pair(c, box.text.toString()) }
            Ui.toast(c, res.getOrElse { it.message ?: "Failed" })
            if (res.isSuccess) { show("home"); syncNow() }
        }, Ui.lp(0, weight = 1f))
        body.addView(r, Ui.lp(top = Ui.dp(c, 8)))
    }

    private fun homePage() {
        val c = this
        val sr = Ui.row(c)
        val q = Ui.input(c, "🔍 Search emails, files and drawings…", false).apply {
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> v.text.toString().let { route(it) }; true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "🔍", true) { q.text.toString().let { route(it) } }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        sr.addView(Ui.button(c, "🔎 Deep") { runDeep(q.text.toString()) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        PhoneLog.prime(c)
        body.addView(Ui.smallButton(c, "📋 " + PhoneLog.logName + if (PhoneLog.builtin) " (WIR · EI · MOM · RFI…)" else "") { runLog(logOpt ?: PhoneLog.fromWords("", project)) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 6)))
        projectChips()
        Thread {
            val n = PcLink.count(c, "mail"); val f = PcLink.count(c, "files"); val old = PcLink.oldest(c); val size = PcLink.sizeOnPhone(c)
            val brief = PcLink.json(c, "brief.json")?.let { runCatching { JSONObject(it) }.getOrNull() }
            val needs = brief?.optJSONArray("needs")
            var red = 0; var or = 0
            if (needs != null) for (i in 0 until needs.length()) when (needs.getJSONObject(i).optString("st")) { "needs" -> red++; "week" -> or++ }
            val stars = File(PcLink.dir(c), "starred").listFiles()?.size ?: 0
            val projs = PcLink.json(c, "projects.json")?.let { runCatching { JSONArray(it) }.getOrNull() }
            main.post {
                if (page != "home") return@post
                card("🌅 Brief", if (brief == null) "not received yet" else "🔴 $red overdue · 🟠 $or this week · ${needs?.length() ?: 0} waiting",
                    brief?.optString("at")?.take(16)?.replace('T', ' ') ?: "") { show("brief") }
                card("📥 Email", "%,d emails".format(n) + (if (old > 0) " · ${SimpleDateFormat("MMM yyyy", Locale.UK).format(Date(old))} → today · full text" else ""),
                    mb(size)) { runFind("", "emails") }
                card("📁 Projects", projs?.let { a -> (0 until a.length()).joinToString(" · ") { a.getJSONObject(it).optString("code") } } ?: "") { }
                card("📂 Project files", "%,d files · names and text inside".format(f)) { runFind("", "files") }
                card("⭐ Starred", "$stars file(s) on the phone · open offline") { show("starred") }
                card("🧠 Same brain as your PC", if (PcLink.brainNote(c).isBlank()) "not received yet" else "your words, project vocabulary and names are used by every correction") { }
                card("⚙ Phone link", "update every ${getSharedPreferences("nova_pc", MODE_PRIVATE).getInt("every_min", 60)} min · unpair") { show("settings") }
            }
        }.start()
    }

    private fun projectChips() {
        val c = this
        val projs = PcLink.json(c, "projects.json")?.let { runCatching { JSONArray(it) }.getOrNull() } ?: return
        val (sv, row) = Ui.chipRow(c)
        row.addView(Ui.chip(c, "All projects", project.isEmpty()) { project = ""; show(page) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        for (i in 0 until projs.length()) {
            val code = projs.getJSONObject(i).optString("code")
            row.addView(Ui.chip(c, "📁 $code", project == code) { project = if (project == code) "" else code; show(page) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        }
        body.addView(sv, Ui.lp(top = Ui.dp(c, 6)))
    }

    // ------------------------------------------------------------------ 🔍 search

    private fun runSearch(q: String, onPc: Boolean) {
        lastQuery = q; fromPc = onPc; filter = ""
        say(if (onPc) "⏳ Asking your PC (about 30–60 s)…" else "⏳ Searching on the phone…")
        val c = applicationContext
        val t0 = System.currentTimeMillis()
        Thread {
            val r = runCatching {
                if (!onPc) PcLink.search(c, q, project)
                else {
                    val o = PcLink.ask(c, "search", JSONObject().put("q", q).put("project", project)) { say(it) }
                    val a = o.optJSONArray("hits") ?: JSONArray()
                    (0 until a.length()).map { hitOf(a.getJSONObject(it)) } to ("🖥 " + o.optString("explain") + "\n" + o.optString("searched"))
                }
            }
            main.post {
                r.onSuccess { (h, e) -> hits = h
                    Thread { PhoneHistory.quick(c, if (onPc) "pc" else "mail", q, h.size, "${h.size} emails" + (if (onPc) " · asked the PC" else ""), h.take(12).map { "✉ " + it.subject }) }.start()
                    explain = e + " · ${h.size} emails · %.1f s".format((System.currentTimeMillis() - t0) / 1000.0) + if (onPc) " · on your PC" else " · on this phone" }
                    .onFailure { hits = emptyList(); explain = "⚠ " + (it.message ?: "failed") }
                show("search")
            }
        }.start()
    }

    private fun hitOf(o: JSONObject) = PcLink.Hit(o.optLong("i"), o.optLong("d"), o.optBoolean("s"), o.optString("fn"), o.optString("fa"), o.optString("to"),
        o.optString("cc"), o.optString("sub"), o.optString("an"), o.optString("p"), "", why = o.optString("why"), status = o.optString("st"), first = o.optString("first"))

    private fun dim(h: PcLink.Hit, d: String) = when (d) {
        "type" -> DocCodes.typeLabel(h.doc.third)
        "proj" -> h.project.ifBlank { "Unsorted" }
        else -> DocCodes.discLabel(h.doc.second)
    }

    private fun searchPage() {
        val c = this
        val sr = Ui.row(c)
        val q = Ui.input(c, "🔍 Search all your emails…", false).apply {
            setText(lastQuery); imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> runSearch(v.text.toString(), false); true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "🔍", true) { runSearch(q.text.toString(), false) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        projectChips()
        body.addView(Ui.text(c, explain, 12.5f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 6)))
        if (!fromPc && PcLink.hasRelay(c)) body.addView(Ui.smallButton(c, "🖥 Ask the PC instead (newest emails, AI search)") { runSearch(q.text.toString(), true) }, Ui.lp(top = Ui.dp(c, 6)))
        // Sort and quick filters, as on the PC.
        val (sv, row) = Ui.chipRow(c)
        for ((k, l) in listOf("best" to "✦ Best", "date" to "📅 Date " + (if (sortBy == "date") (if (sortAsc) "↑ oldest" else "↓ newest") else ""), "disc" to "🔧 Discipline", "type" to "📄 Type", "proj" to "📁 Project")) {
            row.addView(Ui.chip(c, l.trim(), sortBy == k) {
                if (sortBy == k && k == "date") sortAsc = !sortAsc else { sortBy = k; if (k == "date") sortAsc = false; filter = "" }
                show("search")
            }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        }
        body.addView(sv, Ui.lp(top = Ui.dp(c, 6)))
        val fdim = if (sortBy == "type" || sortBy == "proj") sortBy else "disc"
        val groups = hits.groupBy { dim(it, fdim) }.entries.sortedByDescending { it.value.size }
        if (groups.size >= 2) {
            val (fsv, frow) = Ui.chipRow(c)
            frow.addView(Ui.chip(c, "All ${hits.size}", filter.isEmpty()) { filter = ""; show("search") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            for (g in groups.take(14)) frow.addView(Ui.chip(c, "${g.key.substringBefore(" · ")} ${g.value.size}", filter == g.key) { filter = if (filter == g.key) "" else g.key; show("search") },
                Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            body.addView(fsv, Ui.lp(top = Ui.dp(c, 4)))
        }
        var v = if (filter.isEmpty()) hits else hits.filter { dim(it, fdim) == filter }
        v = when (sortBy) {
            "date", "disc", "type", "proj" -> if (sortAsc) v.sortedBy { it.date } else v.sortedByDescending { it.date }
            else -> v
        }
        var lastGroup = ""
        for (h in v.take(150)) {
            if (sortBy in listOf("disc", "type", "proj")) {
                val g = dim(h, sortBy)
                if (g != lastGroup) { body.addView(Ui.section(c, "$g (${v.count { dim(it, sortBy) == g }})")); lastGroup = g }
            }
            mailCard(h)
        }
        if (v.size > 150) body.addView(Ui.text(c, "…${v.size - 150} more — refine the search", 12.5f, Ui.MUTED))
    }

    private fun mailCard(h: PcLink.Hit) {
        val c = this
        val k = Ui.card(c).apply { setOnClickListener { openMail(h) } }
        k.addView(Ui.text(c, h.subject.ifBlank { "(no subject)" }, 14.5f, Ui.TEXT, true))
        k.addView(Ui.text(c, "📁 ${h.project.ifBlank { "—" }} · ${if (h.sent) "Me (sent)" else h.from} · ${fmt.format(Date(h.date))}" +
            (if (h.att.isNotBlank()) " · 📎 ${h.att.split(';', '|').count { it.isNotBlank() }}" else ""), 12f, Ui.MUTED))
        if (h.why.isNotBlank()) k.addView(Ui.text(c, "✦ " + h.why, 12f, Ui.GOOD))
        body.addView(k, Ui.lp(top = Ui.dp(c, 6)))
    }

    // ------------------------------------------------------------------ 🔍 one search: emails + files + drawings, understood, with filter chips

    private var plan = PhoneSearch.Plan()
    private var items: List<PhoneSearch.Item> = emptyList()
    private var fKind = ""; private var fWhen = ""; private var fSort = "best"; private var latestOnly = true
    private val fStage = HashSet<String>(); private val fDisc = HashSet<String>(); private val fDoc = HashSet<String>(); private val fExt = HashSet<String>()
    private var findNote = ""

    private fun runFind(q: String, kind: String = "") {
        lastQuery = q
        say("⏳ Understanding the question…")
        val c = applicationContext
        val t0 = System.currentTimeMillis()
        Thread {
            val r = runCatching {
                val p = PhoneSearch.plan(c, q)
                if (kind.isNotEmpty() && p.kind.isEmpty()) p.kind = kind
                say("⏳ Searching on the phone…")
                p to PhoneSearch.run(c, p, project)
            }
            main.post {
                r.onSuccess { (p, l) ->
                    plan = p; items = l
                    // The AI's ticks — you can change them.
                    fKind = p.kind; fWhen = if (p.since > 0) p.whenLabel else ""; fSort = if (p.waiting || q.isBlank()) "new" else "best"
                    fStage.clear(); fDisc.clear(); fDoc.clear(); fExt.clear()
                    if (p.stage.isNotEmpty()) fStage.add(p.stage); if (p.disc.isNotEmpty()) fDisc.add(p.disc); if (p.doc.isNotEmpty()) fDoc.add(p.doc); if (p.ext.isNotEmpty()) fExt.add(p.ext)
                    // A tick that leaves nothing is taken off again (said in the note).
                    val off = ArrayList<String>()
                    if (shown().isEmpty() && fExt.isNotEmpty()) { off.add(fExt.first()); fExt.clear() }
                    if (shown().isEmpty() && fStage.isNotEmpty()) { off.add(fStage.first()); fStage.clear() }
                    if (shown().isEmpty() && fDoc.isNotEmpty()) { off.add(fDoc.first()); fDoc.clear() }
                    if (shown().isEmpty() && fDisc.isNotEmpty()) { off.add(fDisc.first()); fDisc.clear() }
                    if (shown().isEmpty() && fWhen.isNotEmpty()) { off.add(fWhen); fWhen = "" }
                    if (shown().isEmpty() && fKind.isNotEmpty()) { off.add(fKind); fKind = "" }
                    val top = l.take(12).map { (if (it.isMail) "✉ " else "📂 ") + it.name.ifBlank { it.mail?.subject ?: "" } }
                    Thread { PhoneHistory.quick(c, "find", q, l.size, "${l.size} found", top) }.start()
                    findNote = "%.1f s · on this phone".format((System.currentTimeMillis() - t0) / 1000.0) + (if (off.isNotEmpty()) " · nothing with ${off.joinToString(", ")} — shown without" else "")
                }.onFailure { items = emptyList(); plan = PhoneSearch.Plan(); findNote = "⚠ " + (it.message ?: "failed") }
                show("find")
            }
        }.start()
    }

    private fun sinceOf(label: String): Long {
        val cal = java.util.Calendar.getInstance()
        cal.set(java.util.Calendar.HOUR_OF_DAY, 0); cal.set(java.util.Calendar.MINUTE, 0); cal.set(java.util.Calendar.SECOND, 0); cal.set(java.util.Calendar.MILLISECOND, 0)
        return when (label) {
            "today" -> cal.timeInMillis
            "this week" -> System.currentTimeMillis() - 7L * 86400000
            "this month" -> { cal.set(java.util.Calendar.DAY_OF_MONTH, 1); cal.timeInMillis }
            "since last month" -> System.currentTimeMillis() - 62L * 86400000
            "this year" -> { cal.set(java.util.Calendar.DAY_OF_YEAR, 1); cal.timeInMillis }
            else -> 0L
        }
    }

    /** The results after the chips; [skip] leaves one row out (for that row's own counts). */
    private fun shown(skip: String = ""): List<PhoneSearch.Item> {
        val since = sinceOf(fWhen)
        return items.filter { x ->
            (skip == "kind" || fKind.isEmpty() || x.kind == fKind) &&
            (skip == "stage" || fStage.isEmpty() || x.stage in fStage) &&
            (skip == "disc" || fDisc.isEmpty() || x.disc in fDisc) &&
            (skip == "doc" || fDoc.isEmpty() || x.doc in fDoc) &&
            (skip == "ext" || fExt.isEmpty() || x.ext in fExt) &&
            (skip == "when" || since == 0L || x.date >= since) &&
            (project.isEmpty() || x.isMail && (x.project == project || x.project.isEmpty()) || !x.isMail) &&
            (!latestOnly || x.isMail || x.latest)
        }
    }

    private fun findPage() {
        val c = this
        val construction = Profile.isConstruction(Store(c))
        val sr = Ui.row(c)
        val q = Ui.input(c, "🔍 Search emails, files and drawings…", false).apply {
            setText(lastQuery); imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> runFind(v.text.toString()); true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "🔍", true) { runFind(q.text.toString()) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        body.addView(Ui.text(c, plan.understood(construction), 12.5f, if (plan.ai) Ui.ACCENT else Ui.MUTED), Ui.lp(top = Ui.dp(c, 6)))
        if (findNote.isNotBlank()) body.addView(Ui.text(c, findNote, 11.5f, Ui.MUTED))
        projectChips()

        fun row(label: String, build: (LinearLayout) -> Int) {
            val (sv, r) = Ui.chipRow(c)
            if (build(r) == 0) return
            body.addView(Ui.text(c, label, 10.5f, Ui.MUTED).apply { letterSpacing = 0.07f }, Ui.lp(top = Ui.dp(c, 8)))
            body.addView(sv, Ui.lp(top = Ui.dp(c, 3)))
        }
        fun chip(r: LinearLayout, text: String, on: Boolean, tap: () -> Unit) =
            r.addView(Ui.chip(c, text, on) { tap(); show("find") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        /** A row of values that exist in the results (ticked ones always stay), with their counts. */
        fun facet(r: LinearLayout, key: String, sel: HashSet<String>, order: Collection<String>, of: (PhoneSearch.Item) -> String): Int {
            val counts = shown(key).groupingBy(of).eachCount()
            var n = 0
            for (v in order) {
                val k = counts[v] ?: 0
                if (k == 0 && v !in sel) continue
                chip(r, "$v $k", v in sel) { if (!sel.add(v)) sel.remove(v) }; n++
            }
            return n
        }
        row("KIND") { r ->
            val k = shown("kind")
            chip(r, "All ${k.size}", fKind.isEmpty()) { fKind = "" }
            for ((id, label) in listOf("drawings" to "📐 Drawings", "files" to "📂 Files", "emails" to "✉ Emails"))
                chip(r, "$label ${k.count { it.kind == id }}", fKind == id) { fKind = if (fKind == id) "" else id }
            4
        }
        if (construction) {
            row("STAGE · DISCIPLINE") { r -> facet(r, "stage", fStage, PhoneSearch.STAGE.keys) { it.stage } + facet(r, "disc", fDisc, PhoneSearch.DISC.keys) { it.disc } }
            row("DOCUMENT") { r -> facet(r, "doc", fDoc, PhoneSearch.DOC.keys) { it.doc } }
        }
        row("FILE TYPE · WHEN") { r ->
            val n = facet(r, "ext", fExt, PhoneSearch.EXT.keys) { it.ext }
            val w = shown("when")
            var m = 0
            for (label in listOf("this week", "this month", "this year")) {
                val k = w.count { it.date >= sinceOf(label) }
                if (k == 0 && fWhen != label) continue
                chip(r, label.replaceFirstChar { it.uppercase() } + " $k", fWhen == label) { fWhen = if (fWhen == label) "" else label }; m++
            }
            n + m
        }
        // Sort · latest revision only
        val (ssv, srow) = Ui.chipRow(c)
        srow.addView(Ui.text(c, "Sort", 12f, Ui.MUTED), Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        for ((k, l) in listOf("best" to "✦ Best", "new" to "Newest", "doc" to "Doc no."))
            chip(srow, l, fSort == k) { fSort = k }
        if (items.any { !it.isMail }) chip(srow, (if (latestOnly) "✓ " else "") + "Latest revision only", latestOnly) { latestOnly = !latestOnly }
        body.addView(ssv, Ui.lp(top = Ui.dp(c, 8)))

        val v = shown()
        fun sorted(l: List<PhoneSearch.Item>) = when (fSort) {
            "new" -> l.sortedByDescending { it.date }
            "doc" -> l.sortedWith(compareBy<PhoneSearch.Item> { it.docNo.ifEmpty { "~" + it.name.lowercase() } }.thenByDescending { it.date })
            else -> l.sortedWith(compareByDescending<PhoneSearch.Item> { it.score }.thenByDescending { it.date })
        }
        if (v.isEmpty()) { body.addView(Ui.text(c, "Nothing found — take a filter off, or use fewer words.", 14f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 14))); return }
        val groups = listOf("drawings" to "📐 Drawings", "files" to "📂 Files", "emails" to "✉ Emails")
        for ((id, label) in groups) {
            val g = sorted(v.filter { it.kind == id })
            if (g.isEmpty()) continue
            body.addView(Ui.section(c, "$label · ${g.size}"))
            val max = if (fKind == id) 120 else 4
            for (x in g.take(max)) if (x.isMail) findMail(x) else findFile(x)
            if (g.size > max) body.addView(Ui.text(c, if (fKind == id) "…${g.size - max} more — add a filter or a word" else "▾ show all ${g.size}", 13f, Ui.ACCENT).apply {
                setPadding(Ui.dp(c, 4), Ui.dp(c, 6), 0, Ui.dp(c, 2))
                if (fKind != id) setOnClickListener { fKind = id; show("find") }
            })
        }
        if (PcLink.hasRelay(c) && lastQuery.isNotBlank()) body.addView(Ui.smallButton(c, "🖥 Ask the PC instead (newest emails)") { runSearch(lastQuery, true) }, Ui.lp(top = Ui.dp(c, 10)))
    }

    // ------------------------------------------------------------------ 🔎 deep search (round after round: phone + Google Drive)

    // ------------------------------------------------------------------ 🧭 what you mean · 📋 the log on the phone

    private var logOpt: PhoneLog.Options? = null
    private var logRes: PhoneLog.Result? = null
    private var logBusy = false
    private var logShow = ""
    private var logSaid = ""

    /** 🧭 A log, a follow-up on the log, the unsorted emails, a deep question — or the quick search. */
    private fun route(q: String) {
        val t = q.trim(); if (t.isEmpty()) return
        val last = logOpt
        PhoneLog.prime(applicationContext)
        val f = if (last != null && page == "log") PhoneLog.follow(t, last, (PhoneLog.projectCodes() + (logRes?.items?.map { it.project } ?: emptyList())).filter { it.isNotBlank() }.distinct()) else null
        when {
            f != null -> { logSaid = "🧭 " + PhoneLog.describe(f); runLog(f) }
            PhoneLog.wanted(t) -> { val o = PhoneLog.fromWords(t, project); logSaid = "🧭 " + PhoneLog.describe(o); runLog(o) }
            Regex("\\b(unsorted|not sorted|no project|without project|unassigned)\\b", RegexOption.IGNORE_CASE).containsMatchIn(t) -> runUnsorted(t)
            DeepPhone.wanted(t) -> runDeep(t)
            else -> runFind(t)
        }
    }

    private fun runUnsorted(q: String) {
        lastQuery = q; filter = ""; fromPc = false
        say("⏳ Emails not sorted into a project…")
        val c = applicationContext
        work({
            val out = ArrayList<PcLink.Hit>()
            PcLink.Db.get(c).rawQuery("SELECT id, d, s, fn, fa, too, cc, sub, an, p, f, substr(b,1,600) FROM mail WHERE p IS NULL OR p = '' ORDER BY d DESC LIMIT 500", null).use { cu ->
                while (cu.moveToNext()) out.add(PcLink.Hit(cu.getLong(0), cu.getLong(1), cu.getInt(2) != 0, cu.getString(3) ?: "", cu.getString(4) ?: "", cu.getString(5) ?: "", cu.getString(6) ?: "",
                    cu.getString(7) ?: "", cu.getString(8) ?: "", cu.getString(9) ?: "", cu.getString(10) ?: "", first = cu.getString(11) ?: ""))
            }
            out
        }) { h -> hits = h; explain = "🧭 ❔ Emails not sorted into a project · ${h.size}" + (if (h.size >= 500) " (the newest 500)" else ""); show("search") }
    }

    private fun runLog(o: PhoneLog.Options) {
        logOpt = o; logBusy = true; logShow = ""
        show("log")
        val c = applicationContext
        Thread {
            val r = runCatching { PhoneLog.run(c, o) }
            main.post {
                logBusy = false
                r.onSuccess { logRes = it; Thread { PhoneHistory.quick(c, "find", "📋 " + PhoneLog.describe(o), it.items.size, "${it.items.size} item(s) · Submittal log", it.items.take(12).map { i -> "${i.no} ${i.rev} · ${i.status}" }) }.start() }
                    .onFailure { status.text = "⚠ " + (it.message ?: "the log failed") }
                if (page == "log") show("log")
            }
        }.start()
    }

    private fun logPage() {
        val c = this
        val sr = Ui.row(c)
        val q = Ui.input(c, "🧭 Ask: “only the overdue ones” · “now tower 2” · “EI this month” · “minutes of meetings”", false).apply {
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> route(v.text.toString()); true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "Ask", true) { route(q.text.toString()) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        val o = logOpt ?: PhoneLog.Options().also { logOpt = it }
        if (logSaid.isNotEmpty()) body.addView(Ui.text(c, logSaid, 12f, Ui.ACCENT), Ui.lp(top = Ui.dp(c, 4)))
        // Types · period · status · towers
        val (tv, tr) = Ui.chipRow(c)
        tr.addView(Ui.chip(c, "All", o.types.isEmpty()) { o.types.clear(); runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        for (t in PhoneLog.enabled(c)) tr.addView(Ui.chip(c, t, t in o.types) { if (t in o.types) o.types.remove(t) else o.types.add(t); runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(tv, Ui.lp(top = Ui.dp(c, 8)))
        val (pv, pr) = Ui.chipRow(c)
        val now = System.currentTimeMillis()
        for ((l, days) in listOf("7 days" to 7, "30 days" to 30, "90 days" to 90, "1 year" to 365)) {
            val on = Math.abs(o.from - (now - days * 86_400_000L)) < 2 * 86_400_000L
            pr.addView(Ui.chip(c, l, on) { o.from = System.currentTimeMillis() - days * 86_400_000L; o.to = System.currentTimeMillis() + 86_400_000L; runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        }
        body.addView(pv, Ui.lp(top = Ui.dp(c, 6)))
        // Status: its own row, for every search.
        val (sv, sr) = Ui.chipRow(c)
        sr.addView(Ui.chip(c, "All statuses", o.status.isEmpty()) { o.status = ""; runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        for (st in listOf("Open", "Overdue", "Closed", "Code3/4")) sr.addView(Ui.chip(c, if (st == "Code3/4") "Code 3/4" else st, o.status == st) { o.status = if (o.status == st) "" else st; runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(sv, Ui.lp(top = Ui.dp(c, 6)))
        val r = logRes
        // Projects: your list from the PC (codes and names), then any other one found in the log · ＋ projects found in your emails.
        PhoneLog.prime(c)
        val projs = (PhoneLog.projectCodes() + (r?.items?.map { it.project } ?: emptyList()) + o.projects).filter { it.isNotBlank() }.distinct()
        val (jv, jr) = Ui.chipRow(c)
        jr.addView(Ui.chip(c, "All projects", o.projects.isEmpty()) { o.projects.clear(); runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        for (p in projs) {
            val mine = PhoneLog.canon(p).isNotEmpty()
            jr.addView(Ui.chip(c, if (mine) p else "✚ $p · new", p in o.projects) { if (p in o.projects) o.projects.remove(p) else o.projects.add(p); runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        }
        jr.addView(Ui.chip(c, "＋", true) { loadFound(null) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(jv, Ui.lp(top = Ui.dp(c, 6)))
        if (r != null) {
            val towers = r.items.map { it.tower }.filter { it.isNotEmpty() && !it.startsWith("TH") }.distinct().sorted()
            if (towers.isNotEmpty() || o.tower.isNotEmpty()) {
                val (wv, wr) = Ui.chipRow(c)
                wr.addView(Ui.chip(c, "All towers", o.tower.isEmpty()) { o.tower = ""; runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
                for (t in (towers + o.tower).filter { it.isNotEmpty() }.distinct()) wr.addView(Ui.chip(c, t, o.tower == t) { o.tower = if (o.tower == t) "" else t; runLog(o) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
                body.addView(wv, Ui.lp(top = Ui.dp(c, 6)))
            }
        }
        if (logBusy) { body.addView(Ui.text(c, if (PhoneLog.builtin) "⏳ Reading every submission and its reply on this phone…" else "⏳ Reading every item of your field and its reply on this phone…", 13f, Ui.ACCENT), Ui.lp(top = Ui.dp(c, 10))); return }
        if (r == null) return
        val open = r.items.count { it.status in setOf("Open", "Reviewing", "Replied", "Draft") }
        val (kv, kr) = Ui.chipRow(c)
        for ((l, f) in listOf("${r.items.size} found" to "", "${r.items.count { it.status == "Closed" || it.status == "Received" }} closed" to "Closed", "$open open" to "Open",
                "${r.items.count { it.status == "Overdue" }} overdue" to "Overdue", "${r.items.count { it.check.isNotEmpty() }} to check" to "check"))
            kr.addView(Ui.chip(c, l, logShow == f && f.isNotEmpty()) { logShow = if (logShow == f) "" else f; show("log") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(kv, Ui.lp(top = Ui.dp(c, 8)))
        body.addView(Ui.text(c, "${r.mails} emails read on the phone · %.1f s · rules: 🧠 Nova Brain".format(r.secs), 11f, Ui.MUTED))
        if (r.gaps.isNotEmpty()) body.addView(Ui.text(c, "⚠ Missing in the register: " + r.gaps.joinToString(" · "), 12f, Ui.BAD), Ui.lp(top = Ui.dp(c, 4)))
        val br = Ui.row(c)
        br.addView(Ui.chip(c, "⬇ Excel · share", true) { work({ PhoneLog.excel(applicationContext, r) }) { f ->
            startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(mimeOf(f)).putExtra(Intent.EXTRA_STREAM, uriOf(f)).putExtra(Intent.EXTRA_SUBJECT, f.nameWithoutExtension).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "↗ Share the log"))
        } }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        br.addView(Ui.chip(c, "💾 Save", false) { work({ saveToDownloads(PhoneLog.excel(applicationContext, r)) }) { where -> status.text = "✓ Saved: $where"; Ui.toast(c, "Saved: $where") } }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(br, Ui.lp(top = Ui.dp(c, 6)))
        val list = r.items.filter { i -> when (logShow) { "" -> true; "check" -> i.check.isNotEmpty(); "Open" -> i.status in setOf("Open", "Reviewing", "Replied", "Draft"); "Closed" -> i.status == "Closed" || i.status == "Received"; else -> i.status == logShow } }
        if (list.isEmpty()) body.addView(Ui.text(c, "Nothing found for these choices.", 13f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 10)))
        for (i in list.take(400)) {
            val k = Ui.card(c)
            k.addView(Ui.text(c, "${i.no} ${i.rev}".trim() + " · " + i.type + (if (i.project.isNotEmpty()) " · " + i.project else ""), 12f, Ui.MUTED))
            k.addView(Ui.text(c, i.title.ifBlank { "(no title)" }, 14f, Ui.TEXT, true))
            val place = listOf(i.tower, i.zone, i.level, i.apt).filter { it.isNotEmpty() }.joinToString(" · ")
            val col = when (i.status) { "Closed", "Received" -> Ui.GOOD; "Overdue" -> Ui.BAD; else -> Ui.ACCENT }
            k.addView(Ui.text(c, (if (place.isNotEmpty()) "📍 $place · " else "") + i.status + (if (i.code.isNotEmpty()) " · " + i.code else ""), 12.5f, col, true))
            k.addView(Ui.text(c, i.why + (if (i.check.isNotEmpty()) "\n⚠ " + i.check else ""), 11.5f, Ui.MUTED))
            val h = i.rep ?: i.sub
            if (h != null) k.setOnClickListener { openMail(h) }
            body.addView(k, Ui.lp(top = Ui.dp(c, 6)))
        }
        if (list.size > 400) body.addView(Ui.text(c, "… ${list.size - 400} more — ⬇ Excel has them all", 12f, Ui.MUTED))
    }

    // ------------------------------------------------------------------ ＋ Projects found in your emails (asks the PC)

    private var found: JSONObject? = null
    private var foundBusy = false

    private fun loadFound(args: JSONObject?) {
        foundBusy = true
        show("found")
        val c = applicationContext
        Thread {
            val r = runCatching { PcLink.ask(c, "projfound", args ?: JSONObject().put("t", "pf.load")) { s -> main.post { status.text = s } } }
            main.post {
                foundBusy = false
                r.onSuccess { found = it }.onFailure { found = JSONObject().put("said", "⚠ " + (it.message ?: "your PC did not answer")) }
                if (page == "found") show("found")
            }
        }.start()
    }

    private fun foundPage() {
        val c = this
        body.addView(Ui.text(c, "＋ Projects found in your emails", 17f, Ui.TEXT, true))
        body.addView(Ui.text(c, "document numbers, Outlook folders and subject tags that no project of yours claims yet · ✚ add · ⇢ merge · ✗ ignore — your PC and this phone use it at once (after the next update)", 12f, Ui.MUTED))
        body.addView(Ui.smallButton(c, "← Back to the log") { show("log") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 6)))
        if (foundBusy) { body.addView(Ui.text(c, "⏳ Asking your PC…", 13f, Ui.ACCENT), Ui.lp(top = Ui.dp(c, 10))); return }
        val f = found ?: return
        f.optString("said").takeIf { it.isNotBlank() }?.let { body.addView(Ui.text(c, it, 13f, if (it.startsWith("⚠")) Ui.BAD else Ui.GOOD), Ui.lp(top = Ui.dp(c, 8))) }
        val items = f.optJSONArray("items") ?: JSONArray()
        val projs = f.optJSONArray("projects") ?: JSONArray()
        if (items.length() == 0 && f.has("items")) body.addView(Ui.text(c, "Nothing new — every project in your emails is already in your list.", 13f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 10)))
        val kinds = mapOf("prefix" to "document numbers", "folder" to "Outlook folder", "tag" to "subject tag")
        for (i in 0 until items.length()) {
            val s = items.getJSONObject(i)
            val kind = s.optString("kind"); val value = s.optString("value"); val target = s.optString("target"); val code = s.optString("code")
            val k = Ui.card(c)
            k.addView(Ui.text(c, value + (if (kind == "prefix") "-…" else "") + " · ${s.optInt("emails")} emails", 15f, Ui.TEXT, true))
            k.addView(Ui.text(c, (kinds[kind] ?: kind) + " · " + s.optString("sample"), 11.5f, Ui.MUTED))
            val (bv, br) = Ui.chipRow(c)
            br.addView(Ui.chip(c, "✚ Add as $code", target.isEmpty()) {
                val e = EditText(c).apply { setText(code) }
                val n = EditText(c).apply { hint = "Full name (optional)"; if (kind != "prefix") setText(value) }
                val box = Ui.col(c, 16).apply { addView(e); addView(n) }
                android.app.AlertDialog.Builder(c).setTitle("✚ New project").setView(box)
                    .setPositiveButton("Add") { _, _ -> loadFound(JSONObject().put("t", "pf.add").put("kind", kind).put("value", value).put("code", e.text.toString().trim()).put("name", n.text.toString().trim())) }
                    .setNegativeButton("Cancel", null).show()
            }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            if (target.isNotEmpty()) br.addView(Ui.chip(c, "⇢ $target", true) {
                loadFound(JSONObject().put("t", "pf.merge").put("kind", kind).put("value", value).put("target", target))
            }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            br.addView(Ui.chip(c, "⇢ Merge into…", false) {
                val codes = (0 until projs.length()).map { projs.getJSONObject(it) }.map { it.optString("code") to (it.optString("code") + it.optString("name").let { nm -> if (nm.isNotBlank()) " · $nm" else "" }) }
                android.app.AlertDialog.Builder(c).setTitle("⇢ $value is part of…").setItems(codes.map { it.second }.toTypedArray()) { _, w ->
                    loadFound(JSONObject().put("t", "pf.merge").put("kind", kind).put("value", value).put("target", codes[w].first))
                }.show()
            }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            br.addView(Ui.chip(c, "✗ Ignore", false) { loadFound(JSONObject().put("t", "pf.ignore").put("value", value)) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            k.addView(bv, Ui.lp(top = Ui.dp(c, 6)))
            body.addView(k, Ui.lp(top = Ui.dp(c, 8)))
        }
    }

    private var deep: DeepPhone.Result? = null
    private var deepRunning = false
    private var deepStep = ""
    private val deepStop = java.util.concurrent.atomic.AtomicBoolean(false)

    private fun runDeep(q: String, resume: String? = null) {
        if (q.isBlank() || deepRunning) { if (deepRunning) show("deep"); return }
        lastQuery = q; deep = null; deepRunning = true; deepStep = "🔎 Starting…"; deepStop.set(false)
        show("deep")
        val c = applicationContext
        Thread {
            val r = runCatching { DeepPhone.run(c, q, project, deepStop, resume) { s -> main.post { deepStep = s; if (page == "deep") show("deep") } } }
            main.post {
                deepRunning = false
                deep = r.getOrElse { DeepPhone.Result("⚠ " + (it.message ?: "Deep search failed"), emptyList(), emptyList(), false) }
                if (page == "deep") show("deep")
            }
        }.start()
    }

    private fun deepPage() {
        val c = this
        val sr = Ui.row(c)
        val q = Ui.input(c, "🔎 Ask: e.g. which waterproofing system was approved earlier?", false).apply {
            setText(lastQuery); imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> runDeep(v.text.toString()); true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(if (deepRunning) Ui.button(c, "■ Stop") { deepStop.set(true); deepStep = "■ Stopping — writing up what was found…"; show("deep") }
                   else Ui.button(c, "🔎 Deep", true) { runDeep(q.text.toString()) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        body.addView(Ui.smallButton(c, "← Quick search") { show("find") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 6)))
        if (deepRunning) { body.addView(Ui.text(c, deepStep, 13f, Ui.ACCENT), Ui.lp(top = Ui.dp(c, 12))); return }
        val r = deep ?: run {
            body.addView(Ui.text(c, "Nova splits your question into its parts (the whole system, each product, the references), then searches round after round: the emails and files on this phone" +
                (if (GDrive.usable(c)) " and inside every file of your Google Drive" else "") + " — then the document numbers it finds on the way. Works with the PC off.", 13f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 12)))
            return
        }
        // The answer: text, and its table drawn as a table.
        val box = Ui.card(c)
        val lines = r.answer.lines()
        var i = 0
        while (i < lines.size) {
            if (lines[i].trim().startsWith("|")) {
                val rows = ArrayList<List<String>>()
                while (i < lines.size && lines[i].trim().startsWith("|")) {
                    val cells = lines[i].trim().trim('|').split('|').map { it.trim() }
                    if (!cells.all { Regex("^:?-{2,}:?$").matches(it) }) rows.add(cells)
                    i++
                }
                box.addView(table(rows, r.sources), Ui.lp(top = Ui.dp(c, 6)))
            } else {
                val t = lines[i].trim().replace(Regex("\\*\\*(.+?)\\*\\*"), "$1").removePrefix("#").removePrefix("#").trim()
                if (t.isNotEmpty()) box.addView(Ui.text(c, t, 14f, if (t.startsWith("Missing", true)) Ui.BAD else Ui.TEXT, lines[i].trim().startsWith("#")), Ui.lp(top = Ui.dp(c, 4)))
                i++
            }
        }
        body.addView(box, Ui.lp(top = Ui.dp(c, 10)))
        if (r.log.isNotEmpty()) body.addView(Ui.text(c, r.log.joinToString("\n"), 11f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 6)))
        if (r.sources.isEmpty()) return
        body.addView(Ui.section(c, "Sources · ${r.sources.size}"))
        r.sources.forEachIndexed { n, s -> deepSource(n + 1, s) }
    }

    /** A markdown table as a real table (sideways scroll on a narrow phone); [n] opens that source. */
    private fun table(rows: List<List<String>>, src: List<DeepPhone.Src>): View {
        val c = this
        val sv = android.widget.HorizontalScrollView(c)
        val t = android.widget.TableLayout(c)
        rows.forEachIndexed { ri, cells ->
            val tr = android.widget.TableRow(c)
            for (cell in cells) {
                val ref = Regex("\\[(\\d+)]").find(cell)?.groupValues?.get(1)?.toIntOrNull()
                val tv = Ui.text(c, cell, 12.5f, if (ri == 0) Ui.MUTED else if (ref != null) Ui.ACCENT else Ui.TEXT, ri == 0).apply {
                    setPadding(Ui.dp(c, 6), Ui.dp(c, 5), Ui.dp(c, 10), Ui.dp(c, 5)); maxWidth = Ui.dp(c, 220)
                    if (ref != null && ri > 0 && ref in 1..src.size) setOnClickListener { openSrc(src[ref - 1]) }
                }
                tr.addView(tv)
            }
            if (ri > 0) tr.setBackgroundColor(if (ri % 2 == 0) 0x10FFFFFF else 0)
            t.addView(tr)
        }
        sv.addView(t)
        return sv
    }

    private fun deepSource(n: Int, s: DeepPhone.Src) {
        val c = this
        val k = Ui.card(c).apply { setOnClickListener { openSrc(s) } }
        k.addView(Ui.text(c, "[$n] " + (when (s.kind) { "mail" -> "✉ "; "drive" -> "☁ "; else -> "📂 " }) + s.title, 14f, Ui.TEXT, true))
        k.addView(Ui.text(c, s.sub.replace('\\', '›'), 11.5f, Ui.MUTED))
        k.addView(Ui.text(c, "matched: " + s.terms.joinToString(" · "), 11.5f, Ui.GOOD))
        body.addView(k, Ui.lp(top = Ui.dp(c, 6)))
    }

    private fun openSrc(s: DeepPhone.Src) {
        s.mail?.let { openMail(it); return }
        s.file?.let { fetch(JSONObject().put("path", it.path)); return }
        val d = s.drive ?: return
        say("📥 Downloading from Google Drive…")
        Thread {
            val r = runCatching {
                val data = GDrive.download(applicationContext, d.id) ?: throw Exception("Google Drive refused the download (a Google Docs file? open it in Drive)")
                File(File(PcLink.dir(this), "fetched").apply { mkdirs() }, d.name.replace(Regex("[\\\\/:*?\"<>|]"), "_")).apply { writeBytes(data) }
            }
            main.post {
                r.onSuccess { status.text = "✓ ${it.name} · from Google Drive"; openFile(it) }.onFailure {
                    status.text = "⚠ " + it.message
                    if (d.link.isNotBlank()) try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(d.link))) } catch (_: Exception) { }
                }
            }
        }.start()
    }

    private fun tags(x: PhoneSearch.Item): String = listOfNotNull(
        x.stage.takeIf { it.isNotEmpty() }, x.disc.takeIf { it.isNotEmpty() }, x.doc.takeIf { it.isNotEmpty() },
        ("Level " + plan.level).takeIf { x.levelHit && plan.level.isNotEmpty() },
        (if (x.latest) "latest" + (if (x.rev.isNotEmpty()) " · ${x.rev}" else "") else "rev ${x.rev}").takeIf { !x.isMail && x.rev.isNotEmpty() }).joinToString("  ·  ")

    private fun findFile(x: PhoneSearch.Item) {
        val c = this
        val f = x.file ?: return
        val k = Ui.card(c).apply { setOnClickListener { fetch(JSONObject().put("path", f.path)) } }
        k.addView(Ui.text(c, x.name, 14f, Ui.TEXT, true))
        val folders = f.path.replace('/', '\\').split('\\').dropLast(1).filter { it.isNotBlank() && !it.endsWith(":") }.takeLast(3).joinToString(" › ")
        k.addView(Ui.text(c, folders + " · " + "%.1f MB".format(f.size / 1e6) + (if (f.m > 0) " · " + SimpleDateFormat("dd MMM yyyy", Locale.UK).format(Date(f.m)) else ""), 11.5f, Ui.MUTED))
        tags(x).let { if (it.isNotBlank()) k.addView(Ui.text(c, it, 11.5f, Ui.GOOD)) }
        if (f.snippet.isNotBlank() && x.score < 6) k.addView(Ui.text(c, f.snippet, 11.5f, Ui.MUTED))
        body.addView(k, Ui.lp(top = Ui.dp(c, 6)))
    }

    private fun findMail(x: PhoneSearch.Item) {
        val c = this
        val h = x.mail ?: return
        val k = Ui.card(c).apply { setOnClickListener { openMail(h) } }
        k.addView(Ui.text(c, h.subject.ifBlank { "(no subject)" }, 14f, Ui.TEXT, true))
        k.addView(Ui.text(c, "${if (h.sent) "Me (sent)" else h.from} · ${SimpleDateFormat("EEE dd MMM yyyy", Locale.UK).format(Date(h.date))} · 📁 ${h.project.ifBlank { "—" }}" +
            (if (h.att.isNotBlank()) " · 📎 ${h.att.split(';', '|').count { it.isNotBlank() }}" else ""), 11.5f, Ui.MUTED))
        val t = tags(x)
        if (t.isNotBlank()) k.addView(Ui.text(c, t, 11.5f, Ui.GOOD))
        if (x.waitingDays >= 0) k.addView(Ui.text(c, "⏳ waiting ${x.waitingDays} day(s) for your reply", 11.5f, Ui.BAD))
        body.addView(k, Ui.lp(top = Ui.dp(c, 6)))
    }

    // ------------------------------------------------------------------ ✉ one email

    private fun openMail(h0: PcLink.Hit) {
        page = "mail"; lastMail = h0
        body.removeAllViews()
        body.addView(Ui.text(this, "⏳ Opening the email…", 13f, Ui.MUTED))
        // Read in the background: the screen (and the Nova keyboard, same app) never waits for the phone's database.
        work({ PcLink.mail(applicationContext, h0.id) ?: h0.also { it.body = it.first } }) { h -> if (page == "mail" && lastMail === h0) drawMail(h) }
    }

    private fun drawMail(h: PcLink.Hit) {
        body.removeAllViews()
        val c = this
        body.addView(Ui.text(c, h.subject.ifBlank { "(no subject)" }, 18f, Ui.TEXT, true).apply { setTextIsSelectable(true) })
        // Header: one line, tap for To / Cc.
        val short = "${if (h.sent) "Me" else h.from} · ${fmt.format(Date(h.date))} · 📁 ${h.project.ifBlank { "unsorted" }}"
        val full = "From ${if (h.sent) "me" else h.from}${if (h.fromAddr.isNotBlank()) " <${h.fromAddr}>" else ""}\nTo ${h.to}" +
            (if (h.cc.isNotBlank()) "\nCc ${h.cc}" else "") + "\n${fmt.format(Date(h.date))} · 📁 ${h.project.ifBlank { "unsorted" }}"
        val head = Ui.text(c, "$short  ▾ more", 12.5f, Ui.MUTED)
        head.setOnClickListener { head.text = if (head.text.startsWith("From ")) "$short  ▾ more" else full }
        body.addView(head, Ui.lp(top = Ui.dp(c, 6)))
        val (sv, row) = Ui.chipRow(c)
        fun b(s: String, primary: Boolean = false, f: () -> Unit) = row.addView(Ui.chip(c, s, primary) { f() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        b("📄 Formatted", true) { formatted(h) }
        b("✦ Draft reply") { draft(h) }
        b("✓ Handled") { pcDo("handled", JSONObject().put("i", h.id).put("on", true), "✓ Marked as handled on your PC") }
        b("↗ Share") { shareMenu(h) }
        body.addView(sv, Ui.lp(top = Ui.dp(c, 8)))
        attachments(h)
        // The text, readable: links as 🔗, earlier messages folded.
        val text = h.body.ifBlank { "(the text comes with the next update — or 📄 Formatted)" }
        val cut = Regex("(?m)^\\s*(-{3,}\\s*Original Message\\s*-{3,}|-{5}.*-{5}|From:\\s.+\\n\\s*(Sent|Date):\\s|On .+wrote:)", RegexOption.IGNORE_CASE).find(text)
        val top = if (cut != null && cut.range.first > 20) text.substring(0, cut.range.first) else text
        body.addView(linkified(top.trimEnd()), Ui.lp(top = Ui.dp(c, 12)))
        if (cut != null && cut.range.first > 20) {
            val older = linkified(text.substring(cut.range.first)).apply { visibility = View.GONE; setTextColor(Ui.MUTED) }
            val tg = Ui.smallButton(c, "▾ Show earlier messages") { older.visibility = if (older.visibility == View.GONE) View.VISIBLE else View.GONE }
            body.addView(tg, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 10)))
            body.addView(older, Ui.lp(top = Ui.dp(c, 8)))
        }
        body.addView(Ui.signature(c))
    }

    /** Text with long links shown as "🔗 link" (tap to open), comfortable size and spacing. */
    private fun linkified(t: String): TextView {
        val c = this
        val sb = android.text.SpannableStringBuilder()
        var last = 0
        for (m in Regex("<?(https?://[^\\s<>]+)>?").findAll(t)) {
            sb.append(t, last, m.range.first)
            val url = m.groupValues[1]
            val label = "🔗 " + (Uri.parse(url).host ?: "link")
            val st = sb.length; sb.append(label)
            sb.setSpan(object : android.text.style.ClickableSpan() {
                override fun onClick(w: View) { try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) } catch (_: Exception) { } }
            }, st, sb.length, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            last = m.range.last + 1
        }
        sb.append(t, last, t.length)
        return Ui.text(c, "", 15f, Ui.TEXT).apply {
            text = sb; setLineSpacing(0f, 1.25f); movementMethod = android.text.method.LinkMovementMethod.getInstance()
            setLinkTextColor(0xFFB9B0FF.toInt()); setTextIsSelectable(true)
        }
    }

    /** 📎 each attachment: tap = get it from the PC and open it in the right app. */
    private fun attachments(h: PcLink.Hit) {
        val c = this
        val names = h.att.split(';', '|').map { it.trim() }.filter { it.isNotEmpty() && !Regex("^image\\d+\\.(png|jpe?g|gif)$", RegexOption.IGNORE_CASE).matches(it) }
        if (names.isEmpty()) return
        val box = FlowLayout(c, Ui.dp(c, 6), Ui.dp(c, 6))
        for (n in names) box.addView(Ui.chip(c, "📎 $n", false) { getAttachment(h, n) })
        body.addView(box, Ui.lp(top = Ui.dp(c, 8)))
    }

    /** One attachment as a file on the phone: kept copy → ☁ Google Drive (last 3 months, up to 35 MB) → the PC. Blocking. */
    private fun attFile(h: PcLink.Hit, name: String): File {
        val c = applicationContext
        val local = File(File(PcLink.dir(c), "fetched").apply { mkdirs() }, name.replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        if (local.exists() && local.length() > 0) return local
        val fromDrive = if (GDrive.usable(c)) try {
            val sha = java.security.MessageDigest.getInstance("SHA-1").digest(name.lowercase().toByteArray()).joinToString("") { "%02x".format(it) }.take(16)
            GDrive.fetchSync(c, "full/a/${h.id / 1000}/${h.id}-$sha.nes")?.let { PcLink.unbox(c, it) }
        } catch (_: Exception) { null } else null
        if (fromDrive != null) local.writeBytes(fromDrive)
        else {
            val o = PcLink.ask(c, "att", JSONObject().put("i", h.id).put("name", name)) { say(it) }
            local.writeBytes(PcLink.gunzip(android.util.Base64.decode(o.getString("data"), android.util.Base64.DEFAULT)))
        }
        return local
    }

    private fun getAttachment(h: PcLink.Hit, name: String) {
        say(if (GDrive.usable(this)) "⏳ Getting “$name” from Google Drive…" else "⏳ Getting “$name” from your PC…")
        Thread {
            val r = runCatching { attFile(h, name) }
            main.post {
                r.onSuccess { status.text = "✓ ${it.name}"; openFile(it) }
                    .onFailure { status.text = "⚠ " + (if (GDrive.usable(this)) "Not in Google Drive (older than 3 months or over 35 MB) and your PC can't be reached: " else "") + it.message }
            }
        }.start()
    }

    // ------------------------------------------------------------------ ↗ Share an email: .msg (or .eml), save to the phone, attachments only, copy

    private fun attNames(h: PcLink.Hit) = h.att.split(';', '|').map { it.trim() }.filter { it.isNotEmpty() && !Regex("^image\\d+\\.(png|jpe?g|gif)$", RegexOption.IGNORE_CASE).matches(it) }

    /**
     * The email as a file: the real Outlook .msg from ☁ Google Drive (last 3 months) or from the PC when it answers;
     * otherwise an .eml built here from the formatted page and the attachments (Outlook, Gmail and Windows open it the same way). Blocking.
     */
    private fun mailFile(h: PcLink.Hit): Pair<File, String> {
        val c = applicationContext
        val dir = File(PcLink.dir(c), "fetched/m${h.id}").apply { mkdirs() }      // one folder per email: the same subject never mixes two emails
        val stem = h.subject.ifBlank { "email" }.replace(Regex("[\\\\/:*?\"<>|\\r\\n\\t]"), "_").trim().take(110)
        val msg = File(dir, "$stem.msg")
        if (msg.exists() && msg.length() > 0) return msg to "kept on the phone"
        if (GDrive.usable(c)) try {
            say("⏳ Getting the Outlook email from Google Drive…")
            GDrive.fetchSync(c, "full/e/${h.id / 1000}/${h.id}.nes")?.let { msg.writeBytes(PcLink.unbox(c, it)); return msg to "☁ from Google Drive" }
        } catch (_: Exception) { }
        try {
            say("⏳ Asking your PC for the Outlook email…")
            val o = PcLink.ask(c, "fetch", JSONObject().put("i", h.id)) { say(it) }
            msg.writeBytes(PcLink.gunzip(android.util.Base64.decode(o.getString("data"), android.util.Base64.DEFAULT)))
            return msg to "🖥 from your PC"
        } catch (_: Exception) { }
        // The PC is off and the .msg is not in Drive: an .eml with the same content.
        say("⏳ Building the email file on the phone…")
        val full = PcLink.mail(c, h.id) ?: h
        val html = try { driveMail(h.id)?.optString("h")?.takeIf { it.isNotBlank() } } catch (_: Exception) { null }
            ?: File(File(PcLink.dir(c), "formatted"), "${h.id}.html").takeIf { it.exists() }?.readText()
        val atts = ArrayList<Pair<String, ByteArray>>(); var missing = 0
        for (n in attNames(h)) try { atts.add(n to attFile(h, n).readBytes()) } catch (_: Exception) { missing++ }
        val eml = File(dir, "$stem.eml")
        eml.writeBytes(OutlookFiles.eml(if (h.fromAddr.isNotBlank()) "${h.from} <${h.fromAddr}>" else h.from, h.to, h.cc, h.subject, h.date, html, full.body.ifBlank { h.first }, atts))
        return eml to ("built on the phone as .eml (the PC is off and the .msg is not in Google Drive)" + (if (missing > 0) " · $missing attachment(s) could not be included" else ""))
    }

    private fun uriOf(f: File) = Uri.parse("content://${PcFiles.AUTH}/" + Uri.encode(f.relativeTo(PcLink.dir(this)).path, "/"))
    private fun mimeOf(f: File) = when (f.extension.lowercase()) { "msg" -> "application/vnd.ms-outlook"; "eml" -> "message/rfc822"
        else -> MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase()) ?: "application/octet-stream" }

    private fun shareMenu(h: PcLink.Hit) {
        val c = this
        val n = attNames(h).size
        val items = arrayListOf("✉  Outlook email (.msg)" + (if (n > 0) " with its $n attachment(s)" else ""), "💾  Save to phone (Download › Nova Enhance)")
        if (n > 0) items.add("📎  Attachments only ($n)")
        items.add("⧉  Copy the text")
        android.app.AlertDialog.Builder(c).setTitle("↗ Share this email").setItems(items.toTypedArray()) { _, w ->
            when (items[w].substring(0, 1)) {
                "✉" -> work({ mailFile(h) }) { (f, how) ->
                    status.text = "✓ ${f.name} · $how"
                    startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(mimeOf(f)).putExtra(Intent.EXTRA_STREAM, uriOf(f)).putExtra(Intent.EXTRA_SUBJECT, h.subject)
                        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "↗ Share “${f.name}”"))
                }
                "💾" -> work({ mailFile(h).let { (f, how) -> saveToDownloads(f) to how } }) { (where, how) -> status.text = "✓ Saved: $where · $how"; Ui.toast(c, "Saved: $where") }
                "📎" -> work({ attNames(h).mapNotNull { nm -> try { attFile(h, nm) } catch (_: Exception) { null } } }) { files ->
                    if (files.isEmpty()) { status.text = "⚠ The attachments are not in Google Drive and your PC can't be reached"; return@work }
                    status.text = "✓ ${files.size} attachment(s)"
                    val send = if (files.size == 1) Intent(Intent.ACTION_SEND).setType(mimeOf(files[0])).putExtra(Intent.EXTRA_STREAM, uriOf(files[0]))
                        else Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList(files.map { uriOf(it) }))
                    startActivity(Intent.createChooser(send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "↗ Share the attachments"))
                }
                else -> work({ h.subject + "\n\n" + (PcLink.mail(applicationContext, h.id)?.body ?: h.body) }) { Ui.copy(c, it) }
            }
        }.show()
    }

    /** Runs [job] off the screen thread, then [done] on it; a failure goes to the status line. */
    private fun <T> work(job: () -> T, done: (T) -> Unit) {
        Thread {
            val r = runCatching(job)
            main.post { r.onSuccess { try { done(it) } catch (e: Exception) { status.text = "⚠ " + e.message } }.onFailure { status.text = "⚠ " + (it.message ?: "failed") } }
        }.start()
    }

    /** Download › Nova Enhance › name (Android 10+); older phones: the app's own Download folder. Returns where it is. */
    private fun saveToDownloads(f: File): String {
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            val v = android.content.ContentValues().apply {
                put(android.provider.MediaStore.Downloads.DISPLAY_NAME, f.name); put(android.provider.MediaStore.Downloads.MIME_TYPE, mimeOf(f))
                put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS + "/Nova Enhance")
            }
            val u = contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, v) ?: throw Exception("the phone refused to save the file")
            contentResolver.openOutputStream(u)?.use { o -> f.inputStream().use { it.copyTo(o) } } ?: throw Exception("the phone refused to save the file")
            return "Download › Nova Enhance › ${f.name}"
        }
        val dir = File(getExternalFilesDir(android.os.Environment.DIRECTORY_DOWNLOADS), "Nova Enhance").apply { mkdirs() }
        f.copyTo(File(dir, f.name), true)
        return "Android › data › Nova Enhance › Download › ${f.name}"
    }

    /** 📄 The email as Outlook shows it (fonts, tables, logos, pictures), shown inside Nova. Kept on the phone after the first time. */
    private fun formatted(h: PcLink.Hit) {
        val cache = File(File(PcLink.dir(this), "formatted").apply { mkdirs() }, "${h.id}.html")
        if (cache.exists()) { work({ cache.readText() }) { showHtml(h.subject, it) }; return }
        say(if (GDrive.usable(this)) "⏳ Getting the formatted email from Google Drive…" else "⏳ Getting the formatted email from your PC…")
        Thread {
            // ☁ Google Drive first (the PC can be off), then the PC.
            val r = runCatching {
                driveMail(h.id)?.optString("h")?.takeIf { it.isNotBlank() }
                    ?: PcLink.ask(applicationContext, "html", JSONObject().put("i", h.id)) { say(it) }.optString("html")
            }
            main.post {
                r.onSuccess { html -> cache.writeText(html); status.text = "✓ Formatted"; showHtml(h.subject, html) }
                    .onFailure { status.text = "⚠ " + (if (GDrive.usable(this)) "Not in Google Drive yet (the PC copies the newest first) and your PC can't be reached: " else "") + it.message }
            }
        }.start()
    }

    /** ☁ One email of the PC's copy in Google Drive ({h: page, a: attachment names}) — null when Drive is off or it is not there yet. */
    private fun driveMail(id: Long): JSONObject? {
        val c = applicationContext
        if (!GDrive.usable(c)) return null
        return try {
            val bucket = id / 200
            val f = File(File(PcLink.dir(c), "full").apply { mkdirs() }, "$bucket.json")
            fun pick(): JSONObject? = if (!f.exists()) null else JSONObject(f.readText()).let { o ->
                val m = o.optJSONObject("m")?.optJSONObject(id.toString()) ?: return@let null
                val img = o.optJSONObject("img")
                val page = Regex("nimg:([0-9a-f]{20})").replace(m.optString("h")) { img?.optString(it.groupValues[1])?.takeIf { d -> d.isNotBlank() } ?: it.value }
                JSONObject().put("h", page).put("a", m.optJSONArray("a") ?: org.json.JSONArray())
            }
            pick() ?: run {
                // Not kept yet (or a newer email of a bundle kept earlier): get the bundle again, at most every 10 minutes.
                if (f.exists() && System.currentTimeMillis() - f.lastModified() < 600_000) return null
                val box = GDrive.fetchSync(c, "full/m/$bucket.nes") ?: return null
                f.writeBytes(PcLink.unbox(c, box))
                pick()
            }
        } catch (_: Exception) { null }
    }

    /** ✉ An Outlook file opened by Nova: the page, and its attachments to tap (an email inside an email opens the same way). */
    private fun showOutlook(m: OutlookFiles.Mail, fallbackTitle: String) {
        showHtml(m.subject.ifBlank { fallbackTitle }, OutlookFiles.page(m)) { c ->
            val list = OutlookFiles.listed(m)
            if (list.isEmpty()) return@showHtml
            val box = FlowLayout(c, Ui.dp(c, 6), Ui.dp(c, 6))
            for (a in list) box.addView(Ui.chip(c, (if (a.nested != null) "✉ " else "📎 ") + a.name, false) {
                if (a.nested != null) showOutlook(a.nested, a.name)
                else try {
                    val out = File(File(PcLink.dir(this), "fetched").apply { mkdirs() }, a.name.replace(Regex("[\\\\/:*?\"<>|]"), "_"))
                    out.writeBytes(a.data!!); openFile(out)
                } catch (e: Exception) { Ui.toast(this, "Could not open: " + e.message) }
            })
            body.addView(box, Ui.lp(top = Ui.dp(c, 8)))
        }
    }

    private fun showHtml(title: String, html: String, extra: ((Context) -> Unit)? = null) {
        page = "web"
        body.removeAllViews()
        val c = this
        body.addView(Ui.text(c, title, 16f, Ui.TEXT, true))
        extra?.invoke(c)
        val w = android.webkit.WebView(c).apply {
            settings.javaScriptEnabled = false
            settings.loadWithOverviewMode = true; settings.useWideViewPort = true
            settings.builtInZoomControls = true; settings.displayZoomControls = false
            settings.textZoom = 100
            setBackgroundColor(0xFFFFFFFF.toInt())
            webViewClient = object : android.webkit.WebViewClient() {
                override fun shouldOverrideUrlLoading(v: android.webkit.WebView, req: android.webkit.WebResourceRequest): Boolean {
                    try { startActivity(Intent(Intent.ACTION_VIEW, req.url)) } catch (_: Exception) { }
                    return true
                }
            }
        }
        val fit = html.replace(Regex("<head[^>]*>", RegexOption.IGNORE_CASE)) { it.value + "<meta name='viewport' content='width=device-width, initial-scale=1'><style>body{word-wrap:break-word}table{max-width:100%}img{max-width:100%;height:auto}</style>" }
        w.loadDataWithBaseURL(null, fit, "text/html", "utf-8", null)
        body.addView(w, Ui.lp(h = (resources.displayMetrics.heightPixels * 0.78).toInt(), top = Ui.dp(c, 8)))
    }

    private fun pcDo(op: String, args: JSONObject, ok: String) {
        say("⏳ Asking your PC…")
        Thread {
            val r = runCatching { PcLink.ask(applicationContext, op, args) { say(it) } }
            // The PC is off: a "handled" mark is kept and sent when the PC answers again.
            if (r.isFailure && op == "handled") { PcLink.queue(applicationContext, args); main.post { status.text = "✓ Marked here — your PC gets it when it is on again (${PcLink.queued(applicationContext).length()} waiting)" }; return@Thread }
            main.post { status.text = r.fold({ ok }, { "⚠ " + it.message }) }
        }.start()
    }

    /** An email (.msg with attachments) or a project file, through the relay — then it opens. */
    private fun fetch(args: JSONObject) {
        // ☁ A project file that is also in Google Drive (FreeFileSync copy): straight from Drive, the PC can be off.
        val path = args.optString("path")
        if (path.isNotEmpty() && GDrive.canOpen(this, path)) {
            say("⏳ Opening from Google Drive…")
            Thread {
                val r = runCatching { GDrive.fetchWork(applicationContext, path) { say(it) } }
                main.post {
                    r.onSuccess { status.text = "✓ ${it.name} · from Google Drive"; openFile(it) }
                        .onFailure { status.text = "⚠ Google Drive: ${it.message} — asking your PC instead…"; fetchFromPc(args) }
                }
            }.start()
            return
        }
        fetchFromPc(args)
    }

    private fun fetchFromPc(args: JSONObject) {
        say("⏳ Asking your PC for the file (30–60 s)…")
        Thread {
            val r = runCatching {
                val o = PcLink.ask(applicationContext, "fetch", args) { say(it) }
                val f = File(File(PcLink.dir(this), "fetched").apply { mkdirs() }, o.getString("name").replace(Regex("[\\\\/:*?\"<>|]"), "_"))
                f.writeBytes(PcLink.gunzip(android.util.Base64.decode(o.getString("data"), android.util.Base64.DEFAULT)))
                f
            }
            main.post { r.onSuccess { status.text = "✓ ${it.name}"; openFile(it) }.onFailure { status.text = "⚠ " + it.message } }
        }.start()
    }

    /** ✦ Draft reply on the phone, in your writing style (learned on the PC). */
    private fun draft(h: PcLink.Hit) {
        val c = this
        val store = Store(c)
        val p = Providers.all.filter { store.hasKey(it) }.let { k -> k.firstOrNull { it.id == store.target } ?: k.firstOrNull() }
            ?: run { Ui.toast(c, "Add an API key in Settings"); return }
        val style = PcLink.json(c, "brain.json")?.let { runCatching { JSONObject(it).optString("style") }.getOrNull() } ?: ""
        say("⏳ Writing the reply with ${p.label}…")
        Thread {
            val r = runCatching {
                val sys = "You write a reply email for " + Profile.who(store) + ", in their own voice. Reply with the email text only (no subject line)." +
                    (if (style.isNotBlank()) "\nTheir writing style: $style" else "") + Profile.note(store) + PcLink.brainNote(c, h.body)
                Providers.chatSmart(p, store.key(p), store.model(p), sys, listOf(Msg("user", "Email to answer:\nFrom: ${h.from}\nSubject: ${h.subject}\n\n${h.body.take(8000)}"))) { say(it) }.text
            }
            main.post {
                r.onSuccess { t ->
                    status.text = "✓ Draft ready"
                    android.app.AlertDialog.Builder(c).setTitle("✦ Draft reply").setMessage(t)
                        .setPositiveButton("⧉ Copy") { _, _ -> Ui.copy(c, t) }
                        .setNeutralButton("✉ Share") { _, _ -> startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, t).putExtra(Intent.EXTRA_SUBJECT, "RE: " + h.subject), "Send with")) }
                        .setNegativeButton("Close", null).show()
                }.onFailure { status.text = "⚠ " + it.message }
            }
        }.start()
    }

    // ------------------------------------------------------------------ 🌅 brief

    private fun briefPage() {
        val c = this
        val o = PcLink.json(c, "brief.json")?.let { runCatching { JSONObject(it) }.getOrNull() }
        if (o == null) { body.addView(Ui.text(c, "The brief comes with the next update from your PC.", 14f, Ui.MUTED)); return }
        val needs = o.optJSONArray("needs") ?: JSONArray()
        val list = (0 until needs.length()).map { hitOf(needs.getJSONObject(it)) }
        body.addView(Ui.text(c, "🌅 ${list.size} waiting · from your PC ${o.optString("at").take(16).replace('T', ' ')} · ✓ ${o.optInt("handled")} handled today", 13f, Ui.MUTED))
        if (PcLink.hasRelay(c)) body.addView(Ui.smallButton(c, "🖥 Build the brief again on the PC now") {
            say("⏳ Your PC is building the brief…")
            Thread {
                val r = runCatching { PcLink.ask(applicationContext, "brief", JSONObject()) { say(it) } }
                main.post { r.onSuccess { File(PcLink.dir(c), "brief.json").writeText(it.put("at", SimpleDateFormat("yyyy-MM-dd'T'HH:mm", Locale.US).format(Date())).toString()); show("brief") }.onFailure { status.text = "⚠ " + it.message } }
            }.start()
        }, Ui.lp(top = Ui.dp(c, 6)))
        for ((st, title) in listOf("needs" to "🔴 OVERDUE", "week" to "🟠 THIS WEEK", "fyi" to "⚪ FYI", "" to "WAITING")) {
            val g = list.filter { if (st.isEmpty()) it.status !in listOf("needs", "week", "fyi") else it.status == st }
            if (g.isEmpty()) continue
            body.addView(Ui.section(c, "$title (${g.size})"))
            g.forEach { mailCard(it) }
        }
        val follow = o.optJSONArray("follow") ?: JSONArray()
        if (follow.length() > 0) {
            body.addView(Ui.section(c, "↩ YOUR EMAILS WITH NO ANSWER (${follow.length()})"))
            for (i in 0 until follow.length()) mailCard(hitOf(follow.getJSONObject(i)))
        }
    }

    // ------------------------------------------------------------------ 📂 project files · ⭐ starred

    private fun filesPage() {
        val c = this
        val sr = Ui.row(c)
        val q = Ui.input(c, "📂 File name, drawing number or words inside…", false)
        val results = Ui.col(c)
        fun run() {
            val text = q.text.toString()
            Thread {
                val r = runCatching { PcLink.searchFiles(c, text) }
                main.post {
                    results.removeAllViews()
                    r.onSuccess { l ->
                        results.addView(Ui.text(c, "${l.size} file(s)", 12.5f, Ui.MUTED))
                        for (f in l) {
                            val k = Ui.card(c).apply { setOnClickListener { fetch(JSONObject().put("path", f.path)) } }
                            k.addView(Ui.text(c, f.path.substringAfterLast('\\').substringAfterLast('/'), 14f, Ui.TEXT, true))
                            k.addView(Ui.text(c, f.path + " · " + "%.1f MB".format(f.size / 1e6), 11.5f, Ui.MUTED))
                            if (f.snippet.isNotBlank()) k.addView(Ui.text(c, f.snippet, 12f, Ui.TEXT))
                            results.addView(k, Ui.lp(top = Ui.dp(c, 6)))
                        }
                    }.onFailure { results.addView(Ui.text(c, "⚠ " + it.message, 13f, Ui.BAD)) }
                }
            }.start()
        }
        q.imeOptions = EditorInfo.IME_ACTION_SEARCH
        q.setOnEditorActionListener { _, _, _ -> run(); true }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "🔍", true) { run() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
        body.addView(Ui.text(c, (if (GDrive.signedIn(c) && GDrive.source(c).let { it == "auto" || it == "drive" }) "Tap a file to open it from Google Drive (the PC can be off)."
            else "Tap a file to get it from your PC (office Wi-Fi, or the relay up to 15 MB).") + " ⭐ Star it on the PC to keep it on the phone.", 12f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 6)))
        body.addView(results)
    }

    private fun starredPage() {
        val c = this
        val files = File(PcLink.dir(c), "starred").listFiles()?.sortedBy { it.name } ?: emptyList()
        if (files.isEmpty()) body.addView(Ui.text(c, "Nothing starred yet — ⭐ an email or a file on the PC; it comes with the next update.", 14f, Ui.MUTED))
        for (f in files) card("⭐ " + f.name, "%.1f MB · on this phone".format(f.length() / 1e6)) { openFile(f) }
    }

    private fun openFile(f: File) {
        if (f.extension.lowercase() in listOf("html", "htm")) { work({ f.readText() }) { showHtml(f.nameWithoutExtension, it) }; return }
        // ✉ Outlook files (.msg .oft .eml .ics .vcf): Nova reads them itself — in the background (a big .msg takes seconds).
        if (f.extension.lowercase() in OutlookFiles.EXT && f.length() < 60_000_000) {
            say("⏳ Reading ${f.name}…")
            work({ try { OutlookFiles.read(f.name, f.readBytes()) } catch (e: Exception) { null } }) { m ->
                if (m != null) { status.text = "✓ ${f.name}"; showOutlook(m, f.nameWithoutExtension) }
                else { Ui.toast(this, "Nova could not read this ${f.extension} file — choose another app"); openOutside(f) }
            }
            return
        }
        openOutside(f)
    }

    private fun openOutside(f: File) {
        val uri = Uri.parse("content://${PcFiles.AUTH}/" + Uri.encode(f.relativeTo(PcLink.dir(this)).path, "/"))
        val type = MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase())
        // Only apps that really open this type; otherwise 📤 Share.
        val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, type ?: "application/octet-stream").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        if (type != null && view.resolveActivity(packageManager) != null) {
            try { startActivity(Intent.createChooser(view, "Open “${f.name}” with")); return } catch (_: Exception) { }
        }
        Ui.toast(this, "No app on this phone opens .${f.extension} files — choose where to send it")
        startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(type ?: "application/octet-stream").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "📤 Share “${f.name}”"))
    }

    // ------------------------------------------------------------------ ⚙

    // ------------------------------------------------------------------ 🕘 search history (saved by itself, 60 days)

    private var hFind = ""; private var hKind = ""

    private fun historyPage() {
        val c = this
        val q = Ui.input(c, "🔍 Find in history: subject, words, document no.…", false).apply {
            setText(hFind); imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> hFind = v.text.toString().trim(); show("history"); true }
        }
        body.addView(q)
        val box = Ui.col(c)
        body.addView(box)
        box.addView(Ui.text(c, "⏳ Reading the history…", 13f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 10)))
        work({ PhoneHistory.list(applicationContext) }) { all -> if (page == "history") drawHistory(box, all) }
    }

    private fun drawHistory(box: LinearLayout, all: List<PhoneHistory.Entry>) {
        val c = this
        box.removeAllViews()
        val kinds = listOf("" to "All", "deep" to "🔎 Deep", "find" to "🔍 Quick", "mail" to "✉ Emails", "pc" to "🖥 Asked the PC", "interrupted" to "⚠ Interrupted")
        fun match(e: PhoneHistory.Entry, k: String) = k.isEmpty() || (if (k == "interrupted") e.status == "interrupted" else e.kind == k)
        val (sv, row) = Ui.chipRow(c)
        for ((k, l) in kinds) {
            val n = all.count { match(it, k) }
            if (k.isNotEmpty() && n == 0) continue
            row.addView(Ui.chip(c, "$l $n", hKind == k) { hKind = if (hKind == k) "" else k; show("history") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        }
        box.addView(sv, Ui.lp(top = Ui.dp(c, 8)))
        val words = hFind.lowercase().split(' ').filter { it.isNotBlank() }
        val list = all.filter { e -> match(e, hKind) && words.all { w -> (e.q + " " + e.answer).lowercase().contains(w) } }
        if (list.isEmpty()) box.addView(Ui.text(c, if (all.isEmpty()) "Nothing yet — every search you make is saved here by itself, with its date, subject and answer." else "Nothing matches.", 13.5f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 14)))
        val dayF = SimpleDateFormat("EEE dd MMM", Locale.UK); val timeF = SimpleDateFormat("HH:mm", Locale.UK)
        var lastDay = ""
        for (e in list.take(300)) {
            val d = dayF.format(Date(e.ts)).let { if (it == dayF.format(Date())) "Today · $it" else if (it == dayF.format(Date(System.currentTimeMillis() - 86_400_000))) "Yesterday · $it" else it }
            if (d != lastDay) { box.addView(Ui.section(c, d)); lastDay = d }
            val k = Ui.card(c)
            val icon = when (e.kind) { "deep" -> "🔎"; "find" -> "🔍"; "pc" -> "🖥"; else -> "✉" }
            k.addView(Ui.text(c, "${timeF.format(Date(e.ts))}  $icon ${e.q}", 14f, Ui.TEXT, true))
            val st = when (e.status) { "done" -> "✓ finished"; "stopped" -> "■ stopped"; "interrupted" -> "⚠ interrupted (Nova closed) — what was found is kept"; "running" -> "⏳ running now"; else -> e.status }
            k.addView(Ui.text(c, st + " · ${e.count} found" + (if (e.note.isNotBlank()) " · ${e.note}" else ""), 11.5f, if (e.status == "interrupted") Ui.BAD else Ui.GOOD))
            val r = Ui.row(c)
            fun b(t: String, primary: Boolean, f: () -> Unit) = r.addView(Ui.chip(c, t, primary) { f() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            if (e.kind == "deep" && e.status == "interrupted") b("▶ Continue", true) { runDeep(e.q, e.id) }
            b("Open", e.status != "interrupted") { openHistory(e) }
            b("⧉", false) { Ui.copy(c, e.q + "\n\n" + e.answer) }
            b("🗑", false) { PhoneHistory.delete(c, e.id); show("history") }
            k.addView(r, Ui.lp(top = Ui.dp(c, 6)))
            box.addView(k, Ui.lp(top = Ui.dp(c, 6)))
        }
        // How long it is kept
        box.addView(Ui.section(c, "Keep history for"))
        val (sv2, row2) = Ui.chipRow(c)
        for ((dd, l) in listOf(60 to "60 days", 90 to "90 days", 180 to "180 days", 0 to "Always")) row2.addView(Ui.chip(c, l, PhoneHistory.days(c) == dd) {
            work({ PhoneHistory.setDays(applicationContext, dd) }) { show("history") }
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        box.addView(sv2)
        box.addView(Ui.text(c, "Older searches are removed by themselves · 🔒 stays on this phone", 11.5f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 4)))
        if (all.isNotEmpty()) box.addView(Ui.smallButton(c, "🗑 Clear all history…") {
            android.app.AlertDialog.Builder(c).setMessage("Delete all ${all.size} saved searches?").setPositiveButton("Delete") { _, _ -> PhoneHistory.clear(c); show("history") }
                .setNegativeButton("Cancel", null).show()
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 10)))
    }

    private fun openHistory(e: PhoneHistory.Entry) {
        when (e.kind) {
            "deep" -> { say("⏳ Opening the saved search…"); work({ PhoneHistory.result(applicationContext, e) }) { r -> lastQuery = e.q; deep = r; status.text = "🕘 Saved ${SimpleDateFormat("dd MMM HH:mm", Locale.UK).format(Date(e.ts))}"; show("deep") } }
            "find" -> { lastQuery = e.q; show("find"); runFind(e.q) }
            else -> runSearch(e.q, e.kind == "pc")
        }
    }

    private fun settingsPage() {
        val c = this
        val sp = getSharedPreferences("nova_pc", MODE_PRIVATE)
        Freeze.last(c)?.let { (t, txt) ->
            body.addView(Ui.section(c, "🧊 Last freeze report"))
            body.addView(Ui.text(c, "Nova froze on $t. Share this report so the exact cause can be fixed.", 13f, Ui.TEXT))
            val r = Ui.row(c)
            r.addView(Ui.chip(c, "↗ Share the report", true) {
                startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_SUBJECT, "Nova freeze report $t").putExtra(Intent.EXTRA_TEXT, txt), "↗ Share the report"))
            }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            r.addView(Ui.chip(c, "⧉ Copy", false) { Ui.copy(c, txt) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
            r.addView(Ui.chip(c, "Clear", false) { Freeze.clear(c); show("settings") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
            body.addView(r, Ui.lp(top = Ui.dp(c, 6)))
        }
        body.addView(Ui.section(c, "Update every"))
        val (sv, row) = Ui.chipRow(c)
        for (m in listOf(30, 60, 180)) row.addView(Ui.chip(c, if (m < 60) "$m min" else "${m / 60} h", sp.getInt("every_min", 60) == m) {
            sp.edit().putInt("every_min", m).apply(); PcLink.schedule(c); show("settings")
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(sv)
        body.addView(Ui.text(c, "Ways: ☁ Google Drive first (from anywhere, the PC can be off) · 📶 office Wi-Fi · 🔁 relay when away (small updates and questions to the PC).", 12.5f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 8)))
        // ☁ Google Drive on this phone
        body.addView(Ui.section(c, "☁ Google Drive"))
        val gdLine = when {
            GDrive.signedIn(c) -> "✓ Signed in" + GDrive.account(c).let { if (it.isNotBlank()) " as $it" else "" } + " — the copy and your project files come straight from Google Drive."
            GDrive.configured(c) -> "Not signed in yet. One Google sign-in (read-only) and the phone updates from Google Drive by itself, even when the PC is off."
            else -> "This copy of Nova has no Google sign-in built in yet — update Nova Enhance."
        }
        body.addView(Ui.text(c, gdLine, 13f, if (GDrive.signedIn(c)) Ui.TEXT else Ui.MUTED), Ui.lp(top = Ui.dp(c, 4)))
        if (GDrive.configured(c)) body.addView(Ui.button(c, if (GDrive.signedIn(c)) "Sign out of Google Drive" else "☁ Sign in with Google") {
            if (GDrive.signedIn(c)) { GDrive.signOut(c); show("settings") }
            else googleSignIn()
        }, Ui.lp(top = Ui.dp(c, 8)))
        body.addView(Ui.text(c, "On this phone: ${mb(PcLink.sizeOnPhone(c))} · %,d emails · %,d files".format(PcLink.count(c, "mail"), PcLink.count(c, "files")), 13f, Ui.TEXT), Ui.lp(top = Ui.dp(c, 8)))
        body.addView(Ui.button(c, "📷 Pair again (scan the QR code)") { scan() }, Ui.lp(top = Ui.dp(c, 14)))
        body.addView(Ui.button(c, "Unpair and delete the copy") {
            android.app.AlertDialog.Builder(c).setMessage("Delete everything from your PC on this phone and unpair?")
                .setPositiveButton("Delete") { _, _ -> PcLink.unpair(c); show("pair") }.setNegativeButton("Cancel", null).show()
        }, Ui.lp(top = Ui.dp(c, 14)))
    }
}

/** Opens files of 🖥 My PC (⭐ starred, fetched) in other apps — read-only, only inside the My PC folder. */
class PcFiles : ContentProvider() {
    companion object { const val AUTH = "com.nova.enhance.pcfiles" }
    override fun onCreate() = true
    private fun file(uri: Uri): File {
        val base = PcLink.dir(context!!).canonicalFile
        val f = File(base, uri.path?.trimStart('/') ?: "").canonicalFile
        if (!f.path.startsWith(base.path) || !f.isFile) throw java.io.FileNotFoundException()
        return f
    }
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor = ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.MODE_READ_ONLY)
    override fun getType(uri: Uri): String = MimeTypeMap.getSingleton().getMimeTypeFromExtension(uri.path?.substringAfterLast('.')?.lowercase() ?: "") ?: "application/octet-stream"
    override fun query(uri: Uri, projection: Array<out String>?, s: String?, a: Array<out String>?, o: String?): Cursor {
        val f = file(uri)
        return android.database.MatrixCursor(arrayOf(android.provider.OpenableColumns.DISPLAY_NAME, android.provider.OpenableColumns.SIZE)).apply { addRow(arrayOf<Any>(f.name, f.length())) }
    }
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, s: String?, a: Array<out String>?) = 0
    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<out String>?) = 0
}
