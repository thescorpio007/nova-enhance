package com.nova.enhance

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Color
import android.inputmethodservice.InputMethodService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.inputmethod.ExtractedTextRequest
import android.view.inputmethod.InputMethodManager
import android.widget.LinearLayout
import android.widget.TextView

/**
 * Nova keyboard panel: switch to it with the 🌐 key in any app. One tap enhances the selected text,
 * or the whole message, and replaces it in place. ↶ Undo restores it; ⌨ goes back to your keyboard.
 */
class NovaKeyboard : InputMethodService() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var store: Store
    private lateinit var status: TextView
    private lateinit var sub: TextView
    private lateinit var aiPill: TextView
    private lateinit var voiceBtn: TextView
    private val modeBtns = mutableListOf<Pair<String, TextView>>()
    private var undoText: String? = null
    private var undoWhole = true
    private var busy = false
    private var voice: PhraseVoice? = null
    private var voiceTyped = 0

    private val modes = listOf("grammar", "shorten", "professional", "informal", "translate")

    override fun onCreateInputView(): View {
        store = Store(this)
        val c = this
        val root = Ui.col(c).apply {
            setBackgroundColor(Ui.BG)
            setPadding(Ui.dp(c, 12), Ui.dp(c, 10), Ui.dp(c, 12), Ui.dp(c, 12))
        }
        // Header: title + status, profile, AI
        val head = Ui.row(c)
        val titles = Ui.col(c)
        titles.addView(Ui.text(c, "✦ Nova Enhance", 14f, Ui.TEXT, true))
        sub = Ui.text(c, "Acts on the selected text, or the whole message", 11.5f, Ui.MUTED).apply { maxLines = 1 }
        titles.addView(sub)
        head.addView(titles, Ui.lp(0, weight = 1f, end = Ui.dp(c, 6)))
        val prof = Profile.fields(store).firstOrNull() ?: "Set up"
        head.addView(pill("👤 $prof") {}, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 28), end = Ui.dp(c, 6)))
        aiPill = pill("") { nextAi() }
        head.addView(aiPill, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 28)))
        root.addView(head)

        // 6 big buttons
        val labels = listOf("grammar" to "✦ Fix grammar", "shorten" to "Shorten", "professional" to "Professional",
            "informal" to "Informal", "translate" to "Arabic ⇄ English")
        val r1 = Ui.row(c); val r2 = Ui.row(c)
        labels.forEachIndexed { i, (id, label) ->
            val b = key(label, primary = false) { enhance(id) }
            modeBtns += id to b
            (if (i < 3) r1 else r2).addView(b, LinearLayout.LayoutParams(0, Ui.dp(c, 50), 1f).apply { marginEnd = Ui.dp(c, 8) })
        }
        voiceBtn = key("🎤 Voice") { toggleVoice() }
        r2.addView(voiceBtn, LinearLayout.LayoutParams(0, Ui.dp(c, 50), 1f))
        (r1.getChildAt(2).layoutParams as LinearLayout.LayoutParams).marginEnd = 0
        root.addView(r1, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(r2, Ui.lp(top = Ui.dp(c, 8)))

        status = Ui.text(c, "Tap a mode — the message is enhanced and replaced in place.", 12.5f, Ui.MUTED).apply { maxLines = 2 }
        root.addView(status, Ui.lp(top = Ui.dp(c, 8)))

        val bot = Ui.row(c)
        bot.addView(key("↶ Undo") { undo() }, LinearLayout.LayoutParams(0, Ui.dp(c, 44), 1f).apply { marginEnd = Ui.dp(c, 8) })
        bot.addView(key("⇥ Select all") {
            currentInputConnection?.performContextMenuAction(android.R.id.selectAll)
        }, LinearLayout.LayoutParams(0, Ui.dp(c, 44), 1f).apply { marginEnd = Ui.dp(c, 8) })
        bot.addView(key("⌨ Back to keyboard") { back() }, LinearLayout.LayoutParams(0, Ui.dp(c, 44), 1.2f))
        root.addView(bot, Ui.lp(top = Ui.dp(c, 8)))

        drawAi(); drawModes()
        return root
    }

    override fun onStartInputView(info: android.view.inputmethod.EditorInfo?, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        if (::store.isInitialized) { drawAi(); drawModes() }
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        voice?.cancel(); voice = null
        if (::voiceBtn.isInitialized) voiceBtn.text = "🎤 Voice"
        super.onFinishInputView(finishingInput)
    }

    // ------------------------------------------------------------------ views

    private fun pill(s: String, f: () -> Unit) = Ui.text(this, s, 12.5f, Ui.TEXT).apply {
        gravity = Gravity.CENTER; includeFontPadding = false
        setPadding(Ui.dp(context, 10), 0, Ui.dp(context, 10), 0)
        background = Ui.round(Ui.CARD2, Ui.dp(context, 14).toFloat())
        setOnClickListener { f() }
    }

    private fun key(s: String, primary: Boolean = false, f: () -> Unit) = Ui.text(this, s, 14f, if (primary) Color.WHITE else Ui.TEXT, true).apply {
        gravity = Gravity.CENTER; includeFontPadding = false
        background = Ui.round(if (primary) Ui.ACCENT else Ui.CARD2, Ui.dp(context, 12).toFloat())
        setOnClickListener { f() }
    }

    private fun drawModes() {
        for ((id, b) in modeBtns) {
            val on = store.mode == id
            b.setTextColor(if (on) Color.WHITE else Ui.TEXT)
            b.background = Ui.round(if (on) Ui.ACCENT else Ui.CARD2, Ui.dp(this, 12).toFloat())
        }
    }

    private fun provider(): Provider? {
        val keyed = Providers.all.filter { store.hasKey(it) }
        return keyed.firstOrNull { it.id == store.target } ?: keyed.firstOrNull()
    }

    private fun drawAi() { aiPill.text = (provider()?.label ?: "No API key") + " ▾" }

    private fun nextAi() {
        val keyed = Providers.all.filter { store.hasKey(it) }
        if (keyed.isEmpty()) { say("Add an API key in Nova Enhance ▸ Settings", Ui.BAD); return }
        val i = keyed.indexOfFirst { it.id == provider()?.id }
        store.target = keyed[(i + 1) % keyed.size].id
        drawAi()
    }

    private fun say(s: String, color: Int = Ui.MUTED) { main.post { status.text = s; status.setTextColor(color) } }

    // ------------------------------------------------------------------ text in the other app

    /** The selected text, or the whole message. Returns (text, wholeMessage). */
    private fun grab(): Pair<String, Boolean>? {
        val ic = currentInputConnection ?: return null
        val sel = ic.getSelectedText(0)?.toString()
        if (!sel.isNullOrEmpty()) return sel to false
        val ex = ic.getExtractedText(ExtractedTextRequest(), 0)?.text?.toString()
        val whole = ex ?: ((ic.getTextBeforeCursor(20000, 0)?.toString() ?: "") + (ic.getTextAfterCursor(20000, 0)?.toString() ?: ""))
        return if (whole.isBlank()) null else whole to true
    }

    private fun put(text: String, whole: Boolean) {
        val ic = currentInputConnection ?: return
        ic.beginBatchEdit()
        if (whole) {
            val len = ic.getExtractedText(ExtractedTextRequest(), 0)?.text?.length
            if (len != null) ic.setSelection(0, len) else ic.performContextMenuAction(android.R.id.selectAll)
        }
        ic.commitText(text, 1)
        ic.endBatchEdit()
    }

    // ------------------------------------------------------------------ actions

    private fun enhance(modeId: String) {
        if (busy) return
        val p = provider() ?: run { say("Add an API key in Nova Enhance ▸ Settings ▸ AI & API keys", Ui.BAD); return }
        val (text, whole) = grab() ?: run { say("Type or select some text first", Ui.BAD); return }
        store.mode = modeId; drawModes()
        val mode = Modes.byId(modeId)
        busy = true
        sub.text = "Working…"
        say("⏳ Enhancing with ${p.label}…", 0xFFB9B0FF.toInt())
        val t0 = System.currentTimeMillis()
        Thread {
            val r = runCatching {
                Providers.chatSmart(p, store.key(p), store.model(p), Modes.system(store),
                    listOf(Msg("user", Modes.firstMessage(mode, store.custom, text))), fast = modeId == "grammar") { msg -> say(msg) }.text
            }
            main.post {
                busy = false
                r.onSuccess { out ->
                    undoText = text; undoWhole = whole
                    put(out, whole)
                    val db = Db.get(this)
                    val id = db.newConv(p.id, modeId, text, "api")
                    db.addMsg(id, "user", Modes.firstMessage(mode, store.custom, text)); db.addMsg(id, "assistant", out)
                    val fixed = Diff.countChanged(text, out)
                    sub.text = String.format("Done in %.1f s", (System.currentTimeMillis() - t0) / 1000.0)
                    say("✓ ${mode.label} applied" + (if (modeId == "grammar") " · $fixed word${if (fixed == 1) "" else "s"} fixed" else "") +
                        " · tap ↶ Undo to restore", Ui.GOOD)
                }.onFailure { e ->
                    sub.text = "Acts on the selected text, or the whole message"
                    ErrorLog.add(this, "Keyboard", p.label, store.model(p), (e as? AiException)?.code ?: 0, e.message ?: "", text)
                    say(e.message ?: "Failed", Ui.BAD)
                }
            }
        }.start()
    }

    private fun undo() {
        val u = undoText ?: run { say("Nothing to undo"); return }
        val (_, whole) = grab() ?: ("" to true)
        put(u, whole || undoWhole)
        undoText = null
        say("↶ Restored your original text")
    }

    private fun toggleVoice() {
        voice?.let { if (it.active) { it.stop(); voiceBtn.text = "🎤 Voice"; say("✍ Finishing the last phrase…"); return } }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            say("Open Nova Enhance and tap 🎤 once to allow the microphone", Ui.BAD); return
        }
        if (!store.hasKey(AiVoice.gemini())) { say("Voice needs a Gemini API key (Nova Enhance ▸ Settings)", Ui.BAD); return }
        voiceTyped = 0
        val pv = PhraseVoice(this,
            onText = { t, _ ->
                // Type each new phrase into the box as it arrives.
                if (t.length > voiceTyped) {
                    val add = t.substring(voiceTyped)
                    val before = currentInputConnection?.getTextBeforeCursor(1, 0)?.toString() ?: ""
                    val sep = if (voiceTyped == 0 && before.isNotEmpty() && !before.endsWith(" ") && !before.endsWith("\n")) " " else ""
                    currentInputConnection?.commitText(sep + add, 1)
                    voiceTyped = t.length
                }
            },
            onState = { st ->
                when (st) {
                    "listening" -> say("● Listening — text appears after each phrase. Tap ■ Stop when done.", Ui.BAD)
                    "paused" -> say("⏸ Paused")
                    null -> {}
                    else -> {}
                }
            },
            onDone = { all ->
                if (all.length > voiceTyped) currentInputConnection?.commitText(all.substring(voiceTyped), 1)
                voiceBtn.text = "🎤 Voice"
                say(if (all.isBlank()) "Nothing was heard — check the microphone" else "✓ Voice typed · not enhanced yet", if (all.isBlank()) Ui.BAD else Ui.GOOD)
            })
        voice = pv
        if (pv.start()) voiceBtn.text = "■ Stop" else say("The microphone couldn't be opened", Ui.BAD)
    }

    private fun back() {
        if (Build.VERSION.SDK_INT >= 28) {
            if (!switchToPreviousInputMethod()) (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()
        } else {
            @Suppress("DEPRECATION")
            val ok = (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).switchToLastInputMethod(window.window?.attributes?.token)
            if (!ok) (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()
        }
    }
}
