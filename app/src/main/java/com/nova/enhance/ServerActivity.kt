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
            "\nCertificate: " + (if (ServerAccess.pin(c).isNotEmpty()) "fingerprint from your PC ✓" else "not received yet"), 12f, Ui.MUTED))
        fun field(hint: String, v: String, pw: Boolean) = EditText(c).apply {
            this.hint = hint; setText(v); setTextColor(Ui.TEXT); setHintTextColor(Ui.MUTED)
            if (pw) inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        val vu = field("VPN username", ServerAccess.vpnUser(c), false)
        val vp = field("VPN password" + if (ServerAccess.secret(c, "vpn").isNotEmpty()) " (saved — leave empty to keep)" else "", "", true)
        val su = field("Server login (if different)", ServerAccess.smbUser(c), false)
        val sd = field("Domain (if any, e.g. MZA)", ServerAccess.smbDomain(c), false)
        val sp = field("Server password (empty = same as VPN)", "", true)
        listOf(vu, vp, su, sd, sp).forEach { col.addView(it) }
        col.addView(Ui.text(c, "🔐 Passwords stay in this phone's secure hardware store (Android Keystore) — never synced, never backed up, never sent to the PC or the AI.", 11f, Ui.MUTED))
        AlertDialog.Builder(c).setTitle("⚙ Server access").setView(ScrollView(c).apply { addView(col) })
            .setPositiveButton("Save") { _, _ ->
                ServerAccess.saveLogin(c, vu.text.toString().trim(), su.text.toString().trim(), sd.text.toString().trim())
                if (vp.text.isNotEmpty()) ServerAccess.setSecret(c, "vpn", vp.text.toString())
                if (sp.text.isNotEmpty()) ServerAccess.setSecret(c, "smb", sp.text.toString())
                Ui.toast(c, "✓ Saved in the phone's secure store")
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
        body.removeAllViews()
        status.text = "%,d file(s) · from the index (%,d names, works offline) · server first".format(rows.size, total) + if (ms > 0) " · ${ms} ms" else ""
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
        r.setOnClickListener { detail(p, b, m, net) }
        body.addView(r, Ui.lp(top = Ui.dp(this, 6)))
    }

    /** Browse: drives, then folders, from the names in the index (offline); “⟳ Live” asks the server what is there now. */
    private fun browse(f: String) {
        folder = f
        body.removeAllViews()
        val c = this
        if (f.isEmpty()) {
            status.text = "Browse your project folders (from the index) — or type to search"
            val tops = mutableMapOf<String, Int>()
            PcLink.Db.get(c).rawQuery("SELECT substr(p, 1, 3) FROM names", null).use { cu -> while (cu.moveToNext()) { val k = cu.getString(0); tops[k] = (tops[k] ?: 0) + 1 } }
            for ((k, n) in tops.entries.sortedBy { if (ServerAccess.isServer(c, it.key)) 0 else 1 }) {
                val net = ServerAccess.isServer(c, k)
                body.addView(Ui.card(c).apply {
                    addView(Ui.text(c, "${if (net) "🏢" else "💻"}  $k  ·  %,d files".format(n), 15f, Ui.TEXT, true))
                    addView(Ui.text(c, if (net) "Server · official (MZA DC)" + (ServerAccess.drives(c)[k.take(2)]?.let { " · $it" } ?: "") else "Local copy (on your PC)", 11f, Ui.MUTED))
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
        if (!FortiVpn.up) { Ui.toast(this, "Connect the office VPN first (🔑 Connect)"); return }
        status.text = "⏳ Asking the server…"
        Thread {
            val r = try { ServerAccess.list(applicationContext, f) } catch (e: Exception) { main.post { status.text = "⚠ ${e.message}" }; return@Thread }
            main.post {
                body.removeAllViews(); status.text = "⟳ Live from the server · ${r.size} item(s) · $f"
                body.addView(Ui.smallButton(this, "⬅ Back to the index view") { browse(f) })
                body.addView(folderActions(f))
                for ((n, dir) in r) body.addView(Ui.text(this, (if (dir) "📁  " else "📄  ") + n, 14f).apply {
                    setPadding(0, Ui.dp(this@ServerActivity, 8), 0, Ui.dp(this@ServerActivity, 8))
                    setOnClickListener { if (dir) live(f.trimEnd('\\') + "\\" + n) else detail(f.trimEnd('\\') + "\\" + n, 0, 0, true) }
                })
            }
        }.start()
    }

    // ------------------------------------------------------------------ 📄 one file: preview, open, keep

    private fun detail(p: String, b: Long, m: Long, net: Boolean) {
        val c = this
        val col = Ui.col(c, 8)
        col.addView(Ui.text(c, p.substringAfterLast('\\'), 15f, Ui.TEXT, true))
        col.addView(Ui.text(c, (if (net) "🏢 Server · official · read-only" else "💻 Local copy — on your PC") + (if (m > 0) " · " + SimpleDateFormat("dd MMM yyyy HH:mm", Locale.UK).format(Date(m)) else "") + (if (b > 0) " · %.1f MB".format(b / 1e6) else ""), 12f, Ui.MUTED))
        val prev = Ui.text(c, "", 12f, Ui.TEXT)
        col.addView(prev)
        Thread {
            val t = PcLink.Db.get(applicationContext).rawQuery("SELECT t FROM files WHERE path=? LIMIT 1", arrayOf(p)).use { if (it.moveToFirst()) it.getString(0) else "" }
            main.post { prev.text = if (t.isNullOrBlank()) "(no text preview — Nova has not read inside this file yet)" else "Preview (from the index): " + t.take(1500) }
        }.start()
        val cached = File(ServerAccess.cacheDir(c), p.substringAfterLast('\\').replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        var dlg: AlertDialog? = null
        val acts = android.widget.GridLayout(c).apply { columnCount = 2 }
        fun act(label: String, job: () -> Unit) = acts.addView(Ui.smallButton(c, label) { dlg?.dismiss(); job() })
        if (cached.exists() || net) {
            act(if (cached.exists()) "📄 Open" else "📥 Open from the server") { withFile(p, cached) { open(it) } }
            act("⬇ Download") { withFile(p, cached) { f -> work("⬇ Saving to the phone…") { ServerAccess.toDownloads(applicationContext, f).also { ServerAccess.log(applicationContext, p, "downloaded") } } } }
            act("📤 Share") { confirmShare(p.substringAfterLast('\\')) { withFile(p, cached) { f -> ServerAccess.log(applicationContext, p, "shared"); share(f) } } }
        }
        act("📂 Folder") { search.setText(""); browse(p.substringBeforeLast('\\')) }
        if (cached.exists()) act(if (ServerAccess.isKept(c, cached)) "☆ Don't keep" else "⭐ Keep") { ServerAccess.keep(c, cached, !ServerAccess.isKept(c, cached)) }
        col.addView(acts)
        if (net) col.addView(Ui.text(c, "🔒 Read-only: Nova opens, downloads and shares — it never changes, copies to, renames or deletes anything on the server.", 11f, Ui.MUTED))
        dlg = AlertDialog.Builder(c).setView(ScrollView(c).apply { addView(col) }).setNegativeButton("Close", null).show()
    }

    /** The file on the phone: from the 7-day cache, or read from the server now (VPN needed). */
    private fun withFile(p: String, cached: File, then: (File) -> Unit) {
        if (cached.exists()) { then(cached); return }
        if (!FortiVpn.up) { Ui.toast(this, "Connect the office VPN first (🔑 Connect) — search and previews work without it"); return }
        status.text = "⏳ Reading from the server (read-only)…"
        Thread {
            try {
                val f = ServerAccess.download(applicationContext, p) { s -> main.post { status.text = s } }
                main.post { status.text = "✓ ${f.name} · kept 7 days on the phone (⭐ to keep longer)"; then(f) }
            } catch (e: Exception) { main.post { status.text = "⚠ ${e.message}" } }
        }.start()
    }

    private fun work(start: String, job: () -> String) {
        status.text = start
        Thread { val r = runCatching(job); main.post { status.text = r.fold({ "✓ Saved · $it" }, { "⚠ " + (it.message ?: "failed") }) } }.start()
    }

    /** Company documents leave Nova when shared: asked every time. */
    private fun confirmShare(what: String, go: () -> Unit) {
        AlertDialog.Builder(this).setTitle("📤 Share “$what”?")
            .setMessage("This leaves Nova and goes to the app you choose (WhatsApp, email, Teams…). The server is not changed.")
            .setPositiveButton("Share") { _, _ -> go() }.setNegativeButton("Cancel", null).show()
    }

    private fun uriOf(f: File) = Uri.parse("content://${PcFiles.AUTH}/" + Uri.encode(f.relativeTo(PcLink.dir(this)).path, "/"))

    private fun share(f: File) {
        val type = MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase()) ?: "application/octet-stream"
        val send = Intent(Intent.ACTION_SEND).setType(type).putExtra(Intent.EXTRA_STREAM, uriOf(f)).putExtra(Intent.EXTRA_SUBJECT, f.name).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try { startActivity(Intent.createChooser(send, "Share “${f.name}”")) } catch (_: Exception) { Ui.toast(this, "No app on this phone can share this file") }
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
        if (!FortiVpn.up) { Ui.toast(this, "Connect the office VPN first (🔑 Connect)"); return }
        val c = applicationContext
        zipStop = false
        status.text = "⏳ Counting the folder on the server…"
        Thread {
            try {
                val m = ServerAccess.measure(c, f) { zipStop }
                main.post {
                    val go = {
                        val d = AlertDialog.Builder(this).setTitle("🗜 Zipping on the phone").setMessage("${m.files} file(s) · %.1f MB — read from the server, packed on the phone.".format(m.bytes / 1e6))
                            .setNegativeButton("Cancel") { _, _ -> zipStop = true }.setCancelable(false).show()
                        Thread {
                            try {
                                val z = ServerAccess.zipFolder(c, f, { s -> main.post { status.text = s; d.setMessage(s) } }) { zipStop }
                                main.post {
                                    d.dismiss()
                                    if (shareIt) { ServerAccess.log(c, f, "shared as zip"); share(z) }
                                    else work("⬇ Saving the zip…") { ServerAccess.toDownloads(c, z).also { ServerAccess.log(c, f, "downloaded as zip") } }
                                }
                            } catch (e: Exception) { main.post { d.dismiss(); status.text = if (zipStop) "■ Zip cancelled" else "⚠ ${e.message}" } }
                        }.start()
                    }
                    if (m.files == 0) status.text = "This folder has no files to zip"
                    else if (m.bytes > 500_000_000L) AlertDialog.Builder(this).setTitle("🗜 A big folder").setMessage("${m.files} file(s) · %.0f MB. It takes a while over the VPN and needs that space on the phone. Go on?".format(m.bytes / 1e6))
                        .setPositiveButton("Zip it") { _, _ -> go() }.setNegativeButton("Cancel") { _, _ -> status.text = "" }.show()
                    else go()
                }
            } catch (e: Exception) { main.post { status.text = "⚠ ${e.message}" } }
        }.start()
    }

    private fun open(f: File) {
        val uri = uriOf(f)
        val type = MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase()) ?: "application/octet-stream"
        val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try { startActivity(Intent.createChooser(view, "Open “${f.name}” with")) } catch (_: Exception) { Ui.toast(this, "No app on this phone opens .${f.extension} files") }
    }

    private fun opened() {
        val l = ServerAccess.opens(this)
        val fmt = SimpleDateFormat("dd MMM HH:mm", Locale.UK)
        AlertDialog.Builder(this).setTitle("🕘 From the server (opened · downloaded · zipped · shared)").setMessage(if (l.isEmpty()) "Nothing yet." else l.take(60).joinToString("\n") { "${fmt.format(Date(it.second))} · ${it.third} · ${it.first.trimEnd('\\').substringAfterLast('\\')}" })
            .setPositiveButton("OK", null).show()
    }
}
