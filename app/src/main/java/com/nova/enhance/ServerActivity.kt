package com.nova.enhance

import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.net.VpnService
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.webkit.MimeTypeMap
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 4.1 🏢 Server explorer on the phone (read-only: open · download · zip a folder · share — never writes to the server) — works without the PC: search and browse every file name your PC indexed (server + local),
 * see where each one is (🏢 server · official / 💻 local copy), read its text preview, and open the real file live from the
 * office server through Nova's own VPN (read-only). Designed by A.HAMROUNI
 */
class ServerActivity : BaseActivity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var body: LinearLayout
    private lateinit var vpnLine: TextView
    private lateinit var vpnBtn: TextView
    private lateinit var status: TextView
    private lateinit var search: EditText
    private var folder = ""          // browsing: the folder shown (empty = drives)
    private var liveAt: String? = null   // when showing a live server folder (null = index/browse view)
    private var lastBackAt = 0L
    private var pendingPw = ""

    override fun onCreate(s: Bundle?) {
        super.onCreate(s)
        val c = this
        ServerAccess.expire(c)
        val root = Ui.col(c, 14).apply { setBackgroundColor(Ui.BG) }
        val head = Ui.row(c)
        head.addView(Ui.title(c, "🏢 Server explorer"), LinearLayout.LayoutParams(0, -2, 1f))
        head.addView(Ui.text(c, "4.1", 12f, Ui.MUTED))
        root.addView(head)
        val vcard = Ui.card(c)
        vpnLine = Ui.text(c, FortiVpn.line, 14f, Ui.TEXT, true)
        vcard.addView(vpnLine)
        vpnBtn = Ui.smallButton(c, "Connect") { toggleVpn() }
        val vr = Ui.row(c); vr.addView(vpnBtn); vr.addView(Ui.smallButton(c, "⚙ Setup") { setup() }); vr.addView(Ui.smallButton(c, "🕘 Opened") { opened() })
        vcard.addView(vr)
        root.addView(vcard, Ui.lp(top = Ui.dp(c, 8)))
        search = Ui.input(c, "🔎 Number, name or words — e.g. MAR-00181", multi = false, minLines = 1)
        search.setOnEditorActionListener { _, _, _ -> run(); true }
        search.addTextChangedListener(object : android.text.TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(e: android.text.Editable?) { main.removeCallbacksAndMessages("q"); main.postAtTime({ run() }, "q", android.os.SystemClock.uptimeMillis() + 450) }
        })
        root.addView(search, Ui.lp(top = Ui.dp(c, 10)))
        status = Ui.text(c, "", 12f, Ui.MUTED)
        root.addView(status, Ui.lp(top = Ui.dp(c, 4)))
        body = Ui.col(c)
        root.addView(ScrollView(c).apply { addView(body) }, LinearLayout.LayoutParams(-1, 0, 1f))
        root.addView(Ui.text(c, Ui.SIGNATURE, 11f, Ui.MUTED).apply { gravity = Gravity.CENTER })
        setContentView(root)
        FortiVpn.onChange = { main.post { drawVpn() } }
        drawVpn()
        run()
    }

    override fun onDestroy() { FortiVpn.onChange = null; super.onDestroy() }

    private fun drawVpn() {
        vpnLine.text = FortiVpn.line
        vpnLine.setTextColor(when (FortiVpn.state) { "up" -> Ui.GOOD; "error" -> Ui.BAD; "connecting" -> Ui.AMBER; else -> Ui.TEXT })
        vpnBtn.text = if (FortiVpn.state == "up" || FortiVpn.state == "connecting") "■ Disconnect" else "🔑 Connect"
        if (FortiVpn.state == "error" && FortiVpn.seenPin.isNotEmpty() && ServerAccess.pin(this).isEmpty()) askPin()
    }

    // ------------------------------------------------------------------ 🔑 the VPN

    private fun toggleVpn() {
        if (FortiVpn.state == "up" || FortiVpn.state == "connecting") { FortiVpn.stop(this); return }
        if (!ServerAccess.ready(this)) { setup(); return }
        val prep = VpnService.prepare(this)
        if (prep != null) { startActivityForResult(prep, 7); return }
        go()
    }
    @Deprecated("Activity result")
    override fun onActivityResult(req: Int, res: Int, data: Intent?) { super.onActivityResult(req, res, data); if (req == 7 && res == RESULT_OK) go() }
    private fun go() = FortiVpn.start(this, ServerAccess.vpnHost(this), ServerAccess.vpnUser(this), ServerAccess.secret(this, "vpn"))

    private fun askPin() {
        val p = FortiVpn.seenPin; FortiVpn.seenPin = ""
        AlertDialog.Builder(this).setTitle("🔐 The office firewall's fingerprint")
            .setMessage("Your PC has not sent its saved fingerprint yet. The firewall shows:\n\n$p\n\nOn your PC: 2.3 🌐 Office VPN shows the trusted one. Trust it only if it is the same.")
            .setPositiveButton("Trust it") { _, _ -> ServerAccess.confirmPin(this, p); go() }.setNegativeButton("Cancel", null).show()
    }

    private fun setup() {
        val c = this
        val col = Ui.col(c, 8)
        val host = ServerAccess.vpnHost(c)
        col.addView(Ui.text(c, "Firewall: ${host.ifEmpty { "— (update the phone copy from your PC)" }}\nServer: " +
            ServerAccess.drives(c).entries.joinToString(" · ") { "${it.key} → ${it.value}" }.ifEmpty { "— (your PC sends it with the next update)" } +
            "\nCertificate: " + (if (ServerAccess.pin(c).isNotEmpty()) "fingerprint from your PC ✓" else "not received yet") +
            "\nServer files: " + when (ServerAccess.serverMode(c)) { "pc" -> "always through your PC 💻"; "phonefirst" -> "📱 phone first, 💻 PC as backup"; else -> "📱 phone only — its own VPN, never the PC" } + (if (ServerAccess.smbOk(c)) " · signed in ✓" else ""), 12f, Ui.MUTED))
        fun field(hint: String, v: String, pw: Boolean): EditText {
            val e = EditText(c).apply {
                this.hint = hint; setText(v); setTextColor(Ui.TEXT); setHintTextColor(Ui.MUTED)
                if (pw) inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            }
            if (!pw) { col.addView(e); return e }
            // 👁 a password box with a show/hide eye, so a mistyped password can be checked before saving
            val row = Ui.row(c)
            row.addView(e, LinearLayout.LayoutParams(0, -2, 1f))
            row.addView(Ui.smallButton(c, "👁") {
                val hidden = e.inputType and InputType.TYPE_TEXT_VARIATION_PASSWORD != 0
                e.inputType = InputType.TYPE_CLASS_TEXT or (if (hidden) InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD else InputType.TYPE_TEXT_VARIATION_PASSWORD)
                e.setSelection(e.text.length)
            })
            col.addView(row)
            return e
        }
        fun label(s: String) = col.addView(Ui.text(c, s, 11f, Ui.MUTED).apply { setPadding(0, Ui.dp(c, 6), 0, 0) })
        label("1 · VPN username")
        val vu = field("VPN username", ServerAccess.vpnUser(c), false)
        label("2 · VPN password" + if (ServerAccess.hasSecret(c, "vpn")) "  (saved ✓ — leave empty to keep)" else "")
        val vp = field(if (ServerAccess.hasSecret(c, "vpn")) "saved ✓ — leave empty to keep" else "VPN password", "", true)
        label("3 · Server login (empty = the VPN username)")
        val su = field("e.g. ahamrouni", c.getSharedPreferences("nova_server", MODE_PRIVATE).getString("smb_user", "") ?: "", false)
        label("4 · Domain (e.g. MZA or mza.local)")
        val sd = field("Domain", ServerAccess.smbDomain(c), false)
        label("5 · Server password" + if (ServerAccess.hasSecret(c, "smb")) "  (saved ✓ — leave empty to keep)" else "  (empty = the VPN password)")
        val spw = field(if (ServerAccess.hasSecret(c, "smb")) "saved ✓ — leave empty to keep" else "Server password", "", true)
        val clear = android.widget.CheckBox(c).apply { text = "Forget the saved server password"; setTextColor(Ui.MUTED); visibility = if (ServerAccess.hasSecret(c, "smb")) android.view.View.VISIBLE else android.view.View.GONE }
        col.addView(clear)
        // Server files: how they open (📱 the phone's own VPN, never handing it to the PC, is the default)
        label("Server files open through")
        val modes = listOf("phone" to "📱 Phone only (its own VPN — never uses the PC)", "phonefirst" to "📱 Phone first, 💻 PC as backup", "pc" to "💻 Always through the PC")
        val cur = ServerAccess.serverMode(c)
        val group = android.widget.RadioGroup(c)
        modes.forEach { (id, lbl) -> group.addView(android.widget.RadioButton(c).apply { text = lbl; setTextColor(Ui.TEXT); this.id = modes.indexOfFirst { it.first == id }; isChecked = id == cur }) }
        col.addView(group)
        col.addView(Ui.text(c, "🔐 Passwords stay in this phone's secure hardware store (Android Keystore) — never synced, never backed up, never sent to the PC or the AI.", 11f, Ui.MUTED))
        fun save() {
            ServerAccess.saveLogin(c, vu.text.toString().trim(), su.text.toString().trim(), sd.text.toString().trim())
            if (vp.text.isNotEmpty()) ServerAccess.setSecret(c, "vpn", vp.text.toString())
            if (clear.isChecked) ServerAccess.clearSecret(c, "smb")
            if (spw.text.isNotEmpty()) ServerAccess.setSecret(c, "smb", spw.text.toString())
            modes.getOrNull(group.checkedRadioButtonId)?.let { ServerAccess.setServerMode(c, it.first) }
            ServerAccess.setSmbOk(c, false)   // a changed login is tested again
        }
        AlertDialog.Builder(c).setTitle("⚙ Server access").setView(ScrollView(c).apply { addView(col) })
            .setPositiveButton("Save") { _, _ -> save(); Ui.toast(c, "✓ Saved in the phone's secure store") }
            .setNeutralButton("🔐 Test login") { _, _ -> save(); testLogin() }
            .setNegativeButton("Cancel", null).show()
    }

    /** At most 3 tries, after a warning (each failure counts towards the office account lock). */
    private fun testLogin() {
        if (!FortiVpn.up) { Ui.toast(this, "Connect the office VPN first (🔑 Connect), then 🔐 Test login"); return }
        AlertDialog.Builder(this).setTitle("🔐 Test the phone's server login")
            .setMessage("Nova tries up to 3 login forms, once each, and stops at the first that works. Each failed try counts towards your office account lock — if your account was refused several times today, wait 30 min first.\n\nIf the server refuses all, nothing is lost: server files come through your PC.")
            .setPositiveButton("Test") { _, _ ->
                Thread {
                    val r = runCatching { ServerAccess.testLogin(applicationContext) { s -> main.post { status.text = s } } }
                    main.post { status.text = r.fold({ it }, { "⚠ " + it.message }) }
                }.start()
            }.setNegativeButton("Cancel", null).show()
    }

    // ------------------------------------------------------------------ 🔎 search and browse (from the index your PC sent)

    private fun run() {
        val q = search.text.toString().trim()
        val c = applicationContext
        Thread {
            val db = PcLink.Db.get(c)
            val t0 = System.currentTimeMillis()
            val rows = mutableListOf<Triple<String, Long, Long>>()
            val words = q.split(Regex("\\s+")).filter { it.length >= 2 }
            if (words.isNotEmpty()) {
                val where = words.joinToString(" AND ") { "p LIKE ?" }
                db.rawQuery("SELECT p, b, m FROM names WHERE $where ORDER BY m DESC LIMIT 300", words.map { "%$it%" }.toTypedArray()).use { cu ->
                    while (cu.moveToNext()) rows.add(Triple(cu.getString(0), cu.getLong(1), cu.getLong(2)))
                }
            }
            val total = PcLink.count(c, "names")
            main.post { if (q.isEmpty()) browse(folder) else results(q, rows, total, System.currentTimeMillis() - t0) }
        }.start()
    }

    private fun results(q: String, rows: List<Triple<String, Long, Long>>, total: Long, ms: Long) {
        liveAt = null
        body.removeAllViews()
        status.text = "%,d file(s) · from the index (%,d names, works offline) · server first · tap = download + open · hold = more".format(rows.size, total) + if (ms > 0) " · ${ms} ms" else ""
        val server = rows.filter { ServerAccess.isServer(this, it.first) }.groupBy { it.first.substringAfterLast('\\').lowercase() }.mapValues { e -> e.value.maxOf { it.third } }
        val sorted = rows.sortedWith(compareBy({ if (ServerAccess.isServer(this, it.first)) 0 else 1 }, { -it.third }))
        SubmittalLine(q, rows)?.let { body.addView(Ui.text(this, it, 13f, Ui.AMBER), Ui.lp(top = Ui.dp(this, 6))) }
        for ((p, b, m) in sorted) {
            val net = ServerAccess.isServer(this, p)
            var badge = if (net) "🏢 Server · official" else "💻 Local copy"
            if (!net) server[p.substringAfterLast('\\').lowercase()]?.let { sm -> badge += if (m < sm - 120_000) " · ⚠ older than the server" else " · same as the server ✓" }
            fileRow(p, b, m, badge, net)
        }
        if (rows.isEmpty()) body.addView(Ui.text(this, if (total == 0L) "No file names on the phone yet — on your PC: 6.1 📱 Phone link → ⟳ Update the phone now (the names come with the next update)." else "No file name has “$q”.", 13f, Ui.MUTED))
    }

    private fun SubmittalLine(q: String, rows: List<Triple<String, Long, Long>>): String? {
        val no = Regex("[A-Z]{2,6}-[A-Z]{2,6}-[A-Z]{2,4}-[A-Z]{2,4}-\\d{3,6}|(MAR|MIR|WIR|SSM|SDS|RFI|PQS|SHD)-?\\d{3,6}", RegexOption.IGNORE_CASE).find(q)?.value ?: return null
        val mine = rows.filter { it.first.contains(no, true) }
        if (mine.isEmpty()) return null
        val resp = mine.filter { it.first.contains("\\Response\\", true) || it.first.contains("\\Reply\\", true) }
        val got = mine - resp.toSet()
        val d = SimpleDateFormat("dd MMM", Locale.UK)
        val code = resp.firstNotNullOfOrNull { Regex("Code\\s*[-_ ]?\\s*(\\d)", RegexOption.IGNORE_CASE).find(it.first.substringAfterLast('\\'))?.groupValues?.get(1) }
        return "📋 ${no.uppercase()}: 📥 received ${got.minOfOrNull { it.third }?.let { d.format(Date(it)) } ?: "—"} (${got.size} file(s))" +
            (if (resp.isNotEmpty()) " · 📤 response ${d.format(Date(resp.maxOf { it.third }))}" + (code?.let { " · Code $it" } ?: "") else " · 📤 no response yet")
    }

    private fun fileRow(p: String, b: Long, m: Long, badge: String, net: Boolean) {
        val r = Ui.card(this)
        r.addView(Ui.text(this, (if (p.endsWith(".msg", true)) "✉ " else "📄 ") + p.substringAfterLast('\\'), 14f))
        r.addView(Ui.text(this, "$badge · ${if (m > 0) SimpleDateFormat("dd MMM yyyy", Locale.UK).format(Date(m)) else ""} · %.1f MB".format(b / 1e6), 11f, if (badge.contains("⚠")) Ui.AMBER else if (net) 0xFF9FD0FF.toInt() else Ui.MUTED))
        r.addView(Ui.text(this, p.substringBeforeLast('\\'), 10.5f, Ui.MUTED))
        r.setOnClickListener { get(p, b, m, net) { f, u -> openDefault(f, u) } }
        r.setOnLongClickListener { menu(p, b, m, net); true }
        body.addView(r, Ui.lp(top = Ui.dp(this, 6)))
    }

    /** Browse: drives, then folders, from the names in the index (offline); “⟳ Live” asks the server what is there now. */
    private fun browse(f: String) {
        folder = f; liveAt = null
        body.removeAllViews()
        val c = this
        if (f.isEmpty()) {
            status.text = "Your project folders and the whole server — tap a server drive to browse it live"
            val tops = mutableMapOf<String, Int>()
            PcLink.Db.get(c).rawQuery("SELECT substr(p, 1, 3) FROM names", null).use { cu -> while (cu.moveToNext()) { val k = cu.getString(0); tops[k] = (tops[k] ?: 0) + 1 } }
            // 🏢 every server drive your PC mapped (S:, O:, N:) is always shown, even if it was never indexed — you can navigate it live
            for ((drive, unc) in ServerAccess.drives(c)) {
                val k = "$drive\\"
                val n = tops[k] ?: 0
                body.addView(Ui.card(c).apply {
                    addView(Ui.text(c, "🏢  $drive  ·  " + (if (n > 0) "%,d indexed · browse live".format(n) else "browse live (not indexed)"), 15f, Ui.TEXT, true))
                    addView(Ui.text(c, "Server · official (MZA DC) · $unc", 11f, Ui.MUTED))
                    setOnClickListener { if (n > 0) browse(k) else live(k) }   // not indexed → go straight to a live look
                }, Ui.lp(top = Ui.dp(c, 6)))
                tops.remove(k)
            }
            // 💻 local copies on your PC (and any other indexed roots)
            for ((k, n) in tops.entries.sortedBy { if (ServerAccess.isServer(c, it.key)) 0 else 1 }) {
                if (ServerAccess.isServer(c, k)) continue
                body.addView(Ui.card(c).apply {
                    addView(Ui.text(c, "💻  $k  ·  %,d files".format(n), 15f, Ui.TEXT, true))
                    addView(Ui.text(c, "Local copy (on your PC)", 11f, Ui.MUTED))
                    setOnClickListener { browse(k) }
                }, Ui.lp(top = Ui.dp(c, 6)))
            }
            return
        }
        status.text = f
        val up = f.trimEnd('\\').substringBeforeLast('\\', "")
        body.addView(Ui.smallButton(c, "⬆ Up") { browse(if (f.length <= 3) "" else if (up.length <= 2) up + "\\" else up) })
        if (ServerAccess.isServer(c, f)) {
            body.addView(Ui.smallButton(c, "⟳ Live from the server") { live(f) })
            body.addView(folderActions(f))
        }
        Thread {
            val pre = f.trimEnd('\\') + "\\"
            val dirs = sortedMapOf<String, Int>(String.CASE_INSENSITIVE_ORDER); val files = mutableListOf<Triple<String, Long, Long>>()
            PcLink.Db.get(c).rawQuery("SELECT p, b, m FROM names WHERE p LIKE ? LIMIT 200000", arrayOf("$pre%")).use { cu ->
                while (cu.moveToNext()) {
                    val rest = cu.getString(0).substring(pre.length)
                    val i = rest.indexOf('\\')
                    if (i > 0) { val d = rest.substring(0, i); dirs[d] = (dirs[d] ?: 0) + 1 } else files.add(Triple(cu.getString(0), cu.getLong(1), cu.getLong(2)))
                }
            }
            main.post {
                if (folder != f) return@post
                for ((d, n) in dirs) body.addView(Ui.text(c, "📁  $d  · $n", 14f).apply { setPadding(0, Ui.dp(c, 8), 0, Ui.dp(c, 8)); setOnClickListener { browse(pre + d) } })
                val net = ServerAccess.isServer(c, f)
                for ((p, b, m) in files.sortedByDescending { it.third }) fileRow(p, b, m, if (net) "🏢 Server · official" else "💻 Local copy", net)
            }
        }.start()
    }

    private fun live(f: String) {
        liveAt = f
        val pc = ServerAccess.viaPc(this)
        status.text = if (pc) "⏳ Asking your PC what is in this folder now…" else "⏳ Asking the server…"
        Thread {
            val r = try { if (pc) ServerAccess.viaPcList(applicationContext, f) else ServerAccess.list(applicationContext, f) }
                catch (e: Exception) {
                    if (!pc && ServerAccess.isLoginError(e)) { ServerAccess.setSmbOk(applicationContext, false); main.post { live(f) }; return@Thread }
                    main.post { status.text = ServerAccess.friendly(e) }; return@Thread
                }
            main.post {
                body.removeAllViews(); status.text = "⟳ Live ${if (pc) "via your PC" else "from the server"} · ${r.size} item(s) · $f"
                val up = f.trimEnd('\\').substringBeforeLast('\\', "")
                body.addView(Ui.smallButton(this, "⬆ Up") { if (f.length <= 3 || up.length < 3) browse("") else live(if (up.length <= 2) "$up\\" else up) })
                body.addView(folderActions(f))
                for ((n, dir) in r) body.addView(Ui.text(this, (if (dir) "📁  " else "📄  ") + n, 14f).apply {
                    setPadding(0, Ui.dp(this@ServerActivity, 8), 0, Ui.dp(this@ServerActivity, 8))
                    val full = f.trimEnd('\\') + "\\" + n
                    setOnClickListener { if (dir) live(full) else get(full, 0, 0, true) { fl, u -> openDefault(fl, u) } }
                    if (!dir) setOnLongClickListener { menu(full, 0, 0, true); true }
                })
            }
        }.start()
    }

    // ------------------------------------------------------------------ 📄 one file: preview, open, keep

    /**
     * 📥 Tap a file: it is downloaded to Download › Nova Enhance › Server and opened with the phone's default app.
     * Already downloaded and unchanged on the server → opened at once. Server files: by the phone itself (VPN + its own login),
     * else through your PC; local copies: from Google Drive, else through your PC. Read-only — nothing is written to the server.
     */
    private fun get(p: String, b: Long, m: Long, net: Boolean, then: (File?, Uri?) -> Unit) {
        val c = applicationContext
        ServerAccess.downloaded(c, p, m, b)?.let { status.text = "✓ Already downloaded · opening"; then(null, it); return }
        if (ServerAccess.isPrivate(p)) { status.text = "🔒 private — not opened"; return }
        status.text = "⏳ Getting ${p.substringAfterLast('\\')}…"
        val mode = ServerAccess.serverMode(c)
        Thread {
            val r = runCatching {
                val f: File = when {
                    // 💻 local copies (on your PC) are never on the server: Google Drive first, then the PC
                    !net && GDrive.canOpen(c, p) -> GDrive.fetchWork(c, p) { s -> main.post { status.text = s } }
                    !net -> pcFetch(p)
                    // 🏢 server files
                    mode == "pc" -> ServerAccess.viaPcFetch(c, p, false) { s -> main.post { status.text = s } }
                    ServerAccess.canDirect(c) -> try {
                            ServerAccess.download(c, p) { s -> main.post { status.text = s } }   // 📱 the phone opens it itself
                        } catch (e: Exception) {
                            if (!ServerAccess.isLoginError(e)) throw e
                            ServerAccess.setSmbOk(c, false)
                            if (mode == "phonefirst") { main.post { status.text = "🔐 The server refused the phone's login — asking your PC instead…" }; ServerAccess.viaPcFetch(c, p, false) { s -> main.post { status.text = s } } }
                            else throw Exception("🔐 The server refused the phone's login — open ⚙ Setup → check Box 5 and tap 🔐 Test login (or switch Server files to “Phone first, PC backup”)")
                        }
                    // 📱 phone-only, VPN not up yet
                    mode == "phone" -> throw Exception("Connect the office VPN first (🔑 Connect) to open server files — you're in 📱 Phone-only mode")
                    else -> ServerAccess.viaPcFetch(c, p, false) { s -> main.post { status.text = s } }
                }
                val (where, u) = Downloads.save(c, f, Downloads.serverSub(p))
                ServerAccess.rememberDownload(c, p, m, b, u)
                ServerAccess.log(c, p, "downloaded + opened")
                Triple(f, u, where)
            }
            main.post { r.onSuccess { (f, u, where) -> status.text = "✓ Saved · $where"; then(f, u) }.onFailure { status.text = ServerAccess.friendly(it) } }
        }.start()
    }

    /** A local copy (on your PC) that is not in Google Drive: your PC sends it. */
    private fun pcFetch(p: String): File {
        val o = PcLink.ask(applicationContext, "fetch", org.json.JSONObject().put("path", p)) { s -> main.post { status.text = s } }
        val f = File(ServerAccess.cacheDir(this), o.getString("name").replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        f.writeBytes(PcLink.gunzip(android.util.Base64.decode(o.getString("data"), android.util.Base64.DEFAULT)))
        return f
    }

    /** Hold a file: share · folder · details. */
    private fun menu(p: String, b: Long, m: Long, net: Boolean) {
        val items = arrayOf("📥 Download + open", "📤 Share", "📂 Show its folder", "ℹ Details (text preview)")
        AlertDialog.Builder(this).setTitle(p.substringAfterLast('\\')).setItems(items) { _, i ->
            when (i) {
                0 -> get(p, b, m, net) { f, u -> openDefault(f, u) }
                1 -> confirmShare(p.substringAfterLast('\\')) { get(p, b, m, net) { f, u -> ServerAccess.log(applicationContext, p, "shared"); share(f, u) } }
                2 -> { search.setText(""); browse(p.substringBeforeLast('\\')) }
                3 -> details(p, b, m, net)
            }
        }.show()
    }

    private fun details(p: String, b: Long, m: Long, net: Boolean) {
        val c = this
        val col = Ui.col(c, 8)
        col.addView(Ui.text(c, p.substringAfterLast('\\'), 15f, Ui.TEXT, true))
        col.addView(Ui.text(c, (if (net) "🏢 Server · official · read-only" else "💻 Local copy — on your PC") + (if (m > 0) " · " + SimpleDateFormat("dd MMM yyyy HH:mm", Locale.UK).format(Date(m)) else "") + (if (b > 0) " · %.1f MB".format(b / 1e6) else ""), 12f, Ui.MUTED))
        col.addView(Ui.text(c, p.substringBeforeLast('\\'), 11f, Ui.MUTED))
        val prev = Ui.text(c, "", 12f, Ui.TEXT)
        col.addView(prev)
        Thread {
            val t = PcLink.Db.get(applicationContext).rawQuery("SELECT t FROM files WHERE path=? LIMIT 1", arrayOf(p)).use { if (it.moveToFirst()) it.getString(0) else "" }
            main.post { prev.text = if (t.isNullOrBlank()) "(no text preview — Nova has not read inside this file yet)" else "Preview (from the index): " + t.take(1500) }
        }.start()
        AlertDialog.Builder(c).setView(ScrollView(c).apply { addView(col) }).setPositiveButton("📥 Download + open") { _, _ -> get(p, b, m, net) { f, u -> openDefault(f, u) } }.setNegativeButton("Close", null).show()
    }

    /** Company documents leave Nova when shared: asked every time. */
    private fun confirmShare(what: String, go: () -> Unit) {
        AlertDialog.Builder(this).setTitle("📤 Share “$what”?")
            .setMessage("This leaves Nova and goes to the app you choose (WhatsApp, email, Teams…). The server is not changed.")
            .setPositiveButton("Share") { _, _ -> go() }.setNegativeButton("Cancel", null).show()
    }

    private fun uriOf(f: File) = Uri.parse("content://${PcFiles.AUTH}/" + Uri.encode(f.relativeTo(PcLink.dir(this)).path, "/"))

    private fun mime(name: String) = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"

    private fun share(f: File?, u: Uri?) {
        val uri = u ?: f?.let { uriOf(it) } ?: return
        val name = f?.name ?: uri.lastPathSegment ?: "file"
        val send = Intent(Intent.ACTION_SEND).setType(mime(f?.name ?: (contentResolver.getType(uri) ?: ""))).putExtra(Intent.EXTRA_STREAM, uri).putExtra(Intent.EXTRA_SUBJECT, name).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        if (f == null) contentResolver.getType(uri)?.let { send.type = it }
        try { startActivity(Intent.createChooser(send, "Share “$name”")) } catch (_: Exception) { Ui.toast(this, "No app on this phone can share this file") }
    }

    /** Opens with the phone's default app for this kind of file (Android asks once if none is set yet). */
    private fun openDefault(f: File?, u: Uri?) {
        val uri = u ?: f?.let { uriOf(it) } ?: return
        val type = if (u != null) contentResolver.getType(u) ?: mime(f?.name ?: "") else mime(f!!.name)
        val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        try { startActivity(view) } catch (_: Exception) { Ui.toast(this, "No app on this phone opens .${(f?.extension ?: "")} files — it is saved in Download › Nova Enhance › Server") }
    }

    // ------------------------------------------------------------------ 🗜 a server folder: download or share as one zip

    private var zipStop = false

    private fun folderActions(f: String): LinearLayout {
        val r = Ui.row(this)
        r.addView(Ui.smallButton(this, "🗜 Download as zip") { zip(f, false) })
        r.addView(Ui.smallButton(this, "📤 Share as zip") { confirmShare(f.trimEnd('\\').substringAfterLast('\\') + ".zip") { zip(f, true) } })
        return r
    }

    private fun zip(f: String, shareIt: Boolean) {
        val c = applicationContext
        val pc = ServerAccess.viaPc(c)
        zipStop = false
        status.text = if (pc) "⏳ Your PC counts the folder…" else "⏳ Counting the folder on the server…"
        Thread {
            try {
                val m = if (pc) ServerAccess.viaPcInfo(c, f) else ServerAccess.measure(c, f) { zipStop }
                main.post {
                    val go = {
                        val d = AlertDialog.Builder(this).setTitle("🗜 Zipping").setMessage("${m.files} file(s) · %.1f MB — ${if (pc) "zipped by your PC, sent through Google Drive" else "read from the server, packed on the phone"}.".format(m.bytes / 1e6))
                            .setNegativeButton("Hide") { _, _ -> zipStop = !pc }.setCancelable(false).show()
                        Thread {
                            try {
                                val z = if (pc) ServerAccess.viaPcFetch(c, f, true) { s -> main.post { status.text = s; d.setMessage(s) } }
                                        else ServerAccess.zipFolder(c, f, { s -> main.post { status.text = s; d.setMessage(s) } }) { zipStop }
                                val (where, u) = Downloads.save(c, z, Downloads.serverSub(f.trimEnd('\\') + "\\x"))
                                main.post {
                                    d.dismiss()
                                    if (shareIt) { ServerAccess.log(c, f, "shared as zip"); share(z, u) }
                                    else { ServerAccess.log(c, f, "downloaded as zip"); status.text = "✓ Saved · $where" }
                                }
                            } catch (e: Exception) { main.post { d.dismiss(); status.text = if (zipStop) "■ Zip cancelled" else ServerAccess.friendly(e) } }
                        }.start()
                    }
                    when {
                        m.files == 0 -> status.text = "This folder has no files to zip"
                        pc && m.bytes > 150_000_000L -> status.text = "⚠ %.0f MB — too big to send in one zip; open a sub-folder and zip that".format(m.bytes / 1e6)
                        m.bytes > 500_000_000L -> AlertDialog.Builder(this).setTitle("🗜 A big folder").setMessage("${m.files} file(s) · %.0f MB. It takes a while over the VPN and needs that space on the phone. Go on?".format(m.bytes / 1e6))
                            .setPositiveButton("Zip it") { _, _ -> go() }.setNegativeButton("Cancel") { _, _ -> status.text = "" }.show()
                        else -> go()
                    }
                }
            } catch (e: Exception) { main.post { status.text = ServerAccess.friendly(e) } }
        }.start()
    }

    /** Back stays inside the explorer: go up one folder; only a second Back at the very top leaves. */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        val la = liveAt
        when {
            la != null -> {   // in a live server view → up one live level, or back to the drive list at the root
                val up = la.trimEnd('\\').substringBeforeLast('\\', "")
                if (la.length <= 3 || up.length < 3) { browse("") } else live(if (up.length <= 2) up + "\\" else up)
            }
            folder.isNotEmpty() -> {   // browsing the index → up one folder
                val up = folder.trimEnd('\\').substringBeforeLast('\\', "")
                browse(if (folder.length <= 3) "" else if (up.length <= 2) up + "\\" else up)
            }
            search.text.isNotEmpty() -> { search.setText(""); browse("") }   // a search is on → clear it first
            else -> {   // at the very top: two taps to leave
                if (System.currentTimeMillis() - lastBackAt < 2000) { super.onBackPressed(); return }
                lastBackAt = System.currentTimeMillis()
                Ui.toast(this, "Press back again to leave the server explorer")
            }
        }
    }

    private fun opened() {
        val l = ServerAccess.opens(this)
        val fmt = SimpleDateFormat("dd MMM HH:mm", Locale.UK)
        AlertDialog.Builder(this).setTitle("🕘 From the server (opened · downloaded · zipped · shared)").setMessage(if (l.isEmpty()) "Nothing yet." else l.take(60).joinToString("\n") { "${fmt.format(Date(it.second))} · ${it.third} · ${it.first.trimEnd('\\').substringAfterLast('\\')}" })
            .setPositiveButton("OK", null).show()
    }
}
