package com.nova.enhance

/**
 * 🎯 Guided mode on the phone (the same as Nova on the PC): a strict scope and a time limit, always numbered options.
 * Ask AI starts with a numbered menu, every answer ends with "▶ Options" (tap a number, or type it in Follow up),
 * a question outside the scope gets options instead of an answer, and when the time is up: +10 min, a summary, or end.
 * The default time is 15 min (Settings → 🎯 Guided mode). Designed by A.HAMROUNI
 */
object Guided {
    const val HEAD = "▶ Options"
    val CHOICES = listOf(5, 10, 15, 30, 60, 0)
    fun minLabel(m: Int) = if (m <= 0) "no limit" else "$m min"

    val MENU = listOf(
        "Find a file, a drawing or a document in my projects",
        "Find a clause inside a contract or a specification",
        "Check a submittal against the specification",
        "Prepare a reply or a letter",
        "Summarise a project's open items")

    val WRAP = listOf("+10 min — keep going", "Summarise what we settled and the next steps", "End here")
    const val SUMMARY = "Time is up. Summarise in 5 lines max what we settled (facts, decisions, files) and the 3 next steps — no new analysis."

    fun rule(scope: String) =
        "\n🎯 GUIDED MODE: help the user make progress inside a strict scope — $scope. Answer ONLY the step the user picked or asked, short and concrete. " +
        "If the question is outside this scope, do not answer it: say in one line that it is outside the scope, then give options that bring the work back into the scope. " +
        "If the question is only a greeting or is unclear, give only the options. " +
        "ALWAYS end your answer with a line exactly \"$HEAD\" followed by 3 to 5 numbered next steps, one per line, like \"1. Check the fire rating against the specification\" — each one short (max 12 words), specific and inside the scope."

    private val OPT = Regex("""^\s*(?:\*\*)?(\d{1,2})[.)]\s*(?:\*\*)?\s*(.+?)\s*$""")
    private fun isHead(l: String) = l.contains(HEAD) || Regex("""^(\*\*)?▶?\s*Options\s*:?\s*(\*\*)?$""", RegexOption.IGNORE_CASE).matches(l.trim())

    fun options(text: String?): List<String> {
        val lines = (text ?: "").replace("\r\n", "\n").split("\n")
        val at = lines.indexOfLast { isHead(it) }
        if (at < 0) return emptyList()
        val res = mutableListOf<String>()
        for (i in at + 1 until lines.size) {
            val m = OPT.find(lines[i])
            if (m != null) res += m.groupValues[2].trimEnd('*').trim()
            else if (lines[i].isNotBlank() && res.isNotEmpty()) break
        }
        return res.take(8)
    }

    /** The answer without its options (for Copy and Share). */
    fun strip(text: String?): String {
        val lines = (text ?: "").replace("\r\n", "\n").split("\n").toMutableList()
        val at = lines.indexOfLast { it.contains(HEAD) }
        if (at < 0) return text ?: ""
        var end = at + 1
        while (end < lines.size && (OPT.matches(lines[end]) || lines[end].isBlank())) end++
        repeat(end - at) { lines.removeAt(at) }
        return lines.joinToString("\n").trimEnd()
    }

    /** Typing a bare number ("2") picks that option of the last answer. */
    fun pick(typed: String, last: String?): String {
        val m = Regex("""^\s*(?:option\s*)?#?(\d{1,2})\s*\.?\s*$""", RegexOption.IGNORE_CASE).find(typed) ?: return typed
        val o = options(last); val n = m.groupValues[1].toInt()
        return if (n in 1..o.size) o[n - 1] else typed
    }
}
