package com.nova.enhance

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import org.w3c.dom.Document
import org.w3c.dom.Element
import org.w3c.dom.Node
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import javax.xml.parsers.DocumentBuilderFactory
import javax.xml.transform.OutputKeys
import javax.xml.transform.TransformerFactory
import javax.xml.transform.dom.DOMSource
import javax.xml.transform.stream.StreamResult

/**
 * 📄 A Word file on the phone: .docx read and written in place (only the paragraphs you changed are rewritten — styles, headers,
 * footers, pictures and layout stay as Word made them), .doc (Word 97–2003) read and its text edited with Apache POI.
 * Direction: ¶→ / ←¶ set the paragraph's real Word direction (w:bidi) so it is right in Word on the PC too. Designed by A.HAMROUNI
 */
class WPara(
    var text: String,
    var rtl: Boolean,
    var align: String = "",          // "", left, right, center, both
    val bold: Boolean = false, val italic: Boolean = false, val underline: Boolean = false,
    val size: Float = 11f,           // points
    val heading: Int = 0,
    val table: Int = -1, val row: Int = -1, val col: Int = -1,
    val image: Bitmap? = null,
    val editable: Boolean = true,
    internal val node: Element? = null,     // .docx
    internal val index: Int = -1            // .doc paragraph number
) {
    val original = text
    var dirChanged = false
    val changed get() = text != original
}

class WordDoc(val name: String, val kind: String, val paras: MutableList<WPara>, private val parts: LinkedHashMap<String, ByteArray>?,
              private val dom: Document?, private val doc: Any?) {

    val dirty get() = paras.any { it.changed || it.dirChanged }
    val needsPcForDirection get() = kind == "doc" && paras.any { it.dirChanged }

    companion object {
        private const val W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        private const val R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        private const val A = "http://schemas.openxmlformats.org/drawingml/2006/main"

        fun open(name: String, bytes: ByteArray): WordDoc {
            val isZip = bytes.size > 4 && bytes[0] == 'P'.code.toByte() && bytes[1] == 'K'.code.toByte()
            return if (isZip) openDocx(name, bytes) else openDoc(name, bytes)
        }

        // ------------------------------------------------------------------ .docx

        private fun openDocx(name: String, bytes: ByteArray): WordDoc {
            val parts = LinkedHashMap<String, ByteArray>()
            ZipInputStream(ByteArrayInputStream(bytes)).use { z ->
                while (true) { val e = z.nextEntry ?: break; if (!e.isDirectory) parts[e.name] = z.readBytes() }
            }
            val main = parts["word/document.xml"] ?: throw Exception("not a Word document (word/document.xml is missing)")
            val f = DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true }
            val dom = f.newDocumentBuilder().parse(ByteArrayInputStream(main))
            // pictures: relationship id → media file
            val rels = HashMap<String, String>()
            parts["word/_rels/document.xml.rels"]?.let { rb ->
                val rd = f.newDocumentBuilder().parse(ByteArrayInputStream(rb))
                val l = rd.getElementsByTagName("Relationship")
                for (i in 0 until l.length) { val e = l.item(i) as Element; rels[e.getAttribute("Id")] = "word/" + e.getAttribute("Target").removePrefix("/word/").removePrefix("./") }
            }
            val out = mutableListOf<WPara>()
            val body = dom.getElementsByTagNameNS(W, "body").item(0) as? Element ?: throw Exception("empty document")
            var tableNo = 0
            fun walk(n: Node, t: Int, r: Int, c: Int) {
                var ch = n.firstChild
                var row = -1
                while (ch != null) {
                    if (ch is Element && ch.namespaceURI == W) when (ch.localName) {
                        "p" -> out += para(ch, t, r, c, parts, rels)
                        "tbl" -> { val my = tableNo++; walk(ch, my, -1, -1) }
                        "tr" -> { row++; var col = -1; var cc = ch.firstChild
                            while (cc != null) { if (cc is Element && cc.localName == "tc") { col++; walk(cc, t, row, col) } else if (cc is Element && cc.localName == "sdt") walk(cc, t, row, col); cc = cc.nextSibling } }
                        "sdt", "sdtContent", "customXml", "smartTag" -> walk(ch, t, r, c)
                    }
                    ch = ch.nextSibling
                }
            }
            walk(body, -1, -1, -1)
            return WordDoc(name, "docx", out, parts, dom, null)
        }

        private fun child(e: Element, local: String): Element? {
            var c = e.firstChild
            while (c != null) { if (c is Element && c.namespaceURI == W && c.localName == local) return c; c = c.nextSibling }
            return null
        }

        private fun para(p: Element, t: Int, r: Int, c: Int, parts: Map<String, ByteArray>, rels: Map<String, String>): WPara {
            val sb = StringBuilder()
            var complex = false
            var img: Bitmap? = null
            fun text(n: Node) {
                var ch = n.firstChild
                while (ch != null) {
                    if (ch is Element) when {
                        ch.namespaceURI == W && ch.localName == "t" -> sb.append(ch.textContent)
                        ch.namespaceURI == W && ch.localName == "tab" -> sb.append('\t')
                        ch.namespaceURI == W && (ch.localName == "br" || ch.localName == "cr") -> sb.append('\n')
                        ch.namespaceURI == W && ch.localName in setOf("fldChar", "instrText", "fldSimple", "object", "ins", "del", "moveFrom", "moveTo") -> { complex = true; text(ch) }
                        ch.localName == "AlternateContent" || ch.localName == "oMath" || ch.localName == "oMathPara" || ch.localName == "txbxContent" -> complex = true
                        ch.namespaceURI == W && ch.localName == "drawing" -> {
                            complex = true
                            val b = ch.getElementsByTagNameNS(A, "blip").item(0) as? Element
                            val id = b?.getAttributeNS(R, "embed").orEmpty()
                            if (img == null && id.isNotEmpty()) rels[id]?.let { parts[it] }?.let { img = decode(it) }
                        }
                        ch.namespaceURI == W && ch.localName == "pPr" -> {}
                        else -> text(ch)
                    }
                    ch = ch.nextSibling
                }
            }
            text(p)
            val pPr = child(p, "pPr")
            val bidi = pPr?.let { child(it, "bidi") }?.let { it.getAttributeNS(W, "val") !in setOf("0", "false") } ?: false
            val jc = pPr?.let { child(it, "jc") }?.getAttributeNS(W, "val").orEmpty()
            val style = pPr?.let { child(it, "pStyle") }?.getAttributeNS(W, "val").orEmpty()
            val h = Regex("(?i)heading\\s?(\\d)|^(\\d)$").find(style)?.let { (it.groupValues[1].ifEmpty { it.groupValues[2] }).toIntOrNull() } ?: if (style.equals("Title", true)) 1 else 0
            val firstR = p.getElementsByTagNameNS(W, "rPr").let { l -> (0 until l.length).map { l.item(it) as Element }.firstOrNull { it.parentNode.localName == "r" } }
            fun on(tag: String) = firstR?.let { child(it, tag) }?.let { it.getAttributeNS(W, "val") !in setOf("0", "false", "none") } ?: false
            val sz = firstR?.let { child(it, "sz") }?.getAttributeNS(W, "val")?.toFloatOrNull()?.div(2f) ?: if (h > 0) 16f - h * 1.5f else 11f
            val s = sb.toString()
            return WPara(s, bidi, jc, on("b") || h > 0, on("i"), on("u"), sz, h, t, r, c, img, !complex, p)
        }

        private fun decode(b: ByteArray): Bitmap? = try {
            val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(b, 0, b.size, o)
            var s = 1; while (o.outWidth / s > 1400) s *= 2
            BitmapFactory.decodeByteArray(b, 0, b.size, BitmapFactory.Options().apply { inSampleSize = s })
        } catch (_: Throwable) { null }

        // ------------------------------------------------------------------ .doc (Word 97–2003, Apache POI)

        private fun openDoc(name: String, bytes: ByteArray): WordDoc {
            val d = org.apache.poi.hwpf.HWPFDocument(ByteArrayInputStream(bytes))
            val range = d.range
            val out = mutableListOf<WPara>()
            var tableNo = -1; var inTable = false
            for (i in 0 until range.numParagraphs()) {
                val p = range.getParagraph(i)
                val raw = p.text()
                val t = raw.trimEnd('\r', '\u0007', '\u000C').replace('\u000B', '\n').replace(Regex("[\u0001-\u0008\u000E-\u001F]"), "")
                val tbl = p.isInTable
                if (tbl && !inTable) tableNo++
                inTable = tbl
                if (tbl && raw.endsWith("\u0007") && t.isEmpty()) continue   // the end-of-row mark
                val cr = if (p.numCharacterRuns() > 0) p.getCharacterRun(0) else null
                val j = when (p.justification.toInt()) { 1 -> "center"; 2 -> "right"; 3 -> "both"; else -> "" }
                // fields / pictures / objects inside: shown, not edited (their codes would break)
                val complex = raw.any { it == '\u0013' || it == '\u0001' || it == '\u0008' }
                out += WPara(t, Bidi.firstStrong(t) == 'R', j, cr?.isBold ?: false, cr?.isItalic ?: false, (cr?.underlineCode ?: 0) != 0,
                    (cr?.fontSize ?: 22) / 2f, if (p.styleIndex.toInt() in 1..9) p.styleIndex.toInt() else 0,
                    if (tbl) tableNo else -1, -1, -1, null, !complex, null, i)
            }
            return WordDoc(name, "doc", out, null, null, d)
        }
    }

    // ------------------------------------------------------------------ saving

    /** The file with your changes, in its own format (.docx or .doc). For .doc, direction changes need Word on the PC (see WordActivity). */
    fun save(): ByteArray = if (kind == "docx") saveDocx() else saveDoc()

    private fun el(tag: String) = dom!!.createElementNS(W, "w:$tag")

    private fun saveDocx(): ByteArray {
        val d = dom!!
        for (p in paras) {
            val node = p.node ?: continue
            if (p.changed && p.editable) rewrite(node, p.text, p.rtl)
            if (p.dirChanged) direction(node, p.rtl)
        }
        val bo = ByteArrayOutputStream()
        TransformerFactory.newInstance().newTransformer().apply {
            setOutputProperty(OutputKeys.ENCODING, "UTF-8"); setOutputProperty(OutputKeys.STANDALONE, "yes")
        }.transform(DOMSource(d), StreamResult(bo))
        val out = ByteArrayOutputStream()
        ZipOutputStream(out).use { z ->
            for ((n, b) in parts!!) {
                z.putNextEntry(ZipEntry(n))
                z.write(if (n == "word/document.xml") bo.toByteArray() else b)
                z.closeEntry()
            }
        }
        return out.toByteArray()
    }

    /** A changed paragraph: its runs replaced by the new text, keeping the first run's formatting; Arabic and English parts in their own runs. */
    private fun rewrite(p: Element, text: String, rtl: Boolean) {
        val firstRun = p.getElementsByTagNameNS(W, "r").item(0) as? Element
        val rPr = firstRun?.let { child(it, "rPr") }?.cloneNode(true) as? Element
        rPr?.let { child(it, "rtl")?.let { r -> it.removeChild(r) } }
        var c = p.firstChild
        while (c != null) {
            val nx = c.nextSibling
            if (c is Element && c.localName != "pPr" && c.localName != "bookmarkStart" && c.localName != "bookmarkEnd" && c.localName != "sectPr") p.removeChild(c)
            c = nx
        }
        val clean = Bidi.strip(text)
        for ((seg, arabic) in segments(clean)) {
            val r = el("r")
            val pr = (rPr?.cloneNode(true) as? Element) ?: el("rPr")
            if (arabic) pr.appendChild(el("rtl"))
            if (pr.hasChildNodes()) r.appendChild(pr)
            var first = true
            for (piece in seg.split('\n')) {
                if (!first) r.appendChild(el("br"))
                first = false
                val tabs = piece.split('\t')
                tabs.forEachIndexed { i, tp ->
                    if (i > 0) r.appendChild(el("tab"))
                    if (tp.isNotEmpty()) { val t = el("t"); t.setAttributeNS("http://www.w3.org/XML/1998/namespace", "xml:space", "preserve"); t.textContent = tp; r.appendChild(t) }
                }
            }
            p.appendChild(r)
        }
    }

    /** Arabic runs and the rest, so Word shows numbers and English in place inside Arabic. */
    private fun segments(s: String): List<Pair<String, Boolean>> {
        val out = mutableListOf<Pair<String, Boolean>>()
        val sb = StringBuilder(); var cur: Boolean? = null
        for (ch in s) {
            val a = if (Bidi.isRtl(ch)) true else if (Bidi.isLtr(ch)) false else null
            if (a != null && cur != null && a != cur) { out += sb.toString() to cur; sb.clear() }
            if (a != null) cur = a
            sb.append(ch)
        }
        if (sb.isNotEmpty()) out += sb.toString() to (cur ?: false)
        return out
    }

    /** ¶→ / ←¶ in Word itself: w:bidi on the paragraph (alignment follows it), w:rtl on its Arabic runs. */
    private fun direction(p: Element, rtl: Boolean) {
        var pPr = child(p, "pPr")
        if (pPr == null) { pPr = el("pPr"); p.insertBefore(pPr, p.firstChild) }
        child(pPr, "bidi")?.let { pPr.removeChild(it) }
        if (rtl) {
            // w:bidi comes before w:jc / w:rPr in the schema order
            val before = listOf("adjustRightInd", "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr", "pPrChange")
                .firstNotNullOfOrNull { child(pPr, it) }
            pPr.insertBefore(el("bidi"), before)
        }
        child(pPr, "jc")?.let { j -> if (j.getAttributeNS(W, "val") in setOf("left", "right", "start", "end")) pPr.removeChild(j) }
        val runs = p.getElementsByTagNameNS(W, "r")
        for (i in 0 until runs.length) {
            val r = runs.item(i) as Element
            val txt = r.textContent ?: ""
            var rPr = child(r, "rPr")
            if (rtl && Bidi.hasArabic(txt)) { if (rPr == null) { rPr = el("rPr"); r.insertBefore(rPr, r.firstChild) }; if (child(rPr, "rtl") == null) rPr.appendChild(el("rtl")) }
            if (!rtl) rPr?.let { x -> child(x, "rtl")?.let { x.removeChild(it) } }
        }
    }

    private fun saveDoc(): ByteArray {
        val d = doc as org.apache.poi.hwpf.HWPFDocument
        val range = d.range
        // from the end, so the earlier positions stay right
        for (p in paras.filter { it.changed && it.editable && it.index >= 0 }.sortedByDescending { it.index }) {
            val para = range.getParagraph(p.index)
            val old = para.text().trimEnd('\r', '\u0007', '\u000C')
            val new = Bidi.strip(p.text).replace('\n', '\u000B')
            if (old.isEmpty()) para.insertBefore(new) else para.replaceText(old, new)
        }
        return ByteArrayOutputStream().also { d.write(it) }.toByteArray()
    }

    /** The plain text (for AI, copy and share as text). */
    fun plain(): String = paras.joinToString("\n") { it.text }
}
