package com.nova.enhance

import android.graphics.Typeface
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.BackgroundColorSpan
import android.text.style.ForegroundColorSpan
import android.text.style.StrikethroughSpan
import android.text.style.StyleSpan

/** "Show changes": words the AI added are green, words it removed are red and struck through. */
object Diff {
    private const val MAX_TOKENS = 2500

    private fun tokens(s: String): List<String> = Regex("\\s+|[^\\s]+").findAll(s).map { it.value }.toList()

    fun spans(before: String, after: String): CharSequence {
        val a = tokens(before); val b = tokens(after)
        if (a.size > MAX_TOKENS || b.size > MAX_TOKENS) return after
        // Longest common subsequence on words.
        val n = a.size; val m = b.size
        val dp = Array(n + 1) { IntArray(m + 1) }
        for (i in n - 1 downTo 0) for (j in m - 1 downTo 0)
            dp[i][j] = if (a[i] == b[j]) dp[i + 1][j + 1] + 1 else maxOf(dp[i + 1][j], dp[i][j + 1])
        val out = SpannableStringBuilder()
        fun add(t: String, kind: Int) {
            val start = out.length
            out.append(t)
            if (t.isBlank()) return
            when (kind) {
                1 -> {
                    out.setSpan(ForegroundColorSpan(0xFF3DDC97.toInt()), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    out.setSpan(BackgroundColorSpan(0x223DDC97), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    out.setSpan(StyleSpan(Typeface.BOLD), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                }
                -1 -> {
                    out.setSpan(ForegroundColorSpan(0xFFFF6B6B.toInt()), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    out.setSpan(StrikethroughSpan(), start, out.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                }
            }
        }
        var i = 0; var j = 0
        while (i < n && j < m) {
            when {
                a[i] == b[j] -> { add(b[j], 0); i++; j++ }
                dp[i + 1][j] >= dp[i][j + 1] -> { if (a[i].isNotBlank()) { add(a[i], -1); out.append(' ') }; i++ }
                else -> { add(b[j], 1); j++ }
            }
        }
        while (i < n) { if (a[i].isNotBlank()) { add(a[i], -1); out.append(' ') }; i++ }
        while (j < m) { add(b[j], 1); j++ }
        return out
    }
}
