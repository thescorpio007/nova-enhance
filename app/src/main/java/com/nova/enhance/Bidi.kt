package com.nova.enhance

/**
 * ¶→ / ←¶ Arabic and English direction for any text (the keyboard, Nova's text boxes, Word paragraphs).
 * A keyboard cannot change another app's paragraph settings, so the direction is written with invisible Unicode marks that
 * WhatsApp, Outlook, Gmail and Teams respect: a right-to-left mark at the start of an Arabic line, a left-to-right mark at the
 * start of an English one, and English words / numbers / dates / references inside an Arabic line held together in an
 * “isolate” so they stay in place. Copy and send stay normal. Designed by A.HAMROUNI
 */
object Bidi {
    const val LRM = '‎'; const val RLM = '‏'
    const val LRI = '⁦'; const val RLI = '⁧'; const val PDI = '⁩'
    private val MARKS = Regex("[‎‏‪-‮⁦-⁩]")
    /** A run of left-to-right words inside Arabic: words, numbers, dates, references (SLV-ZUC-AR-SDS-00557_Rev03), emails. */
    private val LTR_RUN = Regex("[A-Za-z0-9][A-Za-z0-9 _.,:/@#&()+\\-'\"%]*[A-Za-z0-9)%\"]|[A-Za-z0-9]")
    private val RTL_RUN = Regex("[؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿][؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿\\s0-9.,،؛:\\-]*[؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿]|[؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿]")

    fun isRtl(c: Char) = c in '֐'..'ࣿ' || c in 'יִ'..'﷿' || c in 'ﹰ'..'﻿'
    fun isLtr(c: Char) = c.isLetter() && !isRtl(c)

    /** 'R', 'L' or null (no letters) — the first strong letter decides, as Word and Android do. */
    fun firstStrong(s: CharSequence): Char? {
        for (c in s) { if (isRtl(c)) return 'R'; if (isLtr(c)) return 'L' }
        return null
    }

    fun hasArabic(s: CharSequence) = s.any { isRtl(it) }

    fun strip(s: String): String = s.replace(MARKS, "")

    /** One line in a direction: 'R' = right-to-left with English / numbers held in place; 'L' = left-to-right with Arabic held. */
    fun line(raw: String, dir: Char?): String {
        val s = strip(raw)
        if (s.isBlank()) return s
        val d = dir ?: firstStrong(s) ?: return s
        return if (d == 'R') RLM + (if (hasArabic(s)) LTR_RUN.replace(s) { LRI + it.value + PDI } else s)
        else LRM + RTL_RUN.replace(s) { RLI + it.value + PDI }
    }

    /** ⇄ Every line in its own direction (or all lines in [dir]). The number of lines never changes. */
    fun fix(text: String, dir: Char? = null): String {
        if (!hasArabic(text) && dir == null) return strip(text)   // English only: nothing to fix, old marks removed
        return text.split('\n').joinToString("\n") { line(it, dir) }
    }

    /** The lines touching [start]..[end] set to [dir]; returns the new text and the new cursor end. */
    fun setLines(text: String, start: Int, end: Int, dir: Char): Pair<String, Int> {
        val a = text.lastIndexOf('\n', (start - 1).coerceAtLeast(0)).let { if (it < 0 || start == 0) 0 else it + 1 }
        val bIdx = text.indexOf('\n', end.coerceAtMost(text.length)).let { if (it < 0) text.length else it }
        val mid = text.substring(a, bIdx).split('\n').joinToString("\n") { line(it, dir) }
        return text.substring(0, a) + mid + text.substring(bIdx) to a + mid.length
    }
}
