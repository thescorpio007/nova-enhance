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
import org.json.JSONArray
import org.json.JSONObject

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
    private lateinit var origBox: LinearLayout
    private lateinit var origHead: TextView
    private lateinit var origText: TextView
    private lateinit var olderBtn: TextView
    private lateinit var origBtn: TextView
    private var origIdx = 0
    private lateinit var langBox: LinearLayout
    private lateinit var langMain: FlowLayout
    private lateinit var langMore: FlowLayout
    private var trBtn: TextView? = null

    private val modes = listOf("grammar", "shorten", "professional", "informal", "translate")

    override fun onCreateInputView(): View {
        store = Store(this)
        val c = this
        val root = Ui.col(c).apply {
            setBackgroundColor(Ui.BG)
            setPadding(Ui.dp(c, 10), Ui.dp(c, 8), Ui.dp(c, 10), Ui.dp(c, 6))
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
        head.addView(aiPill, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 28), end = Ui.dp(c, 6)))
        head.addView(pill("⚙") { openSettings() }.apply {
            setTextColor(Color.WHITE); textSize = 15f
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 14).toFloat())
        }, LinearLayout.LayoutParams(Ui.dp(c, 36), Ui.dp(c, 28)))
        root.addView(head)

        // 6 big buttons
        val labels = listOf("grammar" to "✦ Fix grammar", "shorten" to "Shorten", "professional" to "Professional",
            "informal" to "Informal", "translate" to "🌐 Translate")
        val r1 = Ui.row(c); val r2 = Ui.row(c)
        labels.forEachIndexed { i, (id, label) ->
            val b = key(label, primary = false) { enhance(id) }
            if (id == "translate") {
                trBtn = b
                // Tap = translate to the last language; long-press = pick another language.
                b.setOnLongClickListener { toggleLangs(); true }
            }
            modeBtns += id to b
            (if (i < 3) r1 else r2).addView(b, LinearLayout.LayoutParams(0, Ui.dp(c, 44), 1f).apply { marginEnd = Ui.dp(c, 8) })
        }
        voiceBtn = key("🎤 Voice") { toggleVoice() }
        r2.addView(voiceBtn, LinearLayout.LayoutParams(0, Ui.dp(c, 44), 1f))
        (r1.getChildAt(2).layoutParams as LinearLayout.LayoutParams).marginEnd = 0
        root.addView(r1, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(r2, Ui.lp(top = Ui.dp(c, 8)))

        // 🌐 Translate to: shown by long-pressing Translate
        langBox = Ui.col(c).apply {
            background = Ui.round(0xFF181C24.toInt(), Ui.dp(c, 12).toFloat(), Ui.dp(c, 1), 0x667C6CFF)
            setPadding(Ui.dp(c, 10), Ui.dp(c, 8), Ui.dp(c, 10), Ui.dp(c, 8))
            visibility = View.GONE
        }
        langBox.addView(Ui.text(c, "🌐 TRANSLATE TO", 12f, 0xFFB9B0FF.toInt(), true))
        langMain = FlowLayout(c, Ui.dp(c, 6), Ui.dp(c, 6))
        langMore = FlowLayout(c, Ui.dp(c, 6), Ui.dp(c, 6)).apply { visibility = View.GONE }
        langBox.addView(langMain, Ui.lp(top = Ui.dp(c, 6)))
        langBox.addView(langMore, Ui.lp(top = Ui.dp(c, 6)))
        root.addView(langBox, Ui.lp(top = Ui.dp(c, 8)))

        status = Ui.text(c, "Tap a mode — long-press 🌐 to change the language", 12.5f, Ui.MUTED).apply { maxLines = 1; isSingleLine = true; ellipsize = android.text.TextUtils.TruncateAt.END }
        root.addView(status, Ui.lp(top = Ui.dp(c, 8)))

        // 📄 Original box: the text as it was before the last enhancements
        origBox = Ui.col(c).apply {
            background = Ui.round(0xFF181C24.toInt(), Ui.dp(c, 12).toFloat(), Ui.dp(c, 1), 0x66FFC857)
            setPadding(Ui.dp(c, 10), Ui.dp(c, 8), Ui.dp(c, 10), Ui.dp(c, 8))
            visibility = View.GONE
        }
        origHead = Ui.text(c, "", 12f, 0xFFFFD27A.toInt(), true).apply { maxLines = 1 }
        origBox.addView(origHead)
        origText = Ui.text(c, "", 13f, 0xFFC9CED8.toInt()).apply {
            maxLines = 3; ellipsize = android.text.TextUtils.TruncateAt.END
        }
        origBox.addView(origText, Ui.lp(top = Ui.dp(c, 4)))
        val ob = Ui.row(c)
        ob.addView(chip("↩ Put back") { putBack() }, chipLp())
        ob.addView(chip("⧉ Copy") { copyOrig() }, chipLp())
        olderBtn = chip("🕘 Older") { older() }
        ob.addView(olderBtn, chipLp())
        ob.addView(View(c), LinearLayout.LayoutParams(0, 1, 1f))
        ob.addView(chip("✕") { origBox.visibility = View.GONE }.apply { background = null; setTextColor(Ui.MUTED) },
            LinearLayout.LayoutParams(Ui.dp(c, 34), Ui.dp(c, 30)))
        origBox.addView(ob, Ui.lp(top = Ui.dp(c, 8)))
        root.addView(origBox, Ui.lp(top = Ui.dp(c, 8)))

        val bot = Ui.row(c)
        bot.addView(key("↶ Undo") { undo() }, LinearLayout.LayoutParams(0, Ui.dp(c, 38), 1f).apply { marginEnd = Ui.dp(c, 8) })
        origBtn = key("📄 Original") { toggleOrig() }.apply {
            setTextColor(0xFFFFD27A.toInt()); background = Ui.round(0xFF3A3320.toInt(), Ui.dp(c, 12).toFloat())
        }
        bot.addView(origBtn, LinearLayout.LayoutParams(0, Ui.dp(c, 38), 1.15f).apply { marginEnd = Ui.dp(c, 8) })
        bot.addView(key("⇥ Select all") {
            currentInputConnection?.performContextMenuAction(android.R.id.selectAll)
        }, LinearLayout.LayoutParams(0, Ui.dp(c, 38), 1f).apply { marginEnd = Ui.dp(c, 8) })
        bot.addView(key("⌨ Back") { back() }, LinearLayout.LayoutParams(0, Ui.dp(c, 38), 1f))
        root.addView(bot, Ui.lp(top = Ui.dp(c, 8)))

        drawAi(); drawModes()
        // Keep the panel above the navigation buttons: use the phone's navigation-bar height directly,
        // because some Android versions don't report it to keyboards.
        val navH = navBarHeight()
        root.setPadding(root.paddingLeft, root.paddingTop, root.paddingRight, Ui.dp(c, 6) + navH)
        root.setOnApplyWindowInsetsListener { v, ins ->
            val reported = if (Build.VERSION.SDK_INT >= 30) ins.getInsets(android.view.WindowInsets.Type.navigationBars()).bottom
                else @Suppress("DEPRECATION") ins.systemWindowInsetBottom
            v.setPadding(v.paddingLeft, v.paddingTop, v.paddingRight, Ui.dp(c, 6) + maxOf(reported, navH))
            ins
        }
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

    private fun langChip(s: String, on: Boolean, f: () -> Unit) = chip(s) { f() }.apply {
        if (on) { setTextColor(Color.WHITE); background = Ui.round(Ui.ACCENT, Ui.dp(context, 15).toFloat()) }
    }

    private fun drawLangs() {
        val cur = store.trLang
        langMain.removeAllViews(); langMore.removeAllViews()
        val isOther = Modes.MAIN_LANGS.none { it.first.equals(cur, true) }
        for ((v, l) in Modes.MAIN_LANGS) langMain.addView(langChip(l, v.equals(cur, true)) { pickLang(v) }, chipLp0())
        val open = langMore.visibility == View.VISIBLE
        langMain.addView(langChip(if (open) "Others ▾" else if (isOther) Modes.langLabel(cur) + " ▸" else "Others ▸", isOther || open) {
            langMore.visibility = if (langMore.visibility == View.VISIBLE) View.GONE else View.VISIBLE; drawLangs()
        }, chipLp0())
        for ((v, l) in Modes.OTHER_LANGS) langMore.addView(langChip(l, v.equals(cur, true)) { pickLang(v) }, chipLp0())
        langMore.addView(langChip("✎ Type any", false) {
            try {
                langBox.visibility = View.GONE
                back()   // your normal keyboard, so you can type the language name
                startActivity(android.content.Intent(this, LangActivity::class.java).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
            } catch (_: Exception) { say("Couldn't open — set it in the app", Ui.BAD) }
        }, chipLp0())
    }

    private fun chipLp0() = android.view.ViewGroup.LayoutParams(android.view.ViewGroup.LayoutParams.WRAP_CONTENT, Ui.dp(this, 32))

    private fun toggleLangs() {
        if (langBox.visibility == View.VISIBLE) { langBox.visibility = View.GONE; drawModes(); return }
        langMore.visibility = if (Modes.MAIN_LANGS.none { it.first.equals(store.trLang, true) }) View.VISIBLE else View.GONE
        drawLangs(); langBox.visibility = View.VISIBLE
        say("Tap a language — translated in place")
    }

    private fun pickLang(v: String) {
        store.trLang = v
        langBox.visibility = View.GONE
        drawModes()
        enhance("translate")
    }

    private fun drawModes() {
        trBtn?.text = "🌐 → " + Modes.langLabel(store.trLang)
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
                    saveOriginal(text, out, whole, Modes.label(mode))
                    put(out, whole)
                    val db = Db.get(this)
                    val id = db.newConv(p.id, modeId, text, "api")
                    db.addMsg(id, "user", Modes.firstMessage(mode, store.custom, text)); db.addMsg(id, "assistant", out)
                    val fixed = Diff.countChanged(text, out)
                    sub.text = String.format("Done in %.1f s", (System.currentTimeMillis() - t0) / 1000.0)
                    say("✓ ${Modes.label(mode)} applied" + (if (modeId == "grammar") " · $fixed word${if (fixed == 1) "" else "s"} fixed" else "") +
                        " · original saved", Ui.GOOD)
                    if (origBox.visibility == View.VISIBLE) { origIdx = 0; drawOrig() }
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

    // ------------------------------------------------------------------ ⚙ settings

    private fun openSettings() {
        try {
            startActivity(android.content.Intent(this, SettingsActivity::class.java)
                .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK or android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP))
        } catch (e: Exception) { say("Couldn't open Settings — open the Nova Enhance app", Ui.BAD) }
    }

    // ------------------------------------------------------------------ 📄 original text (last 20)

    private fun chip(s: String, f: () -> Unit) = Ui.text(this, s, 12.5f, Ui.TEXT, true).apply {
        gravity = Gravity.CENTER; includeFontPadding = false
        setPadding(Ui.dp(context, 10), 0, Ui.dp(context, 10), 0)
        background = Ui.round(0xFF2A3040.toInt(), Ui.dp(context, 15).toFloat())
        setOnClickListener { f() }
    }

    private fun chipLp() = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(this, 30)).apply { marginEnd = Ui.dp(this@NovaKeyboard, 6) }

    private fun prefs() = getSharedPreferences("nova_enhance", MODE_PRIVATE)

    private fun originals(): JSONArray = try { JSONArray(prefs().getString("kb_originals", "[]")) } catch (_: Exception) { JSONArray() }

    private fun saveOriginal(text: String, out: String, whole: Boolean, mode: String) {
        val old = originals()
        val now = JSONArray().put(JSONObject().put("t", text).put("o", out).put("w", whole)
            .put("m", mode).put("at", System.currentTimeMillis()))
        for (i in 0 until minOf(old.length(), 19)) now.put(old.get(i))
        prefs().edit().putString("kb_originals", now.toString()).apply()
    }

    private fun toggleOrig() {
        if (origBox.visibility == View.VISIBLE) { origBox.visibility = View.GONE; return }
        if (originals().length() == 0) { say("No original saved yet — tap a mode first"); return }
        origIdx = 0; drawOrig(); origBox.visibility = View.VISIBLE
    }

    private fun drawOrig() {
        val all = originals()
        if (all.length() == 0) { origBox.visibility = View.GONE; return }
        origIdx = origIdx.coerceIn(0, all.length() - 1)
        val e = all.getJSONObject(origIdx)
        val time = java.text.DateFormat.getTimeInstance(java.text.DateFormat.SHORT).format(java.util.Date(e.optLong("at")))
        origHead.text = "📄 ORIGINAL · before ${e.optString("m")}" +
            (if (origIdx > 0) " · ${origIdx + 1} of ${all.length()}" else "") + "  ·  $time"
        origText.text = e.optString("t")
        val left = all.length() - 1 - origIdx
        olderBtn.text = if (left > 0) "🕘 Older ($left)" else "🕘 Newest"
    }

    private fun older() {
        val n = originals().length()
        origIdx = if (origIdx + 1 < n) origIdx + 1 else 0
        drawOrig()
    }

    private fun putBack() {
        val all = originals(); if (all.length() == 0) return
        val e = all.getJSONObject(origIdx.coerceIn(0, all.length() - 1))
        val t = e.optString("t"); val o = e.optString("o")
        val ic = currentInputConnection ?: return
        val sel = ic.getSelectedText(0)?.toString()
        when {
            !sel.isNullOrEmpty() -> ic.commitText(t, 1)
            e.optBoolean("w") -> put(t, true)
            o.isNotEmpty() && (ic.getTextBeforeCursor(o.length, 0)?.toString() ?: "") == o -> {
                ic.beginBatchEdit(); ic.deleteSurroundingText(o.length, 0); ic.commitText(t, 1); ic.endBatchEdit()
            }
            else -> ic.commitText(t, 1)
        }
        undoText = null
        say("↩ Original put back", Ui.GOOD)
    }

    private fun copyOrig() {
        val all = originals(); if (all.length() == 0) return
        val t = all.getJSONObject(origIdx.coerceIn(0, all.length() - 1)).optString("t")
        (getSystemService(CLIPBOARD_SERVICE) as android.content.ClipboardManager)
            .setPrimaryClip(android.content.ClipData.newPlainText("Original", t))
        say("⧉ Original copied", Ui.GOOD)
    }

    /** Height of the navigation bar (◀ ● ■), or 0 with gesture navigation. */
    @android.annotation.SuppressLint("DiscouragedApi", "InternalInsetResource")
    private fun navBarHeight(): Int {
        if (Build.VERSION.SDK_INT < 35) return 0          // older Android already keeps keyboards above it
        val id = resources.getIdentifier("navigation_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else Ui.dp(this, 48)
    }
}
