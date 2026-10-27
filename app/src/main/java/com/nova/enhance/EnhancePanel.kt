package com.nova.enhance

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.speech.RecognizerIntent
import android.graphics.Color
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout

/**
 * The enhance screen, top to bottom:
 *   ORIGINAL (editable)  ·  words/characters
 *   ENHANCED · <mode>                [✦ Enhance]
 *   Enhanced window: one tab per answer, the text, [Copy] Share Replace Continue
 *   Enhancement / AI / Send-through choices
 * Used by the main screen and by the quick sheet (Share, text selection, bubble, tile).
 * [onReplace] is set when the text came from another app's selection menu and can be replaced there.
 */
class EnhancePanel(
    private val act: Activity,
    private val onReplace: ((String) -> Unit)? = null
) : LinearLayout(act) {

    companion object {
        /** Longer texts are enhanced in parts so the AI's answer is never cut off. */
        const val PART_WORDS = 3000
        const val WARN_WORDS = 4000
        const val VOICE_REQ = 77

        fun confirm(c: Context, msg: String, ok: String, f: () -> Unit) {
            AlertDialog.Builder(c).setMessage(msg).setPositiveButton(ok) { _, _ -> f() }
                .setNegativeButton("Cancel", null).show()
        }

        fun words(s: String): Int = s.trim().let { if (it.isEmpty()) 0 else it.split(Regex("\\s+")).size }

        /** Split by paragraphs into parts of at most [max] words (very long paragraphs are cut by words). */
        fun split(text: String, max: Int): List<String> {
            val parts = mutableListOf<String>()
            val cur = StringBuilder()
            var n = 0
            for (para in text.split("\n")) {
                val w = words(para)
                if (w > max) {
                    if (cur.isNotBlank()) parts += cur.toString().trim()
                    cur.clear(); n = 0
                    para.trim().split(Regex("\\s+")).chunked(max).forEach { parts += it.joinToString(" ") }
                    continue
                }
                if (n + w > max) {
                    if (cur.isNotBlank()) parts += cur.toString().trim()
                    cur.clear(); n = 0
                }
                cur.append(para).append('\n'); n += w
            }
            if (cur.isNotBlank()) parts += cur.toString().trim()
            return parts.ifEmpty { listOf(text) }
        }
    }

    /** One answer in the enhanced window. */
    private inner class Tab(val p: Provider, val account: Boolean) {
        var status = if (account) "" else "Working…"
        var text: String? = null
        var error = false
        var convId = -1L
        var note = ""
        var sent = false
        val label: String
            get() = if (account) (if (sent) "✓ Sent to my ${p.label}" else "My ${p.label}") else "${p.label} · API"
    }

    private val store = Store(act)
    private val db = Db.get(act)
    val input = Ui.input(act, "Paste, type or share text here…", true, 5)
    private val customBox = Ui.input(act, "Your instruction, e.g. \"Make it friendlier\"", true, 2)
    private val modeRow = Ui.chipRow(act)
    private val aiRow = Ui.chipRow(act)
    private val routeRow = Ui.chipRow(act)
    private val countText = Ui.text(act, "", 12f, Ui.MUTED)
    private val warnText = Ui.text(act, "", 12f, 0xFFFFC857.toInt())
    private val enhancedLabel = Ui.section(act, "")
    private val tabsRow = Ui.chipRow(act)
    private val body = Ui.text(act, "", 16f, Ui.TEXT).apply { setTextIsSelectable(true) }
    private val noteText = Ui.text(act, "", 12f, Ui.MUTED)
    private val actions = Ui.row(act)
    private val savedText = Ui.text(act, "", 12f, Ui.MUTED)

    private val langSwitch = Ui.row(act)
    private val tabs = mutableListOf<Tab>()
    private var selected = 0

    init {
        orientation = VERTICAL
        val c = act

        // ---------------------------------------------------------------- original
        val head = Ui.row(c)
        head.addView(Ui.section(c, "Original"), Ui.lp(0, weight = 1f))
        head.setPadding(0, Ui.dp(c, 6), 0, 0)
        head.addView(langSwitch, Ui.lp(LayoutParams.WRAP_CONTENT, top = Ui.dp(c, 10), end = Ui.dp(c, 6)))
        head.addView(Ui.text(c, "🎤", 15f, Color.WHITE).apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 18).toFloat())
            setOnClickListener { listen() }
        }, LayoutParams(Ui.dp(c, 36), Ui.dp(c, 36)).apply { topMargin = Ui.dp(c, 10); marginEnd = Ui.dp(c, 4) })
        head.addView(Ui.text(c, "Paste", 14f, Ui.ACCENT, true).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 18), Ui.dp(c, 4), Ui.dp(c, 8))
            setOnClickListener { pasteClipboard() }
        })
        head.addView(Ui.text(c, "Clear", 14f, Ui.MUTED, true).apply {
            setPadding(Ui.dp(c, 12), Ui.dp(c, 18), 0, Ui.dp(c, 8))
            setOnClickListener { input.setText(""); tabs.clear(); drawResult() }
        })
        addView(head)
        addView(input, Ui.lp(top = Ui.dp(c, 8)))
        addView(countText, Ui.lp(top = Ui.dp(c, 4)))
        addView(warnText, Ui.lp(top = Ui.dp(c, 2)))
        input.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun afterTextChanged(s: Editable?) { updateCount() }
        })

        // ---------------------------------------------------------------- enhance button + enhanced window
        val mid = Ui.row(c).apply { setPadding(0, Ui.dp(c, 14), 0, Ui.dp(c, 2)) }
        enhancedLabel.setPadding(0, 0, 0, 0)
        mid.addView(enhancedLabel, Ui.lp(0, weight = 1f))
        mid.addView(Ui.text(c, "✦  Enhance", 16f, Color.WHITE, true).apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            setPadding(Ui.dp(c, 22), 0, Ui.dp(c, 22), 0)
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 23).toFloat())
            setOnClickListener { go() }
        }, LayoutParams(LayoutParams.WRAP_CONTENT, Ui.dp(c, 46)))
        addView(mid)

        val box = Ui.col(c, 14)
        box.background = Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat(), Ui.dp(c, 1) + 1, Ui.ACCENT)
        box.addView(tabsRow.first, Ui.lp())
        box.addView(noteText, Ui.lp(top = Ui.dp(c, 6)))
        box.addView(body, Ui.lp(top = Ui.dp(c, 8)))
        box.addView(actions, Ui.lp(top = Ui.dp(c, 12)))
        addView(box, Ui.lp(top = Ui.dp(c, 8)))
        addView(savedText, Ui.lp(top = Ui.dp(c, 6)))

        // ---------------------------------------------------------------- choices
        addView(Ui.section(c, "Enhancement"))
        addView(modeRow.first)
        customBox.setText(store.custom)
        addView(customBox, Ui.lp(top = Ui.dp(c, 10)))
        addView(Ui.section(c, "AI"))
        addView(aiRow.first)
        addView(Ui.section(c, "Send through"))
        addView(routeRow.first)

        drawModes(); drawAis(); drawRoutes(); drawLangs(); drawResult(); updateCount()
    }

    fun setText(s: String) { input.setText(s); input.setSelection(input.text.length) }

    fun pasteClipboard(): Boolean {
        val cm = act.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val t = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(act)?.toString()
        return if (!t.isNullOrBlank()) { setText(t); true } else { Ui.toast(act, "Clipboard is empty"); false }
    }

    fun refresh() { drawAis() }

    private fun updateCount() {
        val s = input.text.toString()
        val w = words(s)
        countText.text = if (s.isEmpty()) "" else "$w words · ${s.length} characters"
        if (w > WARN_WORDS) {
            val n = split(s, PART_WORDS).size
            warnText.visibility = View.VISIBLE
            warnText.text = "Long text: the API will enhance it in $n parts and join them back together. " +
                "\"My account\" sends it in one piece."
        } else warnText.visibility = View.GONE
    }

    // ------------------------------------------------------------------ choices

    private fun drawModes() {
        val row = modeRow.second; row.removeAllViews()
        for (m in Modes.all) {
            row.addView(Ui.chip(act, m.label, store.mode == m.id) {
                store.mode = m.id; drawModes()
                // Tapping a mode enhances straight away (Custom waits for the instruction).
                if (m.id != Modes.CUSTOM && input.text.isNotBlank()) go(auto = true)
            },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
        customBox.visibility = if (store.mode == Modes.CUSTOM) View.VISIBLE else View.GONE
        enhancedLabel.text = "ENHANCED · " + Modes.byId(store.mode).label.uppercase()
    }

    private fun drawAis() {
        val row = aiRow.second; row.removeAllViews()
        val opts = Providers.all.map { it.id to it.label } + ("all" to "All AIs")
        for ((id, label) in opts) {
            val p = Providers.byId(id)
            val tag = if (p != null && !store.hasKey(p) && !AccountSend.installed(act, p)) " ·" else ""
            row.addView(Ui.chip(act, label + tag, store.target == id) {
                val changed = store.target != id
                store.target = id; drawAis()
                if (changed && tabs.isNotEmpty() && input.text.isNotBlank()) go(auto = true)
            },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
    }

    private val langs = listOf("phone", "en", "ar", "fr", "de")

    private fun langName(l: String): String = when (l) {
        "phone" -> "Phone default  (" + java.util.Locale.getDefault().displayLanguage + ")"
        "en" -> "English"
        "ar" -> "العربية  (Arabic)"
        "fr" -> "Français  (French)"
        "de" -> "Deutsch  (German)"
        else -> l
    }

    private fun langTag(l: String): String = when (l) {
        "en" -> "en-US"; "ar" -> "ar-AE"; "fr" -> "fr-FR"; "de" -> "de-DE"
        else -> java.util.Locale.getDefault().toLanguageTag()
    }

    /** Speaking language: one compact drop-down "🌐 Phone ▾". */
    private fun drawLangs() {
        langSwitch.removeAllViews()
        val cur = store.voiceLang
        val short = if (cur == "phone") "Phone" else cur.uppercase()
        val pill = Ui.text(act, "🌐 $short ▾", 13f, Ui.TEXT, true).apply {
            gravity = Gravity.CENTER
            setPadding(Ui.dp(act, 10), Ui.dp(act, 6), Ui.dp(act, 10), Ui.dp(act, 6))
            background = Ui.round(Ui.CARD2, Ui.dp(act, 16).toFloat(), Ui.dp(act, 1), Ui.ACCENT)
        }
        pill.setOnClickListener {
            val names = langs.map { langName(it) }.toTypedArray()
            AlertDialog.Builder(act).setTitle("Speaking language")
                .setSingleChoiceItems(names, langs.indexOf(store.voiceLang).coerceAtLeast(0)) { d, w ->
                    store.voiceLang = langs[w]; drawLangs(); d.dismiss()
                }.show()
        }
        langSwitch.addView(pill)
    }

    /** Speak: Android's speech recognition turns it into text, then it is enhanced straight away. */
    private fun listen() {
        val tag = langTag(store.voiceLang)
        val i = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE, tag)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, tag)
            putExtra(RecognizerIntent.EXTRA_PROMPT, "Speak now")
        }
        try { act.startActivityForResult(i, VOICE_REQ) }
        catch (_: Exception) { Ui.toast(act, "Voice typing isn't available — install or enable the Google app") }
    }

    /** Called by the screen when speech recognition returns. */
    fun onVoice(data: Intent?) {
        val said = data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()
        if (said.isNullOrBlank()) return
        setText(said)
        go()
    }

    private fun drawRoutes() {
        val row = routeRow.second; row.removeAllViews()
        val opts = listOf("api" to "API key", "account" to "My account", "both" to "Both")
        for ((id, label) in opts) {
            row.addView(Ui.chip(act, label, store.route == id) { store.route = id; drawRoutes() },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
    }

    // ------------------------------------------------------------------ enhanced window

    private fun drawResult() {
        val c = act
        tabsRow.second.removeAllViews(); actions.removeAllViews()
        if (tabs.isEmpty()) {
            tabsRow.first.visibility = View.GONE
            noteText.visibility = View.GONE
            body.setTextColor(Ui.MUTED); body.text = "Your enhanced text will appear here."
            savedText.text = ""
            return
        }
        if (selected !in tabs.indices) selected = 0
        tabsRow.first.visibility = if (tabs.size > 1) View.VISIBLE else View.GONE
        tabs.forEachIndexed { i, t ->
            tabsRow.second.addView(Ui.chip(c, t.label, i == selected) { selected = i; drawResult() },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        }
        val t = tabs[selected]
        val single = tabs.size == 1
        val noteLine = if (single) listOf(t.label, t.note).filter { it.isNotBlank() }.joinToString("  ·  ") else t.note
        noteText.visibility = if (noteLine.isNotBlank()) View.VISIBLE else View.GONE
        noteText.text = noteLine
        noteText.setTextColor(if (single) Ui.ACCENT else Ui.MUTED)

        val name = t.p.label
        val out = t.text
        when {
            t.account -> {
                body.setTextColor(Ui.MUTED)
                body.text = if (t.sent) t.status.ifBlank { "Sent to your $name account." } else "Not sent yet."
                actions.addView(Ui.button(c, if (t.sent) "Send again to $name" else "Send to my $name", !t.sent) {
                    sendAccount(t)
                }, Ui.lp(0, weight = 1f))
            }
            out != null -> {
                body.setTextColor(Ui.TEXT); body.text = out
                actions.addView(Ui.button(c, "⧉  Copy", true) { Ui.copy(c, out) }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
                actions.addView(Ui.button(c, "Share") { share(out) }, Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
                val replace = onReplace
                if (replace != null) actions.addView(Ui.button(c, "Replace") { replace(out) },
                    Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
                actions.addView(Ui.button(c, "Continue") { ChatActivity.open(c, t.convId) }, Ui.lp(LayoutParams.WRAP_CONTENT))
            }
            else -> {
                body.setTextColor(if (t.error) Ui.BAD else Ui.MUTED); body.text = t.status
            }
        }
        val saved = tabs.filter { it.convId >= 0 }.map { it.p.label }.distinct()
        savedText.text = if (saved.isEmpty()) "" else "Saved in ${Db.PROJECT} · " + saved.joinToString(", ")
    }

    // ------------------------------------------------------------------ run

    /** Enhance now with the last mode (used by the text-selection menu). */
    fun runNow() { if (input.text.isNotBlank()) go(auto = true) }

    private fun go(auto: Boolean = false) {
        val text = input.text.toString().trim()
        if (text.isEmpty()) { Ui.toast(act, "Add some text first"); return }
        store.custom = customBox.text.toString()
        val mode = Modes.byId(store.mode)
        val targets = if (store.target == "all") Providers.all else listOfNotNull(Providers.byId(store.target))
        val route = store.route
        tabs.clear(); selected = 0
        hideKeyboard()

        val wantApi = route == "api" || route == "both"
        val wantAccount = route == "account" || route == "both"
        val apiTargets = if (wantApi) targets.filter { store.hasKey(it) } else emptyList()
        if (wantApi && apiTargets.isEmpty() && !wantAccount) {
            drawResult(); missingKeys(targets); return
        }
        for (p in apiTargets) tabs += Tab(p, false)
        val accountTabs = if (wantAccount) targets.map { Tab(it, true) } else emptyList()
        tabs += accountTabs
        if (wantApi && apiTargets.isEmpty()) Ui.toast(act, "No API key yet — sending to your account only")
        drawResult()

        for (t in tabs.filter { !it.account }) runApi(t, mode, text)
        // One account page can open at a time: open it straight away for a single AI.
        // Quick re-runs from tapping a mode or an AI don't jump to the account page; its tab has a Send button.
        if (accountTabs.size == 1 && !(auto && apiTargets.isNotEmpty())) sendAccount(accountTabs[0])
    }

    private fun sendAccount(t: Tab) {
        val original = input.text.toString().trim()
        val msg = Modes.firstMessage(Modes.byId(store.mode), store.custom, original)
        if (!t.sent) {
            val id = db.newConv(t.p.id, store.mode, original, "account")
            db.addMsg(id, "user", msg)
            t.convId = id; t.sent = true
        }
        t.status = AccountSend.send(act, t.p, msg)
        drawResult()
    }

    private fun missingKeys(ps: List<Provider>) {
        AlertDialog.Builder(act)
            .setMessage("No API key yet for: " + ps.joinToString { it.label } +
                ".\nAdd one in Settings, or choose \"My account\" or \"Both\".")
            .setPositiveButton("Open Settings") { _, _ -> act.startActivity(Intent(act, SettingsActivity::class.java)) }
            .setNegativeButton("OK", null).show()
    }

    private fun runApi(t: Tab, mode: Mode, original: String) {
        val p = t.p
        val key = store.key(p); val model = store.model(p); val custom = store.custom
        val parts = if (words(original) > WARN_WORDS) split(original, PART_WORDS) else listOf(original)
        val autoAccount = store.route == "api"   // with "Both" the account copy is already sent
        Thread {
            val outs = mutableListOf<String>()
            var backup: String? = null
            var error: String? = null
            var lastCode = 0
            for ((i, part) in parts.withIndex()) {
                val prefix = if (parts.size > 1) "Part ${i + 1}/${parts.size}: " else ""
                if (parts.size > 1) update { t.status = "${prefix}working…" }
                try {
                    val a = Providers.chatSmart(p, key, model, Modes.SYSTEM,
                        listOf(Msg("user", Modes.firstMessage(mode, custom, part)))) { msg ->
                        update { t.status = prefix + msg }
                    }
                    outs += a.text
                    if (a.switched) backup = a.model
                } catch (e: Exception) {
                    error = prefix + (e.message ?: e.toString())
                    lastCode = (e as? AiException)?.code ?: 0
                    break
                }
            }
            val err = error
            if (err == null) {
                val joined = outs.joinToString("\n\n")
                val id = db.newConv(p.id, mode.id, original, "api")
                db.addMsg(id, "user", Modes.firstMessage(mode, custom, original))
                db.addMsg(id, "assistant", joined)
                val notes = mutableListOf<String>()
                if (parts.size > 1) notes += "enhanced in ${parts.size} parts"
                backup?.let { notes += "answered by backup model $it (usual one was busy)" }
                update { t.text = joined; t.convId = id; t.note = notes.joinToString(" · ") }
            } else {
                ErrorLog.add(act, "Enhance", p.label, store.model(p), lastCode, err, original)
                update { t.error = true; t.status = err }
                // API failed completely: send it to the signed-in account instead (uses the Pro plan).
                if (autoAccount) act.runOnUiThread {
                    if (act.isFinishing) return@runOnUiThread
                    val acc = Tab(p, true)
                    acc.note = "API failed, so it was sent to your account instead"
                    tabs += acc; selected = tabs.size - 1
                    sendAccount(acc)
                }
            }
        }.start()
    }

    private fun update(f: () -> Unit) {
        act.runOnUiThread {
            f()
            if (!act.isFinishing) drawResult()
        }
    }

    private fun share(s: String) {
        act.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"; putExtra(Intent.EXTRA_TEXT, s)
        }, "Share enhanced text"))
    }

    private fun hideKeyboard() {
        val imm = act.getSystemService(Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager
        imm.hideSoftInputFromWindow(input.windowToken, 0)
    }
}
