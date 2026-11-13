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
        head.addView(Ui.smallButton(c, "⌂") { show("home") }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32), end = Ui.dp(c, 8)))
        head.addView(Ui.smallButton(c, "⟳") { syncNow() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32)))
        root.addView(head)
        status = Ui.text(c, "", 12.5f, Ui.MUTED)
        root.addView(status)
        val sv = ScrollView(c).apply { isFillViewport = true }
        body = Ui.col(c)
        sv.addView(body)
        root.addView(sv, Ui.lp(h = 0, weight = 1f))
        root.setBackgroundColor(Ui.BG)
        setContentView(root)
        handle(intent)
        if (PcLink.paired(c)) PcLink.schedule(c)   // the hourly update (again after a restart of the phone)
        show(if (PcLink.paired(c)) "home" else "pair")
    }

    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); handle(intent); show(if (PcLink.paired(this)) "home" else "pair") }

    /** The pairing QR (novaenhance://pair?d=…) opened from the camera. */
    private fun handle(i: Intent?) {
        val d = i?.data ?: return
        if (d.scheme != "novaenhance") return
        val r = runCatching { PcLink.pair(this, d.toString()) }
        Ui.toast(this, r.getOrElse { it.message ?: "Failed" })
        if (r.isSuccess) syncNow()
    }

    override fun onBackPressed() { if (page != "home" && page != "pair") show(if (page == "mail" && hits.isNotEmpty()) "search" else "home") else super.onBackPressed() }

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
        }.start()
    }

    // ------------------------------------------------------------------ pages

    private fun show(p: String) {
        page = p
        body.removeAllViews()
        headerStatus()
        when (p) {
            "pair" -> pairPage()
            "home" -> homePage()
            "search" -> searchPage()
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
        body.addView(Ui.text(c, "1. On the PC: Nova → Settings → 📱 Phone link → 🔳 Show the pairing QR code.\n" +
            "2. Scan it with your phone's camera and choose “Open with Nova Enhance”.\n" +
            "   Or: copy the code the camera shows, then press 📋 Paste below.\n\n" +
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
        val q = Ui.input(c, "🔍 Search all your emails…", false).apply {
            imeOptions = EditorInfo.IME_ACTION_SEARCH
            setOnEditorActionListener { v, _, _ -> runSearch(v.text.toString(), false); true }
        }
        sr.addView(q, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        sr.addView(Ui.button(c, "🔍", true) { runSearch(q.text.toString(), false) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        body.addView(sr)
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
                    mb(size)) { runSearch("", false) }
                card("📁 Projects", projs?.let { a -> (0 until a.length()).joinToString(" · ") { a.getJSONObject(it).optString("code") } } ?: "") { }
                card("📂 Project files", "%,d files · names and text inside".format(f)) { show("files") }
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
                r.onSuccess { (h, e) -> hits = h; explain = e + " · ${h.size} emails · %.1f s".format((System.currentTimeMillis() - t0) / 1000.0) + if (onPc) " · on your PC" else " · on this phone" }
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

    // ------------------------------------------------------------------ ✉ one email

    private fun openMail(h0: PcLink.Hit) {
        page = "mail"
        body.removeAllViews()
        val c = this
        val h = PcLink.mail(c, h0.id) ?: h0.also { it.body = it.first }
        body.addView(Ui.text(c, h.subject, 18f, Ui.TEXT, true))
        body.addView(Ui.text(c, "From ${if (h.sent) "me" else h.from} ${if (h.fromAddr.isNotBlank()) "<${h.fromAddr}>" else ""}\nTo ${h.to}" +
            (if (h.cc.isNotBlank()) "\nCc ${h.cc}" else "") + "\n${fmt.format(Date(h.date))} · 📁 ${h.project.ifBlank { "unsorted" }}", 12.5f, Ui.MUTED).apply { setTextIsSelectable(true) },
            Ui.lp(top = Ui.dp(c, 6)))
        if (h.att.isNotBlank()) body.addView(Ui.text(c, "📎 " + h.att.split(';', '|').filter { it.isNotBlank() }.joinToString("\n📎 ") { it.trim() }, 12.5f, Ui.TEXT), Ui.lp(top = Ui.dp(c, 6)))
        val (sv, row) = Ui.chipRow(c)
        fun b(s: String, primary: Boolean = false, f: () -> Unit) = row.addView(Ui.chip(c, s, primary) { f() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        b("✦ Draft reply", true) { draft(h) }
        b("✓ Handled") { pcDo("handled", JSONObject().put("i", h.id).put("on", true), "✓ Marked as handled on your PC") }
        b("🖥 Fetch from PC") { fetch(JSONObject().put("i", h.id)) }
        b("⧉ Copy") { Ui.copy(c, h.subject + "\n\n" + h.body) }
        body.addView(sv, Ui.lp(top = Ui.dp(c, 8)))
        body.addView(Ui.text(c, h.body.ifBlank { "(the text comes with the next update — or 🖥 Fetch from PC)" }, 14f, Ui.TEXT).apply { setTextIsSelectable(true) }, Ui.lp(top = Ui.dp(c, 10)))
        body.addView(Ui.signature(c))
    }

    private fun pcDo(op: String, args: JSONObject, ok: String) {
        say("⏳ Asking your PC…")
        Thread {
            val r = runCatching { PcLink.ask(applicationContext, op, args) { say(it) } }
            main.post { status.text = r.fold({ ok }, { "⚠ " + it.message }) }
        }.start()
    }

    /** An email (.msg with attachments) or a project file, through the relay — then it opens. */
    private fun fetch(args: JSONObject) {
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
                val sys = "You write a reply email for an architect, in his own voice. Reply with the email text only (no subject line)." +
                    (if (style.isNotBlank()) "\nHis writing style: $style" else "") + Profile.note(store) + PcLink.brainNote(c, h.body)
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
        body.addView(Ui.text(c, "Tap a file to get it from your PC (through the relay, up to 15 MB). ⭐ Star it on the PC to keep it on the phone.", 12f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 6)))
        body.addView(results)
    }

    private fun starredPage() {
        val c = this
        val files = File(PcLink.dir(c), "starred").listFiles()?.sortedBy { it.name } ?: emptyList()
        if (files.isEmpty()) body.addView(Ui.text(c, "Nothing starred yet — ⭐ an email or a file on the PC; it comes with the next update.", 14f, Ui.MUTED))
        for (f in files) card("⭐ " + f.name, "%.1f MB · on this phone".format(f.length() / 1e6)) { openFile(f) }
    }

    private fun openFile(f: File) {
        val uri = Uri.parse("content://${PcFiles.AUTH}/" + Uri.encode(f.relativeTo(PcLink.dir(this)).path, "/"))
        val type = MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase()) ?: "application/octet-stream"
        try { startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)) }
        catch (_: Exception) { startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(type).putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "Open with")) }
    }

    // ------------------------------------------------------------------ ⚙

    private fun settingsPage() {
        val c = this
        val sp = getSharedPreferences("nova_pc", MODE_PRIVATE)
        body.addView(Ui.section(c, "Update every"))
        val (sv, row) = Ui.chipRow(c)
        for (m in listOf(30, 60, 180)) row.addView(Ui.chip(c, if (m < 60) "$m min" else "${m / 60} h", sp.getInt("every_min", 60) == m) {
            sp.edit().putInt("every_min", m).apply(); PcLink.schedule(c); show("settings")
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        body.addView(sv)
        body.addView(Ui.text(c, "Ways: 📶 office Wi-Fi first (fast, the full copy) · 🔁 relay when away (small updates and questions to the PC).\n" +
            "☁ Google Drive: your PC writes the encrypted copy to Google Drive; reading it on the phone needs a Google sign-in set up — coming next.", 12.5f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 8)))
        body.addView(Ui.text(c, "On this phone: ${mb(PcLink.sizeOnPhone(c))} · %,d emails · %,d files".format(PcLink.count(c, "mail"), PcLink.count(c, "files")), 13f, Ui.TEXT), Ui.lp(top = Ui.dp(c, 8)))
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
