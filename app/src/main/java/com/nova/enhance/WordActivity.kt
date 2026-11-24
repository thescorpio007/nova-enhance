package com.nova.enhance

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.pdf.PdfDocument
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.Editable
import android.text.TextWatcher
import android.util.Base64
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONObject
import java.io.File

/**
 * 📄 Nova Word on the phone: open any Word file (.docx and .doc) from the phone, Google Drive or an attachment, read it like a page,
 * edit its paragraphs, and fix Arabic direction with ¶→ / ←¶ (this paragraph) or ⇄ (all paragraphs). Save in its own format:
 * .docx on the phone; .doc on the phone for text, and through Word on your PC when the direction changed (so Word shows it right).
 * ✦ Enhance / 🌐 Translate a paragraph with your AI. Share as .docx, .doc, PDF or text. Designed by A.HAMROUNI
 */
class WordActivity : BaseActivity() {
    companion object {
        private const val PICK = 41; private const val SAVE_AS = 42
        fun open(c: Context) = c.startActivity(Intent(c, WordActivity::class.java))
        private val PAGE = Color.WHITE; private val INK = 0xFF1E1E1E.toInt()
    }

    private lateinit var store: Store
    private lateinit var title: TextView
    private lateinit var status: TextView
    private lateinit var pageBox: LinearLayout
    private lateinit var convBtn: TextView
    private var doc: WordDoc? = null
    private var uri: Uri? = null
    private var name = ""
    private var bytes: ByteArray? = null
    private var cur: WPara? = null
    private val views = HashMap<WPara, EditText>()
    private var pendingSave: ByteArray? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c: Context = this
        store = Store(c)
        val root = Ui.col(c, 12)
        root.setBackgroundColor(Ui.BG)
        val head = Ui.row(c)
        title = Ui.text(c, "📄 Word", 17f, Ui.TEXT, true).apply { maxLines = 2 }
        head.addView(title, Ui.lp(0, weight = 1f))
        head.addView(Ui.smallButton(c, "📂 Open") { pick() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32)))
        root.addView(head)

        // ¶→ ←¶ ⇄ · ✦ 🌐 · 💾 📤
        val (sv, row) = Ui.chipRow(c)
        fun chip(t: String, desc: String = t, f: () -> Unit) = row.addView(Ui.chip(c, t, false) { f() }.apply { contentDescription = desc },
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 34)).apply { marginEnd = Ui.dp(c, 6) })
        chip("¶→", "Left to right") { setDir('L') }
        chip("←¶", "Right to left") { setDir('R') }
        chip("⇄ Fix all directions") { fixAll() }
        chip("✦ Enhance") { ai(false) }
        chip("🌐 Translate") { ai(true) }
        chip("💾 Save") { save(false) }
        chip("💾 Save as…") { save(true) }
        chip("📤 Share") { share() }
        root.addView(sv, Ui.lp(top = Ui.dp(c, 8)))
        convBtn = Ui.smallButton(c, "🔄 Convert this .doc to .docx with Word on my PC") { convert() }.apply { visibility = View.GONE }
        root.addView(convBtn, Ui.lp(top = Ui.dp(c, 6)))
        status = Ui.text(c, "", 12.5f, Ui.MUTED)
        root.addView(status, Ui.lp(top = Ui.dp(c, 6)))

        pageBox = Ui.col(c, 18).apply { setBackgroundColor(PAGE) }
        val scroll = ScrollView(c).apply { isVerticalScrollBarEnabled = true; isScrollbarFadingEnabled = false; addView(pageBox) }
        root.addView(scroll, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f).apply { topMargin = Ui.dp(c, 8) })
        root.addView(Ui.signature(c))
        setContentView(root)

        val i = intent
        val u = i?.data ?: if (i?.action == Intent.ACTION_SEND) i.getParcelableExtra(Intent.EXTRA_STREAM) else null
        if (u != null) load(u) else { say("Open a Word file (.docx or .doc) — 📂 Open, or “Open with → Nova” from WhatsApp, Gmail, Outlook or Files."); pick() }
    }

    private fun say(s: String, color: Int = Ui.MUTED) = runOnUiThread { status.text = s; status.setTextColor(color) }

    private fun pick() = try {
        startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_MIME_TYPES, arrayOf("application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/octet-stream"))
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION), PICK)
    } catch (_: Exception) { say("No file picker on this phone", Ui.BAD) }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(req: Int, res: Int, data: Intent?) {
        super.onActivityResult(req, res, data)
        if (res != Activity.RESULT_OK || data?.data == null) return
        val u = data.data!!
        if (req == PICK) {
            try { contentResolver.takePersistableUriPermission(u, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) } catch (_: Exception) { }
            load(u)
        } else if (req == SAVE_AS) {
            val b = pendingSave ?: return
            try { contentResolver.openOutputStream(u, "wt")!!.use { it.write(b) }; say("✓ Saved as ${nameOf(u)}", Ui.GOOD); uri = u; name = nameOf(u); title.text = "📄 $name" }
            catch (e: Exception) { say("⚠ Not saved: ${e.message}", Ui.BAD) }
        }
    }

    private fun nameOf(u: Uri): String = try {
        contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null }
    } catch (_: Exception) { null } ?: (u.lastPathSegment?.substringAfterLast('/') ?: "document.docx")

    // ------------------------------------------------------------------ open and draw

    private fun load(u: Uri) {
        uri = u; name = nameOf(u)
        title.text = "📄 $name"
        say("⏳ Reading…")
        Thread {
            val r = runCatching {
                val b = contentResolver.openInputStream(u)!!.use { it.readBytes() }
                bytes = b
                WordDoc.open(name, b)
            }
            runOnUiThread {
                r.onSuccess { d -> show(d) }
                 .onFailure { e -> say("⚠ This file could not be read: ${e.message}. If it is an old .doc, try 🔄 Convert with Word on my PC.", Ui.BAD); convBtn.visibility = View.VISIBLE }
            }
        }.start()
    }

    private fun show(d: WordDoc) {
        doc = d; cur = null; views.clear(); pageBox.removeAllViews()
        val c: Context = this
        var tableBox: LinearLayout? = null; var tableNo = -2; var rowBox: LinearLayout? = null; var rowNo = -2; var cellBox: LinearLayout? = null; var cellKey = ""
        for (p in d.paras) {
            if (p.table >= 0) {
                if (p.table != tableNo) {
                    tableNo = p.table; rowNo = -2
                    tableBox = Ui.col(c).apply { background = Ui.round(PAGE, 0f, Ui.dp(c, 1), 0xFF9AA3B5.toInt()) }
                    pageBox.addView(tableBox, Ui.lp(top = Ui.dp(c, 6)))
                }
                if (p.row >= 0) {
                    if (p.row != rowNo) { rowNo = p.row; rowBox = Ui.row(c).apply { gravity = Gravity.TOP }; tableBox!!.addView(rowBox) }
                    val k = "${p.row}:${p.col}"
                    if (k != cellKey) {
                        cellKey = k
                        cellBox = Ui.col(c).apply { background = Ui.round(PAGE, 0f, 1, 0xFFC8CDD6.toInt()); setPadding(Ui.dp(c, 6), Ui.dp(c, 4), Ui.dp(c, 6), Ui.dp(c, 4)) }
                        rowBox!!.addView(cellBox, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f))
                    }
                    cellBox!!.addView(paraView(p))
                } else tableBox!!.addView(paraView(p).apply { setBackgroundColor(0xFFF6F7FA.toInt()) })
            } else {
                tableNo = -2
                p.image?.let { bmp -> pageBox.addView(ImageView(c).apply { setImageBitmap(bmp); adjustViewBounds = true }, Ui.lp(top = Ui.dp(c, 6))) }
                if (p.text.isNotEmpty() || p.image == null) pageBox.addView(paraView(p))
            }
        }
        convBtn.visibility = if (d.kind == "doc") View.VISIBLE else View.GONE
        val locked = d.paras.count { !it.editable }
        say("${d.paras.size} paragraph(s) · ${if (d.kind == "doc") "Word 97–2003 (.doc)" else "Word (.docx)"}" +
            (if (locked > 0) " · $locked with a field / picture / tracked change: direction only (🔒)" else "") + " · tap a paragraph, then ¶→ / ←¶")
    }

    private fun paraView(p: WPara): EditText {
        val c: Context = this
        val e = EditText(c).apply {
            setText(p.text)
            setTextColor(INK); setBackgroundColor(Color.TRANSPARENT)
            textSize = (if (p.heading > 0) maxOf(p.size, 15f) else p.size) * 1.22f
            typeface = Typeface.create(Typeface.SANS_SERIF, when { p.bold && p.italic -> Typeface.BOLD_ITALIC; p.bold -> Typeface.BOLD; p.italic -> Typeface.ITALIC; else -> Typeface.NORMAL })
            if (p.underline) paintFlags = paintFlags or Paint.UNDERLINE_TEXT_FLAG
            setPadding(Ui.dp(c, 2), Ui.dp(c, 3), Ui.dp(c, 2), Ui.dp(c, 3))
            minHeight = 0; minimumHeight = 0
            if (!p.editable) { keyListener = null; setTextIsSelectable(true); alpha = 0.85f }
            setOnFocusChangeListener { _, f -> if (f) cur = p }
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
                override fun afterTextChanged(s: Editable?) { if (p.editable) p.text = s?.toString() ?: "" }
            })
        }
        views[p] = e
        dir(e, p)
        return e
    }

    /** How a paragraph shows: its direction (right-to-left starts at the right) and its alignment. */
    private fun dir(e: EditText, p: WPara) {
        e.textDirection = if (p.rtl) View.TEXT_DIRECTION_RTL else View.TEXT_DIRECTION_LTR
        e.layoutDirection = if (p.rtl) View.LAYOUT_DIRECTION_RTL else View.LAYOUT_DIRECTION_LTR
        e.gravity = when (p.align) {
            "center" -> Gravity.CENTER_HORIZONTAL
            "right", "end" -> if (p.rtl) Gravity.START else Gravity.END
            else -> Gravity.START
        } or Gravity.TOP
    }

    // ------------------------------------------------------------------ direction

    private fun setDir(d: Char) {
        val p = cur ?: run { say("Tap a paragraph first, then ¶→ or ←¶", Ui.BAD); return }
        val rtl = d == 'R'
        if (p.rtl != rtl) { p.rtl = rtl; p.dirChanged = true }
        if (p.align in setOf("left", "right", "start", "end")) p.align = ""
        views[p]?.let { dir(it, p) }
        say(if (rtl) "←¶ This paragraph is right-to-left — 💾 Save to keep it" else "¶→ This paragraph is left-to-right — 💾 Save to keep it", Ui.GOOD)
    }

    private fun fixAll() {
        val d = doc ?: return
        var n = 0
        for (p in d.paras) {
            val want = Bidi.firstStrong(p.text) ?: continue
            val rtl = want == 'R'
            if (p.rtl != rtl) { p.rtl = rtl; p.dirChanged = true; if (p.align in setOf("left", "right", "start", "end")) p.align = ""; views[p]?.let { dir(it, p) }; n++ }
        }
        say(if (n == 0) "✓ Every paragraph already has the right direction" else "⇄ $n paragraph(s) put in their own direction (Arabic right-to-left, English left-to-right) — 💾 Save to keep it", Ui.GOOD)
    }

    // ------------------------------------------------------------------ ✦ AI on a paragraph

    private fun ai(translate: Boolean) {
        val p = cur ?: run { say("Tap a paragraph first", Ui.BAD); return }
        if (!p.editable) { say("🔒 This paragraph has a field or picture — it can't be rewritten here", Ui.BAD); return }
        val keyed = Providers.all.filter { store.hasKey(it) }
        val pr = keyed.firstOrNull { it.id == store.target } ?: keyed.firstOrNull() ?: run { say("Add an API key in Settings ▸ AI & API keys", Ui.BAD); return }
        val text = p.text
        if (text.isBlank()) return
        val toArabic = !Bidi.hasArabic(text)
        say("⏳ ${if (translate) "Translating" else "Enhancing"} with ${pr.label}…", 0xFFB9B0FF.toInt())
        Thread {
            val r = runCatching {
                val user = if (translate) "Translate this paragraph to ${if (toArabic) "Arabic" else "English"}. Return only the translation, as one paragraph:\n\n$text"
                           else Modes.firstMessage(Modes.byId("grammar"), store.custom, text)
                Providers.chatSmart(pr, store.key(pr), store.model(pr), Modes.system(store, text), listOf(Msg("user", user)), fast = true) { m -> say(m) }.text.trim()
            }
            runOnUiThread {
                r.onSuccess { out ->
                    p.text = out
                    views[p]?.setText(out)
                    val rtl = Bidi.firstStrong(out) == 'R'
                    if (p.rtl != rtl) { p.rtl = rtl; p.dirChanged = true; views[p]?.let { dir(it, p) } }
                    say("✓ ${if (translate) "Translated" else "Enhanced"} — 💾 Save to keep it", Ui.GOOD)
                }.onFailure { e -> say("⚠ ${e.message}", Ui.BAD) }
            }
        }.start()
    }

    // ------------------------------------------------------------------ save

    private fun save(asNew: Boolean) {
        val d = doc ?: return
        if (!d.dirty && !asNew) { say("Nothing changed yet"); return }
        say("⏳ Saving…")
        Thread {
            val r = runCatching {
                if (d.needsPcForDirection) viaPc(d) else d.save() to name
            }
            runOnUiThread {
                r.onSuccess { (b, n) -> write(b, n, asNew) }
                 .onFailure { e ->
                     if (d.kind == "doc" && d.needsPcForDirection) {
                         // Word on the PC is not reachable: the direction is kept in a .docx copy instead (nothing is lost).
                         say("⚠ Word on your PC is not reachable (${e.message}). Saving a .docx copy with every change — convert it to .doc later.", Ui.BAD)
                         Thread {
                             val x = runCatching { val docx = pcLessDocx(d); docx }
                             runOnUiThread { x.onSuccess { write(it, name.substringBeforeLast('.') + ".docx", true) }.onFailure { e2 -> say("⚠ Not saved: ${e2.message}", Ui.BAD) } }
                         }.start()
                     } else say("⚠ Not saved: ${e.message}", Ui.BAD)
                 }
            }
        }.start()
    }

    private fun write(b: ByteArray, n: String, asNew: Boolean) {
        val u = uri
        if (!asNew && u != null && n == name) {
            try { contentResolver.openOutputStream(u, "wt")!!.use { it.write(b) }; afterSave(b); say("✓ Saved: $name", Ui.GOOD); return }
            catch (_: Exception) { /* read-only (opened from another app): ask where to save */ }
        }
        pendingSave = b; afterSave(b)
        val mime = if (n.endsWith(".doc", true)) "application/msword" else "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        try { startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType(mime).putExtra(Intent.EXTRA_TITLE, n), SAVE_AS) }
        catch (_: Exception) { say("⚠ No place to save on this phone", Ui.BAD) }
    }

    /** After saving, the file is opened again from what was written, so the next save starts from it. */
    private fun afterSave(b: ByteArray) {
        bytes = b
        try { val nd = WordDoc.open(name, b); runOnUiThread { show(nd) } } catch (_: Exception) { }
    }

    /** .doc with a changed direction: Word on the PC turns it into .docx, Nova sets the direction there, Word turns it back into .doc. */
    private fun viaPc(d: WordDoc): Pair<ByteArray, String> {
        if (!PcLink.paired(this)) throw Exception("the phone is not linked to your PC")
        val textDone = d.save()
        val docx = pc("todocx", textDone, name)
        val x = WordDoc.open(name.substringBeforeLast('.') + ".docx", docx)
        carryDirections(d, x)
        val back = pc("todoc", x.save(), x.name)
        return back to name
    }

    private fun pcLessDocx(d: WordDoc): ByteArray {
        // A .docx made on the phone from the paragraphs (text and direction; the old file's layout stays in the original .doc).
        val sb = StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>")
        fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        for (p in d.paras) {
            sb.append("<w:p>"); if (p.rtl) sb.append("<w:pPr><w:bidi/></w:pPr>")
            sb.append("<w:r>"); if (p.rtl) sb.append("<w:rPr><w:rtl/></w:rPr>")
            sb.append("<w:t xml:space=\"preserve\">").append(esc(Bidi.strip(p.text))).append("</w:t></w:r></w:p>")
        }
        sb.append("</w:body></w:document>")
        val o = java.io.ByteArrayOutputStream()
        java.util.zip.ZipOutputStream(o).use { z ->
            fun put(n: String, s: String) { z.putNextEntry(java.util.zip.ZipEntry(n)); z.write(s.toByteArray()); z.closeEntry() }
            put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")
            put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")
            put("word/document.xml", sb.toString())
        }
        return o.toByteArray()
    }

    /** The ¶→ / ←¶ choices of the .doc paragraphs, onto the same paragraphs of Word's .docx (matched by their text, in order). */
    private fun carryDirections(from: WordDoc, to: WordDoc) {
        var j = 0
        for (p in from.paras.filter { it.dirChanged }) {
            val key = Bidi.strip(p.text).trim()
            val k = (j until to.paras.size).firstOrNull { Bidi.strip(to.paras[it].text).trim() == key } ?: continue
            to.paras[k].rtl = p.rtl; to.paras[k].dirChanged = true; j = k + 1
        }
    }

    /** Word on the PC: .doc → .docx or .docx → .doc (through the encrypted phone ⇄ PC link). */
    private fun pc(action: String, data: ByteArray, n: String): ByteArray {
        val res = PcLink.ask(this, "word", JSONObject().put("action", action).put("name", n).put("data", Base64.encodeToString(data, Base64.NO_WRAP))) { m -> say(m) }
        if (res.has("error")) throw Exception(res.optString("error"))
        return Base64.decode(res.getString("data"), Base64.DEFAULT)
    }

    private fun convert() {
        val b = bytes ?: return
        say("⏳ Word on your PC is converting it to .docx…", 0xFFB9B0FF.toInt())
        Thread {
            val r = runCatching { pc("todocx", b, name) }
            runOnUiThread {
                r.onSuccess { x ->
                    name = name.substringBeforeLast('.') + ".docx"; uri = null; bytes = x
                    title.text = "📄 $name"
                    show(WordDoc.open(name, x))
                    say("✓ Converted to .docx by Word — edit it, then 💾 Save (as a new .docx file)", Ui.GOOD)
                }.onFailure { e -> say("⚠ ${e.message} — is your PC on with Nova running?", Ui.BAD) }
            }
        }.start()
    }

    // ------------------------------------------------------------------ share

    private fun share() {
        val d = doc ?: return
        val items = arrayOf("📄 This file (${name.substringAfterLast('.', "docx")})", "🖨 As PDF", "📝 As text")
        android.app.AlertDialog.Builder(this).setTitle("Share").setItems(items) { _, w ->
            Thread {
                val r = runCatching {
                    val dir = File(PcLink.dir(this), "Word").apply { mkdirs() }
                    when (w) {
                        0 -> { val f = File(dir, name); f.writeBytes(if (d.dirty && !d.needsPcForDirection) d.save() else bytes ?: d.save()); f to (if (name.endsWith(".doc", true)) "application/msword" else "application/vnd.openxmlformats-officedocument.wordprocessingml.document") }
                        1 -> { val f = File(dir, name.substringBeforeLast('.') + ".pdf"); pdf(f); f to "application/pdf" }
                        else -> { val f = File(dir, name.substringBeforeLast('.') + ".txt"); f.writeText(d.plain()); f to "text/plain" }
                    }
                }
                runOnUiThread {
                    r.onSuccess { (f, mime) ->
                        val u = Uri.parse("content://${PcFiles.AUTH}/Word/${Uri.encode(f.name)}")
                        startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(mime).putExtra(Intent.EXTRA_STREAM, u)
                            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "Share ${f.name}"))
                    }.onFailure { e -> say("⚠ ${e.message}", Ui.BAD) }
                }
            }.start()
        }.show()
    }

    /** The page as a PDF (A4 width), drawn from what is on the screen. */
    private fun pdf(f: File) {
        val latch = java.util.concurrent.CountDownLatch(1)
        var err: Throwable? = null
        runOnUiThread {
            try {
                val v = pageBox
                val pageW = 595; val pageH = 842
                val scale = pageW.toFloat() / v.width
                val pdf = PdfDocument()
                var y = 0; var n = 1
                val total = v.height
                while (y < total) {
                    val page = pdf.startPage(PdfDocument.PageInfo.Builder(pageW, pageH, n++).create())
                    val cv = page.canvas
                    cv.scale(scale, scale); cv.translate(0f, -y.toFloat())
                    v.draw(cv)
                    pdf.finishPage(page)
                    y += (pageH / scale).toInt()
                }
                f.outputStream().use { pdf.writeTo(it) }
                pdf.close()
            } catch (e: Throwable) { err = e } finally { latch.countDown() }
        }
        latch.await()
        err?.let { throw it }
    }
}
