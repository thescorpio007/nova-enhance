package com.nova.enhance

import android.content.Context
import android.os.Build
import android.service.textservice.SpellCheckerService
import android.view.textservice.SentenceSuggestionsInfo
import android.view.textservice.SuggestionsInfo
import android.view.textservice.TextInfo
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * ✍ Nova as the phone's spell checker (Android Settings → Languages → Spell checker → Nova Enhance):
 * 🔴 spelling on the phone (English word list) · 🔵 grammar by your AI when a sentence is finished (Android 12+),
 * and your words from the PC (codes, names, project vocabulary) are never marked. Designed by A.HAMROUNI
 */
class NovaSpell : SpellCheckerService() {
    override fun createSession(): SpellCheckerService.Session = S(applicationContext)

    private class S(val ctx: Context) : SpellCheckerService.Session() {
        override fun onCreate() { Words.load(ctx) }

        override fun onGetSuggestions(ti: TextInfo, n: Int): SuggestionsInfo = word(ti.text ?: "", n, ti.cookie, ti.sequence)

        private fun word(w: String, n: Int, cookie: Int = 0, seq: Int = 0): SuggestionsInfo =
            if (Words.ok(ctx, w)) SuggestionsInfo(SuggestionsInfo.RESULT_ATTR_IN_THE_DICTIONARY, emptyArray(), cookie, seq)
            else SuggestionsInfo(SuggestionsInfo.RESULT_ATTR_LOOKS_LIKE_TYPO or SuggestionsInfo.RESULT_ATTR_HAS_RECOMMENDED_SUGGESTIONS,
                Words.suggest(w, n.coerceIn(1, 5)).toTypedArray(), cookie, seq)

        override fun onGetSentenceSuggestionsMultiple(tis: Array<TextInfo>, n: Int): Array<SentenceSuggestionsInfo> =
            tis.map { ti ->
                val text = ti.text ?: ""
                val infos = ArrayList<SuggestionsInfo>(); val offs = ArrayList<Int>(); val lens = ArrayList<Int>()
                // 🔴 words
                for (m in Regex("[\\p{L}][\\p{L}'’]*").findAll(text)) {
                    val r = word(m.value, n, ti.cookie, ti.sequence)
                    if (r.suggestionsAttributes and SuggestionsInfo.RESULT_ATTR_LOOKS_LIKE_TYPO != 0) { infos.add(r); offs.add(m.range.first); lens.add(m.value.length) }
                }
                // 🔵 grammar (your AI, once per finished sentence; shown the next time the text is checked)
                if (Build.VERSION.SDK_INT >= 31) for ((wrong, right) in Grammar.fixes(ctx, text)) {
                    val i = text.indexOf(wrong); if (i < 0) continue
                    infos.add(SuggestionsInfo(SuggestionsInfo.RESULT_ATTR_LOOKS_LIKE_GRAMMAR_ERROR or SuggestionsInfo.RESULT_ATTR_HAS_RECOMMENDED_SUGGESTIONS,
                        arrayOf(right), ti.cookie, ti.sequence)); offs.add(i); lens.add(wrong.length)
                }
                SentenceSuggestionsInfo(infos.toTypedArray(), offs.toIntArray(), lens.toIntArray())
            }.toTypedArray()
    }
}

/** The English word list (built into the app) + your words from the PC and the ones you add on the phone. */
object Words {
    @Volatile private var data: ByteArray = ByteArray(0)
    @Volatile private var offs: IntArray = IntArray(0)
    @Volatile private var mine: Set<String> = emptySet()
    @Volatile private var mineAt = 0L

    fun load(c: Context) {
        if (offs.isNotEmpty()) return
        synchronized(this) {
            if (offs.isNotEmpty()) return
            try {
                val b = c.assets.open("words.txt").use { it.readBytes() }
                val o = ArrayList<Int>(400_000); var start = 0
                for (i in b.indices) if (b[i] == '\n'.code.toByte()) { if (i > start) o.add(start); start = i + 1 }
                if (start < b.size) o.add(start)
                data = b; offs = o.toIntArray()
            } catch (_: Exception) { }
        }
    }

    private fun cmp(i: Int, w: ByteArray): Int {
        var p = offs[i]; var k = 0
        while (true) {
            val a = if (p < data.size && data[p] != '\n'.code.toByte() && data[p] != '\r'.code.toByte()) data[p].toInt() and 0xFF else -1
            val b = if (k < w.size) w[k].toInt() and 0xFF else -1
            if (a != b) return a - b
            if (a == -1) return 0
            p++; k++
        }
    }

    fun inList(w: String): Boolean {
        if (offs.isEmpty()) return true
        val b = w.lowercase().toByteArray()
        var lo = 0; var hi = offs.size - 1
        while (lo <= hi) { val m = (lo + hi) ushr 1; val c = cmp(m, b); if (c == 0) return true; if (c < 0) lo = m + 1 else hi = m - 1 }
        return false
    }

    private fun myWords(c: Context): Set<String> {
        val now = System.currentTimeMillis()
        if (now - mineAt > 60_000) {
            val added = c.getSharedPreferences("nova_enhance", Context.MODE_PRIVATE).getString("my_words", "").orEmpty()
                .split(Regex("[\\s,;]+")).filter { it.isNotBlank() }.map { it.lowercase() }
            mine = PcLink.myWords(c) + added; mineAt = now
        }
        return mine
    }

    /** Not marked: short words, codes and numbers, CAPITALS, Names, non-English letters, contractions, and your words. */
    fun ok(c: Context, w0: String): Boolean {
        val w = w0.trim('\'', '’')
        if (w.length < 3 || w.any { it.isDigit() || it == '-' || it == '_' || it == '\'' || it == '’' }) return true
        if (w.any { it.code > 0x24F }) return true              // Arabic and other scripts: not checked here
        if (w.any { it in 'À'..'ɏ' }) return true               // accented (French…) words
        if (w[0].isUpperCase()) return true                     // names, codes, sentence starts
        val low = w.lowercase()
        if (low in myWords(c)) return true
        if (inList(low)) return true
        // plurals / simple endings of known words
        for (suf in listOf("s", "es", "ed", "ing", "ly", "er", "ers")) if (low.endsWith(suf) && low.length > suf.length + 2 && inList(low.dropLast(suf.length))) return true
        return false
    }

    /** Up to n corrections one or two letters away. */
    fun suggest(w: String, n: Int): List<String> {
        val low = w.lowercase(); val abc = "abcdefghijklmnopqrstuvwxyz"
        fun edits(s: String): Sequence<String> = sequence {
            for (i in 0..s.length) {
                if (i < s.length) yield(s.removeRange(i, i + 1))
                if (i < s.length - 1) yield(s.substring(0, i) + s[i + 1] + s[i] + s.substring(i + 2))
                for (ch in abc) { if (i < s.length) yield(s.substring(0, i) + ch + s.substring(i + 1)); yield(s.substring(0, i) + ch + s.substring(i)) }
            }
        }
        val out = LinkedHashSet<String>()
        for (e in edits(low)) { if (e.length > 1 && inList(e)) out.add(e); if (out.size >= n) break }
        if (out.size < n && low.length <= 12) for (e1 in edits(low).distinct().take(600)) {
            for (e2 in edits(e1)) { if (e2.length > 2 && inList(e2)) out.add(e2); if (out.size >= n) break }
            if (out.size >= n) break
        }
        return out.map { if (w.firstOrNull()?.isUpperCase() == true) it.replaceFirstChar { c -> c.uppercase() } else it }
    }
}

/** 🔵 Grammar by your AI: one call per finished sentence, remembered; only when Settings allows it. */
object Grammar {
    private val done = ConcurrentHashMap<String, List<Pair<String, String>>>()
    private val asked = ConcurrentHashMap<String, Long>()
    @Volatile private var lastCall = 0L

    fun fixes(c: Context, text: String): List<Pair<String, String>> {
        val sp = c.getSharedPreferences("nova_enhance", Context.MODE_PRIVATE)
        if (!sp.getBoolean("spell_grammar", true)) return emptyList()
        val out = ArrayList<Pair<String, String>>()
        for (m in Regex("[^.!?\\n]{12,600}[.!?]").findAll(text)) {
            val s = m.value.trim()
            if (s.split(' ').size < 4) continue
            val known = done[s]
            if (known != null) { out.addAll(known); continue }
            val now = System.currentTimeMillis()
            if ((asked[s] ?: 0) > now - 120_000 || now - lastCall < 2500) continue
            asked[s] = now; lastCall = now
            Thread { done[s] = check(c, s) }.start()
        }
        if (done.size > 400) done.clear()
        return out
    }

    private fun check(c: Context, sentence: String): List<Pair<String, String>> = try {
        val store = Store(c)
        val p = Providers.all.filter { store.hasKey(it) }.let { k -> k.firstOrNull { it.id == store.target } ?: k.firstOrNull() } ?: return emptyList()
        val sys = "You check ONE sentence for grammar mistakes only (not spelling, not style). Reply with JSON only: " +
            "{\"fix\":[{\"wrong\":\"exact words from the sentence\",\"right\":\"corrected words\"}]} — an empty list when it is correct." +
            Profile.note(store) + PcLink.brainNote(c, sentence)
        val a = Providers.chatSmart(p, store.key(p), store.model(p), sys, listOf(Msg("user", sentence)), fast = true) { }.text
        val o = JSONObject(a.substring(a.indexOf('{').coerceAtLeast(0), a.lastIndexOf('}') + 1))
        val arr = o.optJSONArray("fix")
        if (arr == null) emptyList() else (0 until arr.length()).mapNotNull { i ->
            val x = arr.getJSONObject(i); val w = x.optString("wrong"); val r = x.optString("right")
            if (w.isBlank() || r.isBlank() || w == r || !sentence.contains(w)) null else w to r
        }
    } catch (_: Exception) { emptyList() }
}
