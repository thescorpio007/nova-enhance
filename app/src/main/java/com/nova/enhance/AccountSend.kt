package com.nova.enhance

import android.content.Context
import android.content.Intent
import android.net.Uri

/**
 * "My account": hands the text to the official AI app through Android's normal sharing,
 * so the chat lands in the user's own signed-in account. No sign-in happens inside Nova Enhance.
 * If the official app isn't installed, the text is copied and the AI's website opens in the browser.
 */
object AccountSend {
    private val packages = mapOf(
        "gemini" to "com.google.android.apps.bard",
        "chatgpt" to "com.openai.chatgpt",
        "claude" to "com.anthropic.claude",
        "deepseek" to "com.deepseek.chat",
        "grok" to "ai.x.grok"
    )

    fun pkg(p: Provider): String = packages[p.id] ?: ""

    fun installed(c: Context, p: Provider): Boolean = try {
        c.packageManager.getPackageInfo(pkg(p), 0); true
    } catch (_: Exception) { false }

    /** Returns a short line saying what happened. */
    fun send(c: Context, p: Provider, text: String): String {
        Ui.copy(c, text)
        if (installed(c, p)) {
            val i = Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"
                putExtra(Intent.EXTRA_TEXT, text)
                setPackage(pkg(p))
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            try {
                c.startActivity(i)
                return "Sent to your ${p.label} app — choose the ${Db.PROJECT} project there if it isn't selected."
            } catch (_: Exception) {
                // The app doesn't accept shared text: open it; the text is already copied.
                c.packageManager.getLaunchIntentForPackage(pkg(p))?.let {
                    c.startActivity(it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    return "Opened your ${p.label} app — the text is copied: paste it there."
                }
            }
        }
        openUrl(c, p.webUrl)
        return "${p.label} app not installed — opened ${p.label} in the browser. The text is copied: paste it there."
    }

    fun openStore(c: Context, p: Provider) {
        try { c.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=${pkg(p)}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        catch (_: Exception) { openUrl(c, "https://play.google.com/store/apps/details?id=${pkg(p)}") }
    }

    fun openApp(c: Context, p: Provider) {
        val i = c.packageManager.getLaunchIntentForPackage(pkg(p))
        if (i != null) c.startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) else openStore(c, p)
    }

    private fun openUrl(c: Context, u: String) {
        try { c.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(u)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        catch (_: Exception) { Ui.toast(c, u) }
    }
}
