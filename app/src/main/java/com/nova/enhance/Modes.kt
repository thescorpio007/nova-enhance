package com.nova.enhance

/** The enhancement modes. Each one becomes the AI's instruction for the text. */
data class Mode(val id: String, val label: String, val instruction: String)

object Modes {
    const val CUSTOM = "custom"

    val all = listOf(
        Mode("grammar", "Fix grammar",
            "Correct all grammar, spelling and punctuation mistakes. Keep the meaning, tone and language exactly as they are."),
        Mode("professional", "Professional",
            "Rewrite the text in a clear, professional and formal tone suitable for business communication. Keep every fact."),
        Mode("shorten", "Shorten",
            "Make the text shorter and more concise. Keep all key points and the original language."),
        Mode("expand", "Expand",
            "Expand the text with more detail, explanation and smooth flow, without inventing facts that change its meaning."),
        Mode("translate", "Arabic ⇄ English",
            "If the text is mainly Arabic, translate it into natural, fluent English. Otherwise translate it into natural, fluent Modern Standard Arabic."),
        Mode("architect", "Architect report",
            "Rewrite the text in the tone of a professional architectural Design Development report or project description: precise, technical, well structured, using correct architecture, structural and MEP coordination terminology."),
        Mode(CUSTOM, "Custom", "")
    )

    fun byId(id: String?): Mode = all.firstOrNull { it.id == id } ?: all[0]

    const val SYSTEM =
        "You are Nova Enhance, a text enhancement assistant. You receive a piece of text and an instruction. " +
        "Reply with ONLY the improved text: no introduction, no explanation, no quotation marks, no notes. " +
        "Keep the original language unless the instruction asks for a translation. Keep line breaks and lists where sensible. " +
        "In later messages of the conversation the user may ask for changes; then reply with the full revised text only."

    fun firstMessage(mode: Mode, custom: String, text: String): String {
        val instr = if (mode.id == CUSTOM) custom.ifBlank { "Improve this text." } else mode.instruction
        return "Instruction: $instr\n\nText:\n$text"
    }
}
