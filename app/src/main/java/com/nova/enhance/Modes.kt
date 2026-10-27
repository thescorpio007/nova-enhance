package com.nova.enhance

/** The enhancement modes. Each one becomes the AI's instruction for the text. */
data class Mode(val id: String, val label: String, val instruction: String)

object Modes {
    const val CUSTOM = "custom"

    val all = listOf(
        Mode("grammar", "Fix grammar",
            "Correct all grammar, spelling and punctuation mistakes. Keep the meaning, tone and language exactly as they are."),
        Mode("shorten", "Shorten",
            "Make the text shorter and more concise. Keep all key points and the original language."),
        Mode("professional", "Professional",
            "Rewrite the text in a clear, professional and formal tone suitable for business communication. Keep every fact."),
        Mode("informal", "Informal",
            "Rewrite the text in a warm, friendly and informal tone, as you would write to a colleague you know well. Keep every fact and the original language."),
        Mode("expand", "Expand",
            "Expand the text with more detail, explanation and smooth flow, without inventing facts that change its meaning."),
        Mode("translate", "Arabic ⇄ English",
            "If the text is mainly Arabic, translate it into natural, fluent English. Otherwise translate it into natural, fluent Modern Standard Arabic."),
        Mode(CUSTOM, "Custom", "")
    )

    fun byId(id: String?): Mode = all.firstOrNull { it.id == id } ?: all[0]

    const val SYSTEM =
        "You are Nova Enhance, a text enhancement assistant. You receive a piece of text and an instruction. " +
        "Reply with ONLY the improved text: no introduction, no explanation, no quotation marks, no notes. " +
        "Keep the original language unless the instruction asks for a translation. Keep line breaks and lists where sensible. " +
        "In later messages of the conversation the user may ask for changes; then reply with the full revised text only."

    /** Instructions with the user's "About me" profile added. */
    fun system(s: Store): String = SYSTEM + Profile.note(s)

    /** For the official AI apps: the profile goes into the message itself. */
    fun accountMessage(s: Store, mode: Mode, text: String): String {
        val note = Profile.note(s).trim()
        return (if (note.isNotBlank()) "$note\n\n" else "") + firstMessage(mode, s.custom, text)
    }

    // ---------------------------------------------------------------- Ask AI

    val askStyles = listOf(
        Mode("advise", "Advise",
            "Give practical, expert advice: the key points to consider, the risks, and clear next steps."),
        Mode("deeper", "Think deeper",
            "Think the question through carefully step by step before answering. Consider alternatives, trade-offs and " +
            "edge cases, then give a well-reasoned recommendation."),
        Mode("explain", "Explain",
            "Explain clearly and simply, with a short example if it helps."),
        Mode("brainstorm", "Brainstorm",
            "Brainstorm a varied list of ideas or options, each with one line on why it could work."),
        Mode("quick", "Quick answer",
            "Answer in two or three sentences only.")
    )

    fun askById(id: String?): Mode = askStyles.firstOrNull { it.id == id } ?: askStyles[0]

    const val ASK_SYSTEM =
        "You are Nova, a knowledgeable and honest assistant. Answer the user's question directly and accurately. " +
        "Reply in the language of the question. Use short paragraphs and numbered lists where they help; avoid long preambles. " +
        "If something is uncertain or depends on local rules or contract terms, say so."

    fun askSystem(s: Store): String = ASK_SYSTEM + Profile.note(s)

    fun askMessage(style: Mode, question: String): String = "Answer style: ${style.instruction}\n\nQuestion:\n$question"

    fun askAccountMessage(s: Store, style: Mode, question: String): String {
        val note = Profile.note(s).trim()
        return (if (note.isNotBlank()) "$note\n\n" else "") + askMessage(style, question)
    }

    fun firstMessage(mode: Mode, custom: String, text: String): String {
        val instr = if (mode.id == CUSTOM) custom.ifBlank { "Improve this text." } else mode.instruction
        return "Instruction: $instr\n\nText:\n$text"
    }
}
