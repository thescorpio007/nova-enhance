package com.nova.enhance

import android.util.Base64
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.Charset
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * ✉ Outlook files read on the phone itself — no PC, no other app:
 * .msg and .oft (classic Outlook), .eml (new Outlook / Outlook 365), .ics (meeting) and .vcf (contact).
 * Gives the email as a page (fonts, tables, pictures inside it) and its attachments. Designed by A.HAMROUNI
 */
object OutlookFiles {
    class Att(val name: String, val data: ByteArray?, val mime: String = "", val cid: String = "", val nested: Mail? = null)
    class Mail {
        var subject = ""; var from = ""; var to = ""; var cc = ""; var date = 0L
        var html = ""; var text = ""
        val atts = ArrayList<Att>()
    }

    val EXT = setOf("msg", "oft", "eml", "ics", "vcs", "vcf")

    fun read(name: String, data: ByteArray): Mail = when (name.substringAfterLast('.', "").lowercase()) {
        "msg", "oft" -> Msg(data).mail()
        "eml" -> Eml.parse(data)
        "ics", "vcs" -> ics(data)
        "vcf" -> vcf(data)
        else -> if (data.size > 8 && data[0] == 0xD0.toByte() && data[1] == 0xCF.toByte()) Msg(data).mail() else Eml.parse(data)
    }

    private fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    /** Puts [add] right after the first match of [tag] (case-insensitive); unchanged when there is none. */
    private fun after(s: String, tag: String, add: String): String {
        val m = Regex(tag, RegexOption.IGNORE_CASE).find(s) ?: return s
        return s.substring(0, m.range.last + 1) + add + s.substring(m.range.last + 1)
    }

    /** The whole email as one page: a small header, then the body with its pictures inside. */
    fun page(m: Mail): String {
        var body = if (m.html.isNotBlank()) m.html else "<pre style='white-space:pre-wrap;font-family:Calibri,Segoe UI,Arial,sans-serif;font-size:15px'>" + esc(m.text) + "</pre>"
        for (a in m.atts) {
            val d = a.data ?: continue
            if (a.cid.isBlank() || d.size > 1_500_000) continue
            val mime = a.mime.ifBlank { when (a.name.substringAfterLast('.', "").lowercase()) { "jpg", "jpeg" -> "image/jpeg"; "gif" -> "image/gif"; "bmp" -> "image/bmp"; else -> "image/png" } }
            body = body.replace("cid:" + a.cid, "data:$mime;base64," + Base64.encodeToString(d, Base64.NO_WRAP), ignoreCase = true)
        }
        body = body.replace(Regex("<script\\b.*?</script>", setOf(RegexOption.IGNORE_CASE, RegexOption.DOT_MATCHES_ALL)), "")
            .replace(Regex("<meta[^>]+charset[^>]*>", RegexOption.IGNORE_CASE), "")
        val when0 = if (m.date > 0) SimpleDateFormat("EEE dd MMM yyyy HH:mm", Locale.UK).format(Date(m.date)) else ""
        val head = "<div style='font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#444;border-bottom:1px solid #ddd;padding:0 0 8px;margin:0 0 12px'>" +
            (if (m.from.isNotBlank()) "<b>From</b> ${esc(m.from)}<br>" else "") + (if (m.to.isNotBlank()) "<b>To</b> ${esc(m.to)}<br>" else "") +
            (if (m.cc.isNotBlank()) "<b>Cc</b> ${esc(m.cc)}<br>" else "") + (if (when0.isNotBlank()) esc(when0) else "") + "</div>"
        val css = "<meta charset='utf-8'><style>html{background:#fff}body{font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:14.5px;margin:14px 16px;color:#222}img{max-width:100%;height:auto}</style>"
        return if (Regex("<body[^>]*>", RegexOption.IGNORE_CASE).containsMatchIn(body)) {
            val withHead = if (Regex("<head[^>]*>", RegexOption.IGNORE_CASE).containsMatchIn(body)) after(body, "<head[^>]*>", css)
                else after(body, "<html[^>]*>", "<head>$css</head>")
            after(withHead, "<body[^>]*>", head)
        } else "<html><head>$css</head><body>$head$body</body></html>"
    }

    /** The attachments to list (pictures shown inside the page are left out). */
    fun listed(m: Mail) = m.atts.filter { a -> a.nested != null || (a.data != null && !(a.cid.isNotBlank() && m.html.contains("cid:" + a.cid, true))) }

    private fun cs(name: String?): Charset = try { Charset.forName(name?.trim()?.trim('"') ?: "windows-1252") } catch (_: Exception) { Charset.forName("windows-1252") }
    private fun cp(n: Int): Charset = when (n) {
        65001 -> Charsets.UTF_8; 1200 -> Charsets.UTF_16LE; 20127 -> Charsets.US_ASCII; 28591 -> Charsets.ISO_8859_1
        in 28592..28606 -> cs("ISO-8859-" + (n - 28590)); 932 -> cs("Shift_JIS"); 936 -> cs("GBK"); 949 -> cs("EUC-KR"); 950 -> cs("Big5")
        in 1250..1258 -> cs("windows-$n"); 874 -> cs("windows-874")
        else -> cs("windows-1252")
    }

    // ------------------------------------------------------------------ .msg / .oft (Outlook's compound file)

    private class Entry(val name: String, val type: Int, val left: Int, val right: Int, val child: Int, val start: Int, val size: Long)

    private class Msg(val d: ByteArray) {
        val bb: ByteBuffer = ByteBuffer.wrap(d).order(ByteOrder.LITTLE_ENDIAN)
        val sec: Int; val mini: Int; val cutoff: Int
        val fat: IntArray; val miniFat: IntArray
        val dirs = ArrayList<Entry>()
        val miniStream: ByteArray

        init {
            if (d.size < 512 || bb.getInt(0) != 0xE011CFD0.toInt()) throw Exception("not an Outlook .msg file")
            sec = 1 shl bb.getShort(30).toInt(); mini = 1 shl bb.getShort(32).toInt(); cutoff = bb.getInt(56)
            // FAT: the sectors listed in the header (109) and in the DIFAT chain.
            val fatSecs = ArrayList<Int>()
            for (i in 0 until 109) { val s = bb.getInt(76 + i * 4); if (s >= 0) fatSecs.add(s) }
            var dif = bb.getInt(68); var guard = 0
            while (dif >= 0 && guard++ < 10000) {
                val o = off(dif)
                for (i in 0 until sec / 4 - 1) { val s = bb.getInt(o + i * 4); if (s >= 0) fatSecs.add(s) }
                dif = bb.getInt(o + sec - 4)
            }
            fat = IntArray(fatSecs.size * (sec / 4))
            for ((k, s) in fatSecs.withIndex()) { val o = off(s); if (o + sec <= d.size) for (i in 0 until sec / 4) fat[k * (sec / 4) + i] = bb.getInt(o + i * 4) }
            val dirBytes = chain(bb.getInt(48), -1)
            val db = ByteBuffer.wrap(dirBytes).order(ByteOrder.LITTLE_ENDIAN)
            for (i in 0 until dirBytes.size / 128) {
                val o = i * 128
                val len = (db.getShort(o + 64).toInt() and 0xFFFF).coerceIn(0, 64)
                val name = if (len >= 2) String(dirBytes, o, len - 2, Charsets.UTF_16LE) else ""
                dirs.add(Entry(name, dirBytes[o + 66].toInt(), db.getInt(o + 68), db.getInt(o + 72), db.getInt(o + 76), db.getInt(o + 116), db.getLong(o + 120) and 0xFFFFFFFFL))
            }
            val mf = chain(bb.getInt(60), -1)
            miniFat = IntArray(mf.size / 4).also { ByteBuffer.wrap(mf).order(ByteOrder.LITTLE_ENDIAN).asIntBuffer().get(it) }
            miniStream = if (dirs.isNotEmpty()) chain(dirs[0].start, dirs[0].size) else ByteArray(0)
        }

        fun off(s: Int) = (s + 1) * sec

        fun chain(start: Int, size: Long): ByteArray {
            val out = ByteArrayOutputStream()
            var s = start; var guard = 0
            while (s >= 0 && s < fat.size && guard++ < 2_000_000) {
                val o = off(s); if (o >= d.size) break
                out.write(d, o, minOf(sec, d.size - o))
                if (size >= 0 && out.size() >= size) break
                s = fat[s]
            }
            val b = out.toByteArray()
            return if (size >= 0 && b.size > size) b.copyOf(size.toInt()) else b
        }

        fun stream(e: Entry): ByteArray {
            if (e.size >= cutoff) return chain(e.start, e.size)
            val out = ByteArrayOutputStream()
            var s = e.start; var guard = 0
            while (s >= 0 && s < miniFat.size && guard++ < 2_000_000) {
                val o = s * mini; if (o >= miniStream.size) break
                out.write(miniStream, o, minOf(mini, miniStream.size - o))
                if (out.size() >= e.size) break
                s = miniFat[s]
            }
            val b = out.toByteArray()
            return if (b.size > e.size) b.copyOf(e.size.toInt()) else b
        }

        fun children(e: Entry): List<Entry> {
            val out = ArrayList<Entry>()
            fun walk(i: Int, depth: Int) {
                if (i < 0 || i >= dirs.size || depth > 4000) return
                val x = dirs[i]
                walk(x.left, depth + 1); out.add(x); walk(x.right, depth + 1)
            }
            walk(e.child, 0)
            return out
        }

        fun mail(): Mail = storage(dirs[0], 32)

        /** One message (the file itself, or an email attached inside it). */
        fun storage(root: Entry, propHeader: Int): Mail {
            val kids = children(root)
            val by = kids.associateBy { it.name.lowercase() }
            val m = Mail()
            // Fixed-size values (dates, code page) live in the properties stream.
            var codepage = 1252; var bodyCp = 0
            by["__properties_version1.0"]?.let { e ->
                val p = stream(e); val pb = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN)
                var o = propHeader
                while (o + 16 <= p.size) {
                    val tag = pb.getInt(o); val id = tag ushr 16; val type = tag and 0xFFFF
                    if (type == 0x0040 && (id == 0x0039 || id == 0x0E06) && (m.date == 0L || id == 0x0039)) {
                        val ft = pb.getLong(o + 8); if (ft > 0) m.date = ft / 10000 - 11644473600000L
                    }
                    if (type == 0x0003 && id == 0x3FFD) codepage = pb.getInt(o + 8)
                    if (type == 0x0003 && id == 0x3FDE) bodyCp = pb.getInt(o + 8)
                    o += 16
                }
            }
            fun bin(id: String): ByteArray? = by["__substg1.0_${id}0102".lowercase()]?.let { stream(it) }
            fun str(id: String): String =
                by["__substg1.0_${id}001f".lowercase()]?.let { String(stream(it), Charsets.UTF_16LE).trimEnd('\u0000') }
                    ?: by["__substg1.0_${id}001e".lowercase()]?.let { String(stream(it), cp(codepage)).trimEnd('\u0000') } ?: ""
            m.subject = str("0037")
            val fromName = str("0C1A").ifBlank { str("0042") }
            val fromAddr = str("5D01").ifBlank { str("5D02") }.ifBlank { str("0C1F").takeIf { it.contains('@') } ?: "" }.ifBlank { str("0065").takeIf { it.contains('@') } ?: "" }
            m.from = if (fromAddr.isNotBlank() && !fromName.contains(fromAddr, true)) "$fromName <$fromAddr>".trim() else fromName
            m.to = str("0E04"); m.cc = str("0E03")
            m.text = str("1000")
            // The page: HTML as stored, or taken out of Outlook's RTF.
            val h = bin("1013")
            if (h != null && h.isNotEmpty()) {
                val probe = String(h, 0, minOf(h.size, 3000), Charsets.ISO_8859_1)
                val named = Regex("charset\\s*=\\s*[\"']?([A-Za-z0-9_\\-]+)", RegexOption.IGNORE_CASE).find(probe)?.groupValues?.get(1)
                m.html = String(h, if (named != null) cs(named) else if (bodyCp > 0) cp(bodyCp) else Charsets.UTF_8)
            } else {
                val hs = str("1013")
                if (hs.isNotBlank()) m.html = hs
                else bin("1009")?.let { r -> try { m.html = Rtf.html(Rtf.decompress(r)) } catch (_: Exception) { } }
            }
            // Attachments (and emails attached inside).
            for (k in kids.filter { it.type == 1 && it.name.startsWith("__attach_version1.0_", true) }.sortedBy { it.name }) {
                try {
                    val ab = children(k).associateBy { it.name.lowercase() }
                    fun astr(id: String): String =
                        ab["__substg1.0_${id}001f".lowercase()]?.let { String(stream(it), Charsets.UTF_16LE).trimEnd('\u0000') }
                            ?: ab["__substg1.0_${id}001e".lowercase()]?.let { String(stream(it), cp(codepage)).trimEnd('\u0000') } ?: ""
                    val name = astr("3707").ifBlank { astr("3704") }.ifBlank { astr("3001") }.ifBlank { "attachment" }
                    val inner = ab["__substg1.0_3701000d"]
                    if (inner != null) {
                        val nm = try { storage(inner, 24) } catch (_: Exception) { null }
                        if (nm != null) m.atts.add(Att((nm.subject.ifBlank { name }) + ".msg", null, nested = nm))
                        continue
                    }
                    val data = ab["__substg1.0_37010102"]?.let { stream(it) } ?: continue
                    m.atts.add(Att(name, data, astr("370E"), astr("3712")))
                } catch (_: Exception) { }
            }
            return m
        }
    }

    // ------------------------------------------------------------------ Outlook's compressed RTF → the HTML inside it

    private object Rtf {
        private val PRE = ("{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier" +
            "{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx").toByteArray(Charsets.US_ASCII)

        fun decompress(src: ByteArray): ByteArray {
            if (src.size < 16) return ByteArray(0)
            val b = ByteBuffer.wrap(src).order(ByteOrder.LITTLE_ENDIAN)
            val raw = b.getInt(4); val magic = b.getInt(8)
            if (magic == 0x414C454D) return src.copyOfRange(16, minOf(src.size, 16 + raw))       // "MELA": not compressed
            if (magic != 0x75465A4C) throw Exception("unknown RTF packing")
            val dict = ByteArray(4096); PRE.copyInto(dict)
            var w = PRE.size
            val out = ByteArrayOutputStream(maxOf(raw, 16))
            var i = 16
            while (i < src.size) {
                val control = src[i++].toInt() and 0xFF
                for (bit in 0 until 8) {
                    if (i >= src.size) break
                    if (control and (1 shl bit) != 0) {
                        if (i + 1 >= src.size) { i = src.size; break }
                        val word = ((src[i].toInt() and 0xFF) shl 8) or (src[i + 1].toInt() and 0xFF); i += 2
                        var off = word ushr 4; val len = (word and 0xF) + 2
                        if (off == w) return out.toByteArray()
                        for (k in 0 until len) { val c = dict[off]; off = (off + 1) and 4095; out.write(c.toInt()); dict[w] = c; w = (w + 1) and 4095 }
                    } else { val c = src[i++]; out.write(c.toInt()); dict[w] = c; w = (w + 1) and 4095 }
                }
            }
            return out.toByteArray()
        }

        private val SKIP = setOf("fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "generator", "header", "footer", "themedata", "colorschememapping", "datastore", "latentstyles", "listtable", "listoverridetable", "rsidtbl", "xmlnstbl", "mmathPr")

        /** The HTML Outlook wrapped in RTF (\fromhtml); plain RTF text becomes simple paragraphs. */
        fun html(rtf: ByteArray): String {
            val s = String(rtf, Charsets.ISO_8859_1)
            val fromHtml = s.contains("\\fromhtml")
            var charset = Regex("\\\\ansicpg(\\d+)").find(s)?.groupValues?.get(1)?.toIntOrNull()?.let { cp(it) } ?: cs("windows-1252")
            val out = StringBuilder(); val pend = ByteArrayOutputStream()
            fun flush() { if (pend.size() > 0) { out.append(String(pend.toByteArray(), charset)); pend.reset() } }
            class St(var ignore: Boolean, var tag: Boolean, var uc: Int)
            val stack = ArrayList<St>(); var st = St(false, false, 1)
            var htmlrtf = false; var i = 0; var first = false; var skipChars = 0
            fun visible() = !st.ignore && (if (fromHtml) !htmlrtf else true)
            while (i < s.length) {
                val ch = s[i]
                when {
                    ch == '{' -> { flush(); stack.add(st); st = St(st.ignore, st.tag, st.uc); first = true; i++ }
                    ch == '}' -> { flush(); if (stack.isNotEmpty()) st = stack.removeAt(stack.size - 1); first = false; i++ }
                    ch == '\\' && i + 1 < s.length -> {
                        val n = s[i + 1]
                        if (n == '\\' || n == '{' || n == '}') { if (skipChars > 0) skipChars-- else if (visible()) { flush(); out.append(n) }; i += 2; first = false }
                        else if (n == '\'') {
                            val hex = s.substring(minOf(i + 2, s.length), minOf(i + 4, s.length)).toIntOrNull(16)
                            if (hex != null) { if (skipChars > 0) skipChars-- else if (visible()) pend.write(hex) }
                            i += 4; first = false
                        }
                        else if (n == '*') { i += 2 }                                   // a destination follows: decided by its word
                        else if (n == '~') { if (visible()) { flush(); out.append('\u00A0') }; i += 2 }
                        else if (n == '\r' || n == '\n') { if (visible()) { flush(); out.append("\r\n") }; i += 2 }
                        else if (!n.isLetter()) { i += 2 }
                        else {
                            var j = i + 1
                            while (j < s.length && s[j].isLetter()) j++
                            val word = s.substring(i + 1, j)
                            var k = j
                            if (k < s.length && (s[k] == '-' || s[k].isDigit())) { k++; while (k < s.length && s[k].isDigit()) k++ }
                            val num = s.substring(j, k).toIntOrNull()
                            if (k < s.length && s[k] == ' ') k++
                            val starred = i >= 2 && s[i - 2] == '\\' && s[i - 1] == '*'
                            when (word) {
                                "htmltag" -> { flush(); if (first || starred) { st.tag = true; st.ignore = false } }
                                "mhtmltag" -> if (first || starred) st.ignore = true
                                "htmlrtf" -> { flush(); htmlrtf = num != 0 }
                                "ansicpg" -> if (num != null) charset = cp(num)
                                "uc" -> if (num != null) st.uc = num
                                "u" -> if (num != null) { if (visible()) { flush(); out.append((if (num < 0) num + 65536 else num).toChar()) }; skipChars = st.uc }
                                "par", "line" -> if (visible()) { flush(); out.append(if (fromHtml) "\r\n" else "<br>\r\n") }
                                "tab" -> if (visible()) { flush(); out.append(if (fromHtml) "\t" else "&emsp;") }
                                "lquote" -> if (visible()) { flush(); out.append('\u2018') }
                                "rquote" -> if (visible()) { flush(); out.append('\u2019') }
                                "ldblquote" -> if (visible()) { flush(); out.append('\u201C') }
                                "rdblquote" -> if (visible()) { flush(); out.append('\u201D') }
                                "bullet" -> if (visible()) { flush(); out.append('\u2022') }
                                "endash" -> if (visible()) { flush(); out.append('\u2013') }
                                "emdash" -> if (visible()) { flush(); out.append('\u2014') }
                                else -> if ((first || starred) && (starred || word in SKIP)) st.ignore = true
                            }
                            first = false; i = k
                        }
                    }
                    ch == '\r' || ch == '\n' -> i++
                    else -> {
                        if (skipChars > 0) skipChars--
                        else if (visible()) {
                            if (ch.code > 127) pend.write(ch.code)
                            else { flush(); if (fromHtml) out.append(ch) else out.append(when (ch) { '<' -> "&lt;"; '>' -> "&gt;"; '&' -> "&amp;"; else -> ch.toString() }) }
                        }
                        first = false; i++
                    }
                }
            }
            flush()
            return if (fromHtml) out.toString() else "<div style='font-family:Calibri,Segoe UI,Arial,sans-serif;font-size:15px'>$out</div>"
        }
    }

    // ------------------------------------------------------------------ .eml (new Outlook, Outlook 365, any mail program)

    private object Eml {
        fun parse(data: ByteArray): Mail {
            val m = Mail()
            part(String(data, Charsets.ISO_8859_1), m, true, 0)
            return m
        }

        private fun split(raw: String): Pair<Map<String, String>, String> {
            val cut = Regex("\r?\n\r?\n").find(raw)
            val head = if (cut != null) raw.substring(0, cut.range.first) else raw
            val body = if (cut != null) raw.substring(cut.range.last + 1) else ""
            val h = LinkedHashMap<String, String>()
            for (line in head.replace(Regex("\r?\n[ \t]+"), " ").split(Regex("\r?\n"))) {
                val c = line.indexOf(':'); if (c <= 0) continue
                val k = line.substring(0, c).trim().lowercase()
                if (!h.containsKey(k)) h[k] = line.substring(c + 1).trim()
            }
            return h to body
        }

        /** =?utf-8?B?…?= and =?utf-8?Q?…?= in headers. */
        fun words(s0: String): String {
            val s = s0.replace(Regex("(\\?=)\\s+(=\\?)"), "$1$2")
            return Regex("=\\?([^?]+)\\?([bBqQ])\\?([^?]*)\\?=").replace(s) { m ->
                try {
                    val bytes = if (m.groupValues[2].equals("b", true)) Base64.decode(m.groupValues[3], Base64.DEFAULT) else qp(m.groupValues[3].replace('_', ' '))
                    String(bytes, cs(m.groupValues[1]))
                } catch (_: Exception) { m.value }
            }.let { if (it.any { c -> c.code in 128..255 }) try { String(it.toByteArray(Charsets.ISO_8859_1), Charsets.UTF_8).takeIf { u -> !u.contains('\uFFFD') } ?: it } catch (_: Exception) { it } else it }
        }

        fun qp(s: String): ByteArray {
            val o = ByteArrayOutputStream(); var i = 0
            while (i < s.length) {
                val c = s[i]
                if (c == '=') {
                    // "=" at the end of a line joins it with the next one; "=41" is one byte.
                    if (i + 1 < s.length && (s[i + 1] == '\r' || s[i + 1] == '\n')) { i++; if (i < s.length && s[i] == '\r') i++; if (i < s.length && s[i] == '\n') i++; continue }
                    val hex = if (i + 3 <= s.length) s.substring(i + 1, i + 3).toIntOrNull(16) else null
                    if (hex != null) { o.write(hex); i += 3; continue }
                }
                o.write(c.code); i++
            }
            return o.toByteArray()
        }

        private fun param(v: String, name: String): String? {
            Regex("(?:^|;)\\s*" + Regex.escape(name) + "\\*\\s*=\\s*([^']*)'[^']*'([^;]+)", RegexOption.IGNORE_CASE).find(v)?.let { m ->
                return try { java.net.URLDecoder.decode(m.groupValues[2].trim(), m.groupValues[1].ifBlank { "UTF-8" }) } catch (_: Exception) { m.groupValues[2] }
            }
            return Regex("(?:^|;)\\s*" + Regex.escape(name) + "\\s*=\\s*(\"([^\"]*)\"|[^;\\s]+)", RegexOption.IGNORE_CASE).find(v)?.let { it.groupValues[2].ifEmpty { it.groupValues[1].trim('"') } }
        }

        private fun decode(body: String, enc: String): ByteArray = when (enc.trim().lowercase()) {
            "base64" -> try { Base64.decode(body, Base64.DEFAULT) } catch (_: Exception) { ByteArray(0) }
            "quoted-printable" -> qp(body)
            else -> body.toByteArray(Charsets.ISO_8859_1)
        }

        private fun part(raw: String, m: Mail, top: Boolean, depth: Int) {
            if (depth > 12) return
            val (h, body) = split(raw)
            if (top) {
                m.subject = words(h["subject"] ?: ""); m.from = words(h["from"] ?: ""); m.to = words(h["to"] ?: ""); m.cc = words(h["cc"] ?: "")
                h["date"]?.let { dt ->
                    for (f in listOf("EEE, d MMM yyyy HH:mm:ss Z", "d MMM yyyy HH:mm:ss Z", "EEE, d MMM yyyy HH:mm Z"))
                        try { m.date = SimpleDateFormat(f, Locale.US).parse(dt.replace(Regex("\\s*\\(.*\\)\\s*$"), ""))!!.time; break } catch (_: Exception) { }
                }
            }
            val ct = h["content-type"] ?: "text/plain"
            val type = ct.substringBefore(';').trim().lowercase()
            val disp = h["content-disposition"] ?: ""
            val name = words(param(disp, "filename") ?: param(ct, "name") ?: "")
            if (type.startsWith("multipart/")) {
                val b = param(ct, "boundary") ?: return
                val parts = body.split("--$b")
                for (p in parts.drop(1)) { if (p.startsWith("--")) break; part(p.trimStart('\r', '\n'), m, false, depth + 1) }
                return
            }
            val bytes = decode(body, h["content-transfer-encoding"] ?: "")
            val attach = disp.trim().lowercase().startsWith("attachment") || (name.isNotBlank() && !type.startsWith("text/"))
            if (type == "message/rfc822") { val n = Mail(); part(String(bytes, Charsets.ISO_8859_1), n, true, depth + 1); m.atts.add(Att((n.subject.ifBlank { name.ifBlank { "email" } }) + ".eml", null, nested = n)); return }
            if (!attach && type == "text/html" && m.html.isBlank()) { m.html = String(bytes, cs(param(ct, "charset") ?: "utf-8")); return }
            if (!attach && type == "text/plain" && m.text.isBlank()) { m.text = String(bytes, cs(param(ct, "charset") ?: "utf-8")); return }
            if (!attach && type == "text/calendar" && name.isBlank()) { m.atts.add(Att("invite.ics", bytes, type)); return }
            m.atts.add(Att(name.ifBlank { "attachment" + (if (type.contains('/')) "." + type.substringAfter('/').take(5) else "") }, bytes, type, (h["content-id"] ?: "").trim('<', '>', ' ')))
        }
    }

    // ------------------------------------------------------------------ .ics (meeting) and .vcf (contact)

    private fun lines(data: ByteArray): List<Pair<String, String>> {
        val t = String(data, Charsets.UTF_8).replace(Regex("\r?\n[ \t]"), "")
        return t.split(Regex("\r?\n")).mapNotNull { l -> val c = l.indexOf(':'); if (c <= 0) null else l.substring(0, c) to l.substring(c + 1) }
    }

    private fun un(v: String) = v.replace("\\n", "\n").replace("\\N", "\n").replace("\\,", ",").replace("\\;", ";").replace("\\\\", "\\")

    private fun icsTime(key: String, v: String): String {
        val m = Regex("(\\d{4})(\\d{2})(\\d{2})(?:T(\\d{2})(\\d{2}))?").find(v) ?: return v
        val (y, mo, d, h, mi) = m.destructured
        val tz = Regex("TZID=([^;:]+)").find(key)?.groupValues?.get(1) ?: if (v.endsWith("Z")) "UTC" else ""
        return "$d/$mo/$y" + (if (h.isNotEmpty()) " $h:$mi" else " (all day)") + (if (tz.isNotBlank()) " · $tz" else "")
    }

    private fun ics(data: ByteArray): Mail {
        val m = Mail(); val rows = StringBuilder(); var inEvent = false; val who = ArrayList<String>()
        fun row(k: String, v: String) { if (v.isNotBlank()) rows.append("<tr><td style='color:#666;padding:4px 12px 4px 0;vertical-align:top'>").append(esc(k)).append("</td><td style='padding:4px 0'>").append(esc(v).replace("\n", "<br>")).append("</td></tr>") }
        for ((k, v) in lines(data)) {
            val key = k.substringBefore(';').uppercase()
            if (key == "BEGIN" && v.uppercase() == "VEVENT") inEvent = true
            if (!inEvent) continue
            when (key) {
                "SUMMARY" -> { m.subject = un(v); row("Meeting", un(v)) }
                "DTSTART" -> row("Starts", icsTime(k, v))
                "DTEND" -> row("Ends", icsTime(k, v))
                "LOCATION" -> row("Where", un(v))
                "ORGANIZER" -> { val n = Regex("CN=\"?([^;:\"]+)").find(k)?.groupValues?.get(1) ?: v.removePrefix("mailto:").removePrefix("MAILTO:"); m.from = n; row("Organiser", n) }
                "ATTENDEE" -> who.add(Regex("CN=\"?([^;:\"]+)").find(k)?.groupValues?.get(1) ?: v.removePrefix("mailto:").removePrefix("MAILTO:"))
                "DESCRIPTION" -> row("Notes", un(v))
                "END" -> if (v.uppercase() == "VEVENT") inEvent = false
            }
        }
        if (who.isNotEmpty()) row("Invited", who.joinToString(", "))
        m.html = "<h3 style='font-family:Segoe UI,Arial,sans-serif'>📅 " + esc(m.subject.ifBlank { "Meeting" }) + "</h3><table style='font-family:Segoe UI,Arial,sans-serif;font-size:15px;border-collapse:collapse'>$rows</table>"
        return m
    }

    private fun vcf(data: ByteArray): Mail {
        val m = Mail(); val rows = StringBuilder()
        fun row(k: String, v: String) { if (v.isNotBlank()) rows.append("<tr><td style='color:#666;padding:4px 12px 4px 0'>").append(esc(k)).append("</td><td style='padding:4px 0'>").append(esc(v)).append("</td></tr>") }
        for ((k, v) in lines(data)) {
            val key = k.substringBefore(';').substringAfterLast('.').uppercase()
            val kind = Regex("TYPE=([A-Za-z,]+)", RegexOption.IGNORE_CASE).find(k)?.groupValues?.get(1)?.lowercase() ?: ""
            when (key) {
                "FN" -> m.subject = un(v)
                "ORG" -> row("Company", un(v).replace(";", " · "))
                "TITLE" -> row("Title", un(v))
                "TEL" -> row("Phone" + (if (kind.isNotBlank()) " ($kind)" else ""), v)
                "EMAIL" -> row("Email", v)
                "ADR" -> row("Address", un(v).split(';').filter { it.isNotBlank() }.joinToString(", "))
                "URL" -> row("Web", v)
                "NOTE" -> row("Note", un(v))
            }
        }
        m.html = "<h3 style='font-family:Segoe UI,Arial,sans-serif'>👤 " + esc(m.subject.ifBlank { "Contact" }) + "</h3><table style='font-family:Segoe UI,Arial,sans-serif;font-size:15px;border-collapse:collapse'>$rows</table>"
        return m
    }
}
