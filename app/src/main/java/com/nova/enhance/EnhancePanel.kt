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
        const val MIC_REQ = 79

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
        /** Every version of the result (v1 = first answer, then each refinement or own edit). */
        val versions = mutableListOf<String>()
        var vIndex = 0
        var firstMsg = ""
        var ask = false
        var showChanges = false
        var busy = false
        val current: String? get() = versions.getOrNull(vIndex) ?: text
        val label: String
            get() = if (account) (if (sent) "✓ Sent to my ${p.label}" else "My ${p.label}") else "${p.label} · API"
    }

    private val store = Store(act)
    private val db = Db.get(act)
    val input = Ui.input(act, "Paste, type or share text here…", true, 5)
    private val customBox = Ui.input(act, "Your instruction, e.g. \"Make it friendlier\"", true, 2)
    private val modeRow = Ui.chipRow(act)
    private val trBox = Ui.col(act)
    private val trRow = FlowLayout(act, Ui.dp(act, 8), Ui.dp(act, 8))
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
    /** Editable result, the "show changes" view, version bar and refine bar. */
    private val resultBox = Ui.input(act, "", true, 6).apply {
        background = null; setPadding(0, 0, 0, 0); textSize = 16f; maxLines = 14
    }
    private val diffView = Ui.text(act, "", 16f, Ui.TEXT).apply { maxLines = 14 }
    private val verRow = Ui.row(act)
    private val refineChips = Ui.chipRow(act)
    private val refineRow = Ui.row(act)
    private val refineInput = Ui.input(act, "Tell the AI what to change…", false).apply { textSize = 14f }
    private var settingResult = false
    private val refinePanel = Ui.col(act)
    private val refineLabel = Ui.text(act, "", 11f, 0xFFB9B0FF.toInt(), true)
    private var barRefresh: () -> Unit = {}
    private val tabs = mutableListOf<Tab>()
    private var selected = 0

    /** false = Enhance text, true = Ask AI. The app always opens in Enhance text. */
    var askMode = false; private set
    private val segRow = Ui.row(act)
    private lateinit var origLabel: android.widget.TextView
    private lateinit var goBtn: android.widget.TextView
    private lateinit var choicesLabel: android.widget.TextView
    private val listenBar = Ui.row(act)
    private val listenText = Ui.text(act, "", 13f, Ui.TEXT)
    private val listenDot = android.view.View(act)
    private lateinit var pauseBtn: android.widget.TextView
    /** "✓ Cleaned by Gemini · 2 words fixed · not enhanced yet   ↶ Undo" */
    private val cleanRow = Ui.row(act)
    private val cleanText = Ui.text(act, "", 12f, Ui.GOOD)
    private var undoText: String? = null
    private var voice: VoiceInput? = null
    private var aiVoice: AiVoice? = null
    private var phrase: PhraseVoice? = null
    /** Each voice session has a number, so a late result from an old session is ignored. */
    private var voiceSession = 0

    init {
        orientation = VERTICAL
        val c = act

        // ---------------------------------------------------------------- Enhance text | Ask AI
        segRow.background = Ui.round(Ui.CARD, Ui.dp(c, 12).toFloat())
        segRow.setPadding(Ui.dp(c, 3), Ui.dp(c, 3), Ui.dp(c, 3), Ui.dp(c, 3))
        addView(segRow, Ui.lp(top = Ui.dp(c, 8)))

        // ---------------------------------------------------------------- about me bar
        val (bar, refreshBar) = Profile.bar(act) { }
        barRefresh = refreshBar
        addView(bar, Ui.lp(top = Ui.dp(c, 8)))

        // ---------------------------------------------------------------- original
        val head = Ui.row(c)
        head.addView(Ui.section(c, "Original"), Ui.lp(0, weight = 1f))
        head.setPadding(0, Ui.dp(c, 8), 0, 0)
        origLabel = head.getChildAt(0) as android.widget.TextView
        origLabel.setPadding(0, 0, 0, 0)
        head.addView(langSwitch, Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        head.addView(Ui.text(c, "🎤", 12f, Color.WHITE).apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 14).toFloat())
            setOnClickListener { listen() }
        }, LayoutParams(Ui.dp(c, 28), Ui.dp(c, 28)).apply { marginEnd = Ui.dp(c, 4) })
        head.addView(Ui.text(c, "Paste", 13f, Ui.ACCENT, true).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 4), Ui.dp(c, 4), Ui.dp(c, 4))
            setOnClickListener { pasteClipboard() }
        })
        head.addView(Ui.text(c, "Clear", 13f, Ui.MUTED, true).apply {
            setPadding(Ui.dp(c, 12), Ui.dp(c, 4), 0, Ui.dp(c, 4))
            setOnClickListener { input.setText(""); tabs.clear(); cleanRow.visibility = View.GONE; drawResult() }
        })
        addView(head)

        // ---------------------------------------------------------------- listening bar
        listenBar.background = Ui.round(0xFF3A1D2A.toInt(), Ui.dp(c, 12).toFloat(), 1, 0x66FF6B6B)
        listenBar.setPadding(Ui.dp(c, 10), Ui.dp(c, 6), Ui.dp(c, 8), Ui.dp(c, 6))
        listenDot.background = Ui.round(Ui.BAD, Ui.dp(c, 5).toFloat())
        listenBar.addView(listenDot, LayoutParams(Ui.dp(c, 10), Ui.dp(c, 10)).apply { marginEnd = Ui.dp(c, 10) })
        listenBar.addView(listenText, Ui.lp(0, weight = 1f))
        pauseBtn = Ui.text(c, "⏸ Pause", 13f, Color.WHITE, true).apply {
            gravity = Gravity.CENTER; includeFontPadding = false
            setPadding(Ui.dp(c, 11), 0, Ui.dp(c, 11), 0)
            background = Ui.round(0xFF4B5263.toInt(), Ui.dp(c, 14).toFloat())
            setOnClickListener {
                phrase?.takeIf { it.active }?.let { if (it.paused) it.resume() else it.pause() }
                voice?.takeIf { it.active }?.let { if (it.paused) it.resume() else it.pause() }
                tick.run()
            }
        }
        listenBar.addView(pauseBtn, LayoutParams(LayoutParams.WRAP_CONTENT, Ui.dp(c, 28)).apply { marginEnd = Ui.dp(c, 6) })
        listenBar.addView(Ui.text(c, "■ Stop", 13f, Color.WHITE, true).apply {
            gravity = Gravity.CENTER; includeFontPadding = false
            setPadding(Ui.dp(c, 12), 0, Ui.dp(c, 12), 0)
            background = Ui.round(Ui.BAD, Ui.dp(c, 14).toFloat())
            setOnClickListener { phrase?.stop(); aiVoice?.stop(); voice?.stop() }
        }, LayoutParams(LayoutParams.WRAP_CONTENT, Ui.dp(c, 28)))
        listenBar.visibility = View.GONE
        addView(listenBar, Ui.lp(top = Ui.dp(c, 6)))

        input.textSize = 15f
        input.setOnFocusChangeListener { _, has -> if (has && tabs.isNotEmpty()) sizeBoxes(editing = true) }
        input.setOnClickListener { if (tabs.isNotEmpty()) sizeBoxes(editing = true) }
        addView(input, Ui.lp(top = Ui.dp(c, 6)))
        cleanRow.addView(cleanText, Ui.lp(0, weight = 1f))
        cleanRow.addView(Ui.text(c, "↶ Undo", 12f, Ui.ACCENT, true).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 2), 0, Ui.dp(c, 2))
            setOnClickListener { undoText?.let { input.setText(it); input.setSelection(it.length) }; cleanRow.visibility = View.GONE }
        })
        cleanRow.visibility = View.GONE
        addView(cleanRow, Ui.lp(top = Ui.dp(c, 4)))
        addView(countText, Ui.lp(top = Ui.dp(c, 4)))
        addView(warnText, Ui.lp(top = Ui.dp(c, 2)))
        input.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun afterTextChanged(s: Editable?) { updateCount() }
        })

        // ---------------------------------------------------------------- enhance button + enhanced window
        val mid = Ui.row(c).apply { setPadding(0, Ui.dp(c, 8), 0, 0) }
        enhancedLabel.setPadding(0, 0, 0, 0)
        mid.addView(enhancedLabel, Ui.lp(0, weight = 1f))
        goBtn = Ui.text(c, "✦ Enhance", 14f, Color.WHITE, true)
        mid.addView(goBtn.apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            setPadding(Ui.dp(c, 16), 0, Ui.dp(c, 16), 0)
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 17).toFloat())
            setOnClickListener { go() }
        }, LayoutParams(LayoutParams.WRAP_CONTENT, Ui.dp(c, 34)))
        addView(mid)

        val box = Ui.col(c, 12)
        box.background = Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat(), Ui.dp(c, 1) + 1, Ui.ACCENT)
        box.addView(tabsRow.first, Ui.lp())
        box.addView(noteText, Ui.lp(top = Ui.dp(c, 6)))
        box.addView(verRow, Ui.lp(top = Ui.dp(c, 4)))
        box.addView(body, Ui.lp(top = Ui.dp(c, 8)))
        box.addView(resultBox, Ui.lp(top = Ui.dp(c, 6)))
        box.addView(diffView, Ui.lp(top = Ui.dp(c, 6)))
        box.addView(actions, Ui.lp(top = Ui.dp(c, 10)))
        refineRow.addView(refineInput, Ui.lp(0, weight = 1f, end = Ui.dp(c, 6)))
        refineRow.addView(Ui.text(c, "↻", 18f, Color.WHITE, true).apply {
            gravity = Gravity.CENTER; includeFontPadding = false
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 18).toFloat())
            setOnClickListener { refine(refineInput.text.toString().trim()) }
        }, LayoutParams(Ui.dp(c, 36), Ui.dp(c, 36)))
        // Refine panel: shown below, under the Enhancement chips (added further down).
        refinePanel.background = Ui.round(0xFF141821.toInt(), Ui.dp(c, 14).toFloat(), Ui.dp(c, 1), 0x887C6CFF.toInt())
        refinePanel.setPadding(Ui.dp(c, 10), Ui.dp(c, 10), Ui.dp(c, 10), Ui.dp(c, 10))
        refinePanel.addView(refineLabel)
        refinePanel.addView(refineChips.first, Ui.lp(top = Ui.dp(c, 8)))
        refinePanel.addView(refineRow, Ui.lp(top = Ui.dp(c, 8)))
        refinePanel.visibility = View.GONE
        Ui.scrollBox(input); Ui.scrollBox(resultBox); Ui.scrollBox(diffView)
        resultBox.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun afterTextChanged(e: Editable?) {
                if (settingResult) return
                val t = tabs.getOrNull(selected) ?: return
                if (t.versions.isEmpty()) return
                t.versions[t.vIndex] = e.toString()   // own edits are kept in the version on screen
            }
        })
        addView(box, Ui.lp(top = Ui.dp(c, 6)))
        addView(savedText, Ui.lp(top = Ui.dp(c, 4)))

        // ---------------------------------------------------------------- choices
        choicesLabel = Ui.section(c, "Enhancement")
        addView(choicesLabel)
        addView(modeRow.first)
        customBox.setText(store.custom)
        addView(customBox, Ui.lp(top = Ui.dp(c, 8)))
        trBox.addView(Ui.section(c, "Translate to"))
        trBox.addView(trRow)
        addView(trBox)
        addView(refinePanel, Ui.lp(top = Ui.dp(c, 10)))
        addView(Ui.section(c, "AI"))
        addView(aiRow.first)
        addView(Ui.section(c, "Send through"))
        addView(routeRow.first)

        drawSeg(); drawModes(); drawAis(); drawRoutes(); drawLangs(); drawResult(); updateCount()
    }

    private fun drawSeg() {
        val c = act
        segRow.removeAllViews()
        for ((ask, label) in listOf(false to "✦ Enhance text", true to "💬 Ask AI")) {
            val on = askMode == ask
            segRow.addView(Ui.text(c, label, 14f, if (on) Color.WHITE else Ui.MUTED, true).apply {
                gravity = Gravity.CENTER; includeFontPadding = false
                if (on) background = Ui.round(Ui.ACCENT, Ui.dp(c, 10).toFloat())
                setOnClickListener { setAskMode(ask) }
            }, LayoutParams(0, Ui.dp(c, 32), 1f).apply { marginEnd = if (!ask) Ui.dp(c, 3) else 0 })
        }
    }

    fun setAskMode(ask: Boolean) {
        if (askMode == ask) return
        askMode = ask
        tabs.clear()
        origLabel.text = if (ask) "YOUR QUESTION" else "ORIGINAL"
        input.hint = if (ask) "Type or speak your question…" else "Paste, type or share text here…"
        goBtn.text = if (ask) "💬 Ask" else "✦ Enhance"
        choicesLabel.text = if (ask) "ANSWER STYLE" else "ENHANCEMENT"
        drawSeg(); drawModes(); drawResult(); updateCount()
    }

    /** [auto] = text arrived by Paste, Share, text menu, bubble, tile or voice: enhance it straight away. */
    fun setText(s: String, auto: Boolean = false) {
        input.setText(s); input.setSelection(input.text.length)
        tabs.clear(); drawResult()
        if (auto && !askMode) post { autoEnhance() }
    }

    /** Auto-enhance always uses Fix grammar; other modes run when their chip is tapped. */
    fun autoEnhance() {
        if (input.text.isBlank()) return
        if (store.mode != "grammar") { store.mode = "grammar"; drawModes() }
        go(auto = true)
    }

    fun pasteClipboard(): Boolean {
        val cm = act.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val t = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(act)?.toString()
        return if (!t.isNullOrBlank()) { setText(t, auto = true); true } else { Ui.toast(act, "Clipboard is empty"); false }
    }

    /**
     * Before a result: Original is big, Enhanced is a slim line.
     * After a result: Original shrinks (tap it to expand and edit), Enhanced grows.
     */
    private fun sizeBoxes(editing: Boolean = false) {
        val bigOriginal = tabs.isEmpty() || editing
        val lines = if (bigOriginal) (if (tabs.isEmpty()) 15 else 10) else 5
        input.minLines = lines; input.maxLines = lines
        input.isVerticalScrollBarEnabled = true
        body.minLines = if (tabs.isEmpty()) 1 else 3
        resultBox.minLines = 6; resultBox.maxLines = 14
    }

    fun refresh() { drawAis(); barRefresh() }

    private fun updateCount() {
        val s = input.text.toString()
        val w = words(s)
        countText.text = if (s.isEmpty()) "" else "$w words · ${s.length} characters"
        if (w > WARN_WORDS && !askMode) {
            val n = split(s, PART_WORDS).size
            warnText.visibility = View.VISIBLE
            warnText.text = "Long text: the API will enhance it in $n parts and join them back together. " +
                "\"My account\" sends it in one piece."
        } else warnText.visibility = View.GONE
    }

    // ------------------------------------------------------------------ choices

    private fun drawModes() {
        val row = modeRow.second; row.removeAllViews()
        if (askMode) {
            for (m in Modes.askStyles) {
                row.addView(Ui.chip(act, m.label, store.askStyle == m.id) {
                    store.askStyle = m.id; drawModes()
                    if (input.text.isNotBlank() && tabs.isNotEmpty()) go(auto = true)
                }, Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
            }
            customBox.visibility = View.GONE
            trBox.visibility = View.GONE
            enhancedLabel.text = "AI ANSWER · " + Modes.askById(store.askStyle).label.uppercase()
            return
        }
        for (m in Modes.all) {
            row.addView(Ui.chip(act, if (m.id == "translate") "🌐 Translate" else m.label, store.mode == m.id) {
                store.mode = m.id; drawModes()
                // Tapping a mode enhances straight away (Custom waits for the instruction).
                if (m.id != Modes.CUSTOM && input.text.isNotBlank()) go(auto = true)
            },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
        customBox.visibility = if (store.mode == Modes.CUSTOM) View.VISIBLE else View.GONE
        drawTrLangs()
        enhancedLabel.text = "ENHANCED · " + Modes.label(Modes.byId(store.mode)).uppercase()
    }

    /** "Translate to" chips: tap a language to translate straight away. */
    private fun drawTrLangs() {
        trBox.visibility = if (!askMode && store.mode == "translate") View.VISIBLE else View.GONE
        if (trBox.visibility != View.VISIBLE) return
        trRow.removeAllViews()
        val cur = store.trLang
        val isOther = Modes.MAIN_LANGS.none { it.first.equals(cur, true) }
        for ((v, label) in Modes.MAIN_LANGS) trRow.addView(Ui.chip(act, label, v.equals(cur, true)) { pickLang(v) })
        trRow.addView(Ui.chip(act, if (isOther) Modes.langLabel(cur) + " ▸" else "Others ▸", isOther) { otherLangs() })
    }

    private fun pickLang(v: String) {
        store.trLang = v; drawModes()
        if (input.text.isNotBlank()) go(auto = true)
    }

    private fun otherLangs() {
        val items = Modes.OTHER_LANGS.map { it.second } + "✎ Type any language"
        AlertDialog.Builder(act).setTitle("Translate to")
            .setItems(items.toTypedArray()) { _, i ->
                if (i < Modes.OTHER_LANGS.size) pickLang(Modes.OTHER_LANGS[i].first)
                else {
                    val box = Ui.input(act, "e.g. Japanese, Dutch, Greek…", false)
                    AlertDialog.Builder(act).setTitle("Any language").setView(box)
                        .setPositiveButton("Translate") { _, _ -> box.text.toString().trim().takeIf { it.isNotBlank() }?.let { pickLang(it) } }
                        .setNegativeButton("Cancel", null).show()
                }
            }.setNegativeButton("Cancel", null).show()
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

    /** Speak: the app's own listening, words appear live, Stop finishes (then enhance / ask straight away). */
    private fun listen() {
        voice?.let { if (it.active) { it.stop(); return } }
        aiVoice?.let { if (it.active) { it.stop(); return } }
        phrase?.let { if (it.active) { it.stop(); return } }
        if (act.checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            pendingListen = true
            act.requestPermissions(arrayOf(android.Manifest.permission.RECORD_AUDIO), MIC_REQ)
            return
        }
        // Main engine: AI live by phrase (Gemini). Without a Gemini key: Android live, then Google's pop-up.
        voiceSession++
        if (store.hasKey(AiVoice.gemini())) listenPhrase()
        else if (VoiceInput.available(act)) listenAndroid() else listenWithGoogle()
    }

    /** Records continuously; each phrase is written out by Gemini and appears in Original while you talk. */
    private fun listenPhrase() {
        markInsertPoint()
        tabs.clear(); drawResult(); cleanRow.visibility = View.GONE
        pauseBtn.visibility = View.VISIBLE
        val session = voiceSession
        val pv = PhraseVoice(act,
            onText = { t, waiting -> if (session == voiceSession) showLive(t, if (waiting > 0) "…" else "") },
            onState = { st ->
                if (session == voiceSession) {
                    listenText.tag = st
                    pauseBtn.visibility = if (st == "finishing") View.GONE else View.VISIBLE
                    if (st == null) { tickH.removeCallbacks(tick); showListen(null, "") } else tick.run()
                }
            },
            onDone = { all ->
                if (session != voiceSession) return@PhraseVoice
                showLive(all, "")
                if (all.isBlank()) { Ui.toast(act, "Nothing was heard — check the microphone or Bluetooth headset"); return@PhraseVoice }
                cleanText.text = "✓ Written by Gemini · not enhanced yet"; undoText = null
                cleanRow.visibility = View.VISIBLE
            })
        phrase = pv
        if (!pv.start()) {
            Ui.toast(act, "The microphone couldn't be opened — another app may be using it")
            if (VoiceInput.available(act)) listenAndroid()
        }
    }

    /** Where spoken text goes: at the cursor (or the end). */
    private var insBefore = ""
    private var insAfter = ""

    private fun markInsertPoint() {
        val t = input.text.toString()
        val at = if (input.hasFocus()) input.selectionStart.coerceIn(0, t.length) else t.length
        insBefore = t.substring(0, at); insAfter = t.substring(at)
    }

    private fun sep(a: String) = if (a.isEmpty() || a.endsWith(" ") || a.endsWith("\n")) "" else " "

    /** Shows live text inside Original: confirmed words white, words being recognised grey. */
    private fun showLive(final: String, partial: String) {
        val sb = android.text.SpannableStringBuilder(insBefore)
        val mid = listOf(final, partial).filter { it.isNotBlank() }.joinToString(" ")
        if (mid.isNotEmpty()) sb.append(sep(insBefore)).append(final)
        if (partial.isNotBlank()) {
            if (final.isNotBlank()) sb.append(' ')
            val st = sb.length
            sb.append(partial)
            sb.setSpan(android.text.style.ForegroundColorSpan(Ui.MUTED), st, sb.length, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
        val cursor = sb.length
        if (insAfter.isNotEmpty() && mid.isNotEmpty()) sb.append(if (insAfter.startsWith(" ") || insAfter.startsWith("\n")) "" else " ")
        sb.append(insAfter)
        input.setText(sb); input.setSelection(cursor.coerceAtMost(input.text.length))
    }

    private fun showListen(st: String?, sub: String, paused: Boolean = false) {
        if (st == null) { listenBar.visibility = View.GONE; return }
        listenBar.visibility = View.VISIBLE
        listenDot.background = Ui.round(if (paused) 0xFFFFC857.toInt() else Ui.BAD, Ui.dp(act, 5).toFloat())
        listenText.text = android.text.Html.fromHtml("<b>$st</b><br><font color='#9AA3B5'><small>$sub</small></font>", 0)
    }

    private val tickH = android.os.Handler(android.os.Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() {
            tickH.removeCallbacks(this)
            val pv = phrase?.takeIf { it.active }
            val v = voice?.takeIf { it.active }
            val secs = pv?.seconds ?: v?.seconds ?: return
            val isPaused = pv?.paused ?: v?.paused ?: false
            val t = "${secs / 60}:${String.format("%02d", secs % 60)}"
            val lang = langName(store.voiceLang).substringBefore("  (")
            when {
                pv != null && !pv.paused && listenText.tag == "finishing" -> showListen("✍ Finishing the last phrase…", "")
                isPaused -> showListen("⏸ Paused $t", "Tap Resume to continue", true)
                pv != null -> showListen("● Listening $t", "AI (Gemini) · any language · text appears after each phrase")
                else -> showListen("● Listening $t", "Android · $lang · live")
            }
            pauseBtn.text = if (isPaused) "▶ Resume" else "⏸ Pause"
            pauseBtn.background = Ui.round(if (isPaused) Ui.GOOD else 0xFF4B5263.toInt(), Ui.dp(act, 14).toFloat())
            tickH.postDelayed(this, 500)
        }
    }

    private fun listenAndroid() {
        markInsertPoint()
        tabs.clear(); drawResult(); cleanRow.visibility = View.GONE
        pauseBtn.visibility = View.VISIBLE
        voice = VoiceInput(act, langTag(store.voiceLang),
            onText = { f, p -> showLive(f, p) },
            onState = { st -> if (st == null) showListen(null, "") else tick.run() },
            onDone = { heard -> afterSpeech(heard) },
            onFatal = { heard ->
                // Android's speech service failed: keep what was heard, continue with the AI recording.
                showLive(heard, "")
                if (store.hasKey(AiVoice.gemini())) {
                    Ui.toast(act, "Android voice failed — continuing with AI recording")
                    insBefore = input.text.toString().substring(0, input.selectionStart.coerceIn(0, input.text.length))
                    insAfter = input.text.toString().substring(insBefore.length)
                    listenAi(keepPoint = true)
                } else { Ui.toast(act, "Android voice failed — opening Google voice"); listenWithGoogle() }
            })
        voice?.start()
    }

    /** After Stop: Gemini cleans Android's text (misheard words, punctuation). No enhancement runs. */
    private fun afterSpeech(heard: String) {
        showLive(heard, "")
        if (heard.isBlank()) { Ui.toast(act, "Nothing was heard"); return }
        if (!store.hasKey(AiVoice.gemini())) return
        showListen("✍ Cleaning up with Gemini…", "fixing misheard words and punctuation")
        pauseBtn.visibility = View.GONE
        val before = insBefore; val after = insAfter
        val session = voiceSession
        Thread {
            val r = runCatching { AiVoice.cleanup(act, heard) }
            act.runOnUiThread {
                if (session != voiceSession) return@runOnUiThread   // a newer recording has started
                showListen(null, ""); pauseBtn.visibility = View.VISIBLE
                r.onSuccess { clean ->
                    if (clean.isBlank()) return@onSuccess
                    val raw = input.text.toString()
                    val sb = android.text.SpannableStringBuilder(before).append(sep(before))
                    val st = sb.length
                    sb.append(Diff.added(heard, clean))
                    val end = sb.length
                    if (after.isNotEmpty()) sb.append(if (after.startsWith(" ")) "" else " ").append(after)
                    input.setText(sb); input.setSelection(end.coerceAtMost(input.text.length))
                    val fixed = Diff.countChanged(heard, clean)
                    undoText = raw
                    cleanText.text = "✓ Cleaned by Gemini · $fixed word${if (fixed == 1) "" else "s"} fixed · not enhanced yet"
                    cleanRow.visibility = View.VISIBLE
                    if (st < 0) Unit
                }.onFailure { e ->
                    ErrorLog.add(act, "Voice cleanup", "Gemini", AiVoice.fastModel(act), (e as? AiException)?.code ?: 0, e.message ?: "", heard)
                    Ui.toast(act, "Couldn't clean the text (${e.message}) — kept Android's text")
                }
            }
        }.start()
    }

    /** Backup engine: records until Stop, then Gemini writes it out (any language, your technical terms). */
    private fun listenAi(keepPoint: Boolean = false) {
        if (!keepPoint) markInsertPoint()
        pauseBtn.visibility = View.GONE
        var av: AiVoice? = null
        av = AiVoice(act,
            onState = { st -> showListen(st, if (st?.startsWith("🔴") == true) "AI (Gemini) · any language · pauses are fine" else "") },
            onDone = { t ->
                showLive(t, "")
                pauseBtn.visibility = View.VISIBLE
                cleanText.text = "✓ Written out by Gemini · not enhanced yet"; undoText = null
                cleanRow.visibility = View.VISIBLE
            },
            onFail = { msg, file ->
                pauseBtn.visibility = View.VISIBLE
                val b = AlertDialog.Builder(act).setTitle("Voice recognition").setMessage(msg)
                if (file != null) b.setPositiveButton("Retry") { _, _ ->
                    showListen("✍ Writing it out with Gemini…", "")
                    Thread { av?.recognize(file) }.start()
                }
                b.setNegativeButton("Cancel") { _, _ -> file?.delete() }
                b.show()
            })
        aiVoice = av
        tabs.clear(); drawResult()
        if (!av.start()) Ui.toast(act, "The microphone couldn't be opened — another app may be using it")
    }

    private var pendingListen = false

    /** Called by the screen after the microphone permission answer. */
    fun onMicPermission(granted: Boolean) {
        if (!pendingListen) return
        pendingListen = false
        if (granted) listen() else Ui.toast(act, "Microphone permission is needed for voice input")
    }

    fun stopVoice() { voice?.cancel(); aiVoice?.cancel(); phrase?.cancel() }

    /** Fallback when the phone has no speech service for apps: Google's own pop-up. */
    private fun listenWithGoogle() {
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
        markInsertPoint(); showLive(said, "")
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
        sizeBoxes()
        val c = act
        tabsRow.second.removeAllViews(); actions.removeAllViews(); verRow.removeAllViews()
        resultBox.visibility = View.GONE; diffView.visibility = View.GONE; body.visibility = View.VISIBLE
        refinePanel.visibility = View.GONE; verRow.visibility = View.GONE
        if (tabs.isEmpty()) {
            tabsRow.first.visibility = View.GONE
            noteText.visibility = View.GONE
            body.setTextColor(Ui.MUTED); body.text = if (askMode) "The AI's answer will appear here." else "Your enhanced text will appear here."
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
        val out = t.current
        when {
            t.account -> {
                body.setTextColor(Ui.MUTED)
                body.text = if (t.sent) t.status.ifBlank { "Sent to your $name account." } else "Not sent yet."
                actions.addView(Ui.button(c, if (t.sent) "Send again to $name" else "Send to my $name", !t.sent) {
                    sendAccount(t)
                }, Ui.lp(0, weight = 1f))
            }
            out != null -> {
                drawVersionBar(t)
                if (t.busy) { body.setTextColor(Ui.MUTED); body.text = t.status } else body.visibility = View.GONE
                if (t.showChanges && !t.ask) {
                    diffView.visibility = View.VISIBLE
                    diffView.text = Diff.spans(input.text.toString().trim(), out)
                } else {
                    resultBox.visibility = View.VISIBLE
                    if (resultBox.text.toString() != out) {
                        settingResult = true; resultBox.setText(out); settingResult = false
                    }
                }
                fun cur() = t.current ?: ""
                actions.addView(Ui.button(c, "⧉  Copy", true) { Ui.copy(c, cur()) }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
                actions.addView(Ui.button(c, "Share") { share(cur()) }, Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
                val replace = onReplace
                if (replace != null) actions.addView(Ui.button(c, "Replace") { replace(cur()) },
                    Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
                actions.addView(Ui.button(c, "Continue") { ChatActivity.open(c, t.convId) }, Ui.lp(LayoutParams.WRAP_CONTENT))
                drawRefine(t)
            }
            else -> {
                body.setTextColor(if (t.error) Ui.BAD else Ui.MUTED); body.text = t.status
            }
        }
        val saved = tabs.filter { it.convId >= 0 }.map { it.p.label }.distinct()
        savedText.text = if (saved.isEmpty()) "" else "Saved in ${if (askMode) Db.ASK else Db.PROJECT} · " + saved.joinToString(", ")
    }

    // ------------------------------------------------------------------ run

    /** "‹ v2/3 ›   ◐ Changes   ⬆ Use as original" */
    private fun drawVersionBar(t: Tab) {
        val c = act
        verRow.visibility = View.VISIBLE
        fun link(label: String, on: Boolean = false, enabled: Boolean = true, f: () -> Unit) =
            verRow.addView(Ui.text(c, label, 13f, if (!enabled) 0x55FFFFFF else if (on) Ui.GOOD else Ui.ACCENT, true).apply {
                setPadding(0, Ui.dp(c, 4), Ui.dp(c, 14), Ui.dp(c, 4))
                if (enabled) setOnClickListener { f() }
            })
        val n = t.versions.size
        link("‹", enabled = t.vIndex > 0) { t.vIndex--; drawResult() }
        verRow.addView(Ui.text(c, "v${t.vIndex + 1}/${n.coerceAtLeast(1)}", 13f, Ui.TEXT, true)
            .apply { setPadding(0, Ui.dp(c, 4), Ui.dp(c, 14), Ui.dp(c, 4)) })
        link("›", enabled = t.vIndex < n - 1) { t.vIndex++; drawResult() }
        verRow.addView(android.view.View(c), Ui.lp(0, weight = 1f))
        if (!t.ask) link(if (t.showChanges) "◐ Changes ON" else "◐ Changes", on = t.showChanges) {
            t.showChanges = !t.showChanges; drawResult()
        }
        if (!t.ask) link("⬆ Use as original") {
            val text = t.current ?: return@link
            input.setText(text); input.setSelection(input.text.length)
            tabs.clear(); drawResult()
            Ui.toast(c, "Moved up — pick a mode to enhance it again")
        }
    }

    /** Quick refine chips + "Tell the AI what to change…". */
    private fun drawRefine(t: Tab) {
        val c = act
        refinePanel.visibility = View.VISIBLE
        refineLabel.text = android.text.Html.fromHtml(
            if (t.ask) "FOLLOW UP <font color='#9AA3B5'>· asks about the answer above</font>"
            else "REFINE RESULT <font color='#9AA3B5'>· changes the enhanced text above</font>", 0)
        val row = refineChips.second; row.removeAllViews()
        val opts = if (t.ask) listOf("More detail", "Shorter", "Simpler", "Give examples", "Next steps")
            else listOf("Shorter", "More formal", "Warmer", "Simpler", "Keep my wording")
        for (o in opts) row.addView(Ui.chip(c, o, false) { refine(o) },
            Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 6)))
        refineInput.hint = if (t.ask) "Ask a follow-up…" else "Tell the AI what to change…"
    }

    /** Re-enhance the version on screen (including own edits); the result becomes a new version. */
    private fun refine(request: String) {
        if (request.isBlank()) { Ui.toast(act, "Type what to change, or tap a chip"); return }
        val t = tabs.getOrNull(selected) ?: return
        val cur = t.current ?: return
        if (t.busy) return
        if (!store.hasKey(t.p)) { Ui.toast(act, "Add a ${t.p.label} API key in Settings"); return }
        val p = t.p
        val sys = if (t.ask) Modes.askSystem(store) else Modes.system(store)
        val req = if (t.ask) request else "Change request: $request\nApply it to the text above and reply with the full revised text only."
        val history = listOf(Msg("user", t.firstMsg), Msg("assistant", cur), Msg("user", req))
        refineInput.setText(""); hideKeyboard()
        t.busy = true; t.status = "Refining: $request…"; drawResult()
        Thread {
            val r = runCatching {
                Providers.chatSmart(p, store.key(p), store.model(p), sys, history) { msg -> update { t.status = msg } }.text
            }
            r.onSuccess {
                if (t.convId >= 0) { db.addMsg(t.convId, "user", request); db.addMsg(t.convId, "assistant", it) }
            }
            r.onFailure { ErrorLog.add(act, "Refine", p.label, store.model(p), (it as? AiException)?.code ?: 0, it.message ?: "", cur) }
            update {
                t.busy = false
                r.onSuccess { out -> t.versions += out; t.vIndex = t.versions.size - 1; t.showChanges = false }
                r.onFailure { e -> Ui.toast(act, e.message ?: "Refine failed") }
            }
        }.start()
    }

    /** Enhance now with the last mode (used by the text-selection menu). */
    fun runNow() { if (input.text.isNotBlank()) go(auto = true) }

    private fun go(auto: Boolean = false) {
        val text = input.text.toString().trim()
        if (text.isEmpty()) { Ui.toast(act, "Add some text first"); return }
        store.custom = customBox.text.toString()
        val mode = if (askMode) Modes.askById(store.askStyle) else Modes.byId(store.mode)
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
        val msg = if (askMode) Modes.askAccountMessage(store, Modes.askById(store.askStyle), original)
            else Modes.accountMessage(store, Modes.byId(store.mode), original)
        if (!t.sent) {
            val id = if (askMode) db.newConv(t.p.id, store.askStyle, original, "account", Db.ASK)
                else db.newConv(t.p.id, store.mode, original, "account")
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
        val ask = askMode
        val parts = if (!ask && words(original) > WARN_WORDS) split(original, PART_WORDS) else listOf(original)
        val sys = if (ask) Modes.askSystem(store) else Modes.system(store)
        fun msgFor(textPart: String) = if (ask) Modes.askMessage(mode, textPart) else Modes.firstMessage(mode, custom, textPart)
        val deeper = ask && mode.id == "deeper"
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
                    // "Think deeper": try the stronger model first; if the plan doesn't allow it, use the default.
                    var strongAnswer: String? = null
                    if (deeper) {
                        val strong = strongModelFor(p, key)
                        if (strong != null && strong != model) {
                            update { t.status = "Thinking deeper with $strong…" }
                            strongAnswer = try {
                                Providers.chat(p, key, strong, sys, listOf(Msg("user", msgFor(part)))).takeIf { it.isNotBlank() }
                            } catch (e: AiException) {
                                // Not in the plan (or gone): stop trying it; use the default model.
                                if (e.code in setOf(402, 403, 404) || (e.code == 429 &&
                                        Regex("quota|billing|plan", RegexOption.IGNORE_CASE).containsMatchIn(e.message ?: ""))) store.setStrongModel(p, "-")
                                null
                            } catch (_: Exception) { null }
                            if (strongAnswer != null) backup = "★$strong"
                        }
                    }
                    if (strongAnswer != null) outs += strongAnswer
                    else {
                        val a = Providers.chatSmart(p, key, model, sys, listOf(Msg("user", msgFor(part))),
                            fast = !ask && mode.id == "grammar") { msg ->
                            update { t.status = prefix + msg }
                        }
                        outs += a.text
                        if (a.switched) backup = a.model
                    }
                } catch (e: Exception) {
                    error = prefix + (e.message ?: e.toString())
                    lastCode = (e as? AiException)?.code ?: 0
                    break
                }
            }
            val err = error
            if (err == null) {
                val joined = outs.joinToString("\n\n")
                val id = db.newConv(p.id, mode.id, original, "api", if (ask) Db.ASK else Db.PROJECT)
                db.addMsg(id, "user", msgFor(original))
                db.addMsg(id, "assistant", joined)
                val notes = mutableListOf<String>()
                if (parts.size > 1) notes += "enhanced in ${parts.size} parts"
                backup?.let {
                    notes += if (it.startsWith("★")) "answered by ${it.drop(1)} (stronger model)"
                    else "answered by backup model $it (usual one was busy)"
                }
                update {
                    t.text = joined; t.convId = id; t.note = notes.joinToString(" · ")
                    t.versions.clear(); t.versions += joined; t.vIndex = 0
                    t.firstMsg = msgFor(original); t.ask = ask
                }
            } else {
                ErrorLog.add(act, if (ask) "Ask AI" else "Enhance", p.label, store.model(p), lastCode, err, original)
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

    /** Finds (once) and remembers the stronger model for "Think deeper". */
    private fun strongModelFor(p: Provider, key: String): String? {
        val saved = store.strongModel(p)
        if (saved == "-") return null
        if (saved.isNotBlank()) return saved
        val m = runCatching { Providers.pickStrong(p, Providers.listModels(p, key)) }.getOrNull()
        store.setStrongModel(p, m ?: "-")
        return m
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
