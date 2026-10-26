package com.nova.enhance

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.text.Editable
import android.text.TextWatcher
import android.view.View
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView

/**
 * The enhance screen (text box, modes, AI choice, results). Used by the main screen
 * and by the quick sheet that opens from Share, text selection, the bubble and the tile.
 * [onReplace] is set when the text came from another app's selection menu and can be replaced there.
 */
class EnhancePanel(
    private val act: Activity,
    private val onReplace: ((String) -> Unit)? = null
) : LinearLayout(act) {

    private val store = Store(act)
    private val db = Db.get(act)
    val input = Ui.input(act, "Paste, type or share text here…", true, 5)
    private val customBox = Ui.input(act, "Your instruction, e.g. \"Make it friendlier\"", true, 2)
    private val modeRow = Ui.chipRow(act)
    private val aiRow = Ui.chipRow(act)
    private val routeRow = Ui.chipRow(act)
    private val results = Ui.col(act)
    private val countText = Ui.text(act, "", 12f, Ui.MUTED)

    init {
        orientation = VERTICAL
        val c = act

        val head = Ui.row(c)
        head.addView(Ui.section(c, "Your text"), Ui.lp(0, weight = 1f))
        head.addView(Ui.text(c, "Paste", 14f, Ui.ACCENT, true).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 18), Ui.dp(c, 4), Ui.dp(c, 8))
            setOnClickListener { pasteClipboard() }
        })
        head.addView(Ui.text(c, "Clear", 14f, Ui.MUTED, true).apply {
            setPadding(Ui.dp(c, 12), Ui.dp(c, 18), 0, Ui.dp(c, 8))
            setOnClickListener { input.setText(""); results.removeAllViews() }
        })
        addView(head)
        addView(input, Ui.lp())
        addView(countText, Ui.lp(top = Ui.dp(c, 4)))
        input.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun afterTextChanged(s: Editable?) {
                val n = s?.length ?: 0
                countText.text = if (n == 0) "" else "$n characters"
            }
        })

        addView(Ui.section(c, "Enhancement"))
        addView(modeRow.first)
        customBox.setText(store.custom)
        addView(customBox, Ui.lp(top = Ui.dp(c, 10)))

        addView(Ui.section(c, "AI"))
        addView(aiRow.first)

        addView(Ui.section(c, "Send through"))
        addView(routeRow.first)

        addView(Ui.button(c, "✦  Enhance", true) { go() }, Ui.lp(top = Ui.dp(c, 18)))
        addView(results, Ui.lp(top = Ui.dp(c, 8)))

        drawModes(); drawAis(); drawRoutes()
    }

    fun setText(s: String) { input.setText(s); input.setSelection(input.text.length) }

    fun pasteClipboard(): Boolean {
        val cm = act.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val t = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(act)?.toString()
        return if (!t.isNullOrBlank()) { setText(t); true } else { Ui.toast(act, "Clipboard is empty"); false }
    }

    // ------------------------------------------------------------------ choices

    private fun drawModes() {
        val row = modeRow.second; row.removeAllViews()
        for (m in Modes.all) {
            row.addView(Ui.chip(act, m.label, store.mode == m.id) { store.mode = m.id; drawModes() },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
        customBox.visibility = if (store.mode == Modes.CUSTOM) View.VISIBLE else View.GONE
    }

    private fun drawAis() {
        val row = aiRow.second; row.removeAllViews()
        val opts = Providers.all.map { it.id to it.label } + ("all" to "All AIs")
        for ((id, label) in opts) {
            val p = Providers.byId(id)
            val tag = if (p != null && !store.hasKey(p) && store.webProject(p).isBlank()) " ·" else ""
            row.addView(Ui.chip(act, label + tag, store.target == id) { store.target = id; drawAis() },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
    }

    private fun drawRoutes() {
        val row = routeRow.second; row.removeAllViews()
        val opts = listOf("api" to "API key", "account" to "My account", "both" to "Both")
        for ((id, label) in opts) {
            row.addView(Ui.chip(act, label, store.route == id) { store.route = id; drawRoutes() },
                Ui.lp(LayoutParams.WRAP_CONTENT, end = Ui.dp(act, 8)))
        }
    }

    // ------------------------------------------------------------------ run

    private fun go() {
        val text = input.text.toString().trim()
        if (text.isEmpty()) { Ui.toast(act, "Add some text first"); return }
        store.custom = customBox.text.toString()
        val mode = Modes.byId(store.mode)
        val first = Modes.firstMessage(mode, store.custom, text)
        val targets = if (store.target == "all") Providers.all else listOfNotNull(Providers.byId(store.target))
        val route = store.route
        results.removeAllViews()
        hideKeyboard()

        if (route == "api" || route == "both") {
            val usable = targets.filter { store.hasKey(it) }
            val missing = targets.filter { !store.hasKey(it) }
            if (usable.isEmpty()) {
                missingKeys(missing.ifEmpty { targets })
                if (route == "api") return
            } else if (missing.isNotEmpty() && store.target != "all") {
                missingKeys(missing)
            }
            for (p in usable) runApi(p, mode, text, first)
        }
        if (route == "account" || route == "both") {
            if (targets.size == 1) {
                val p = targets[0]
                saveAccountConv(p, mode, text, first)
                WebAccountActivity.send(act, p, first)
            } else {
                // One account page at a time: give a button for each AI.
                val card = Ui.card(act)
                card.addView(Ui.text(act, "Send to my accounts", 16f, Ui.TEXT, true))
                card.addView(Ui.text(act, "Each opens your signed-in AI in its Enhancement project and sends the text.", 13f, Ui.MUTED))
                for (p in targets) {
                    card.addView(Ui.button(act, "Send to my ${p.label}") {
                        saveAccountConv(p, mode, text, first)
                        WebAccountActivity.send(act, p, first)
                    }, Ui.lp(top = Ui.dp(act, 8)))
                }
                results.addView(card, Ui.lp(top = Ui.dp(act, 12)))
            }
        }
    }

    private fun saveAccountConv(p: Provider, mode: Mode, text: String, first: String) {
        val id = db.newConv(p.id, mode.id, text, "account")
        db.addMsg(id, "user", first)
    }

    private fun missingKeys(ps: List<Provider>) {
        val card = Ui.card(act)
        card.addView(Ui.text(act, "No API key yet for: " + ps.joinToString { it.label }, 14f, Ui.BAD))
        card.addView(Ui.button(act, "Open Settings") {
            act.startActivity(Intent(act, SettingsActivity::class.java))
        }, Ui.lp(top = Ui.dp(act, 8)))
        results.addView(card, Ui.lp(top = Ui.dp(act, 12)))
    }

    private fun runApi(p: Provider, mode: Mode, original: String, first: String) {
        val c = act
        val card = Ui.card(c)
        val top = Ui.row(c)
        top.addView(Ui.text(c, "${p.label}  ·  ${mode.label}", 14f, Ui.ACCENT, true), Ui.lp(0, weight = 1f))
        val spin = ProgressBar(c).apply { isIndeterminate = true }
        top.addView(spin, LayoutParams(Ui.dp(c, 22), Ui.dp(c, 22)))
        card.addView(top)
        val body = Ui.text(c, "Working…", 16f, Ui.MUTED).apply { setTextIsSelectable(true) }
        card.addView(body, Ui.lp(top = Ui.dp(c, 8)))
        val actions = Ui.row(c).apply { visibility = View.GONE }
        card.addView(actions, Ui.lp(top = Ui.dp(c, 10)))
        results.addView(card, Ui.lp(top = Ui.dp(c, 12)))

        val key = store.key(p); val model = store.model(p)
        Thread {
            val ans: Result<Answer> = runCatching {
                Providers.chatSmart(p, key, model, Modes.SYSTEM, listOf(Msg("user", first))) { msg ->
                    act.runOnUiThread { body.text = msg }
                }
            }
            val res: Result<String> = ans.map { it.text }
            var convId = -1L
            res.onSuccess {
                convId = db.newConv(p.id, mode.id, original, "api")
                db.addMsg(convId, "user", first); db.addMsg(convId, "assistant", it)
            }
            act.runOnUiThread {
                if (act.isFinishing) return@runOnUiThread
                spin.visibility = View.GONE
                ans.getOrNull()?.takeIf { it.switched }?.let { a ->
                    card.addView(Ui.text(c, "Answered by backup model ${a.model} (usual model was busy)", 12f, Ui.MUTED), 1)
                }
                res.onSuccess { out ->
                    body.setTextColor(Ui.TEXT); body.text = out
                    actions.visibility = View.VISIBLE
                    fun a(label: String, f: () -> Unit) = actions.addView(
                        Ui.text(c, label, 14f, Ui.ACCENT, true).apply {
                            setPadding(0, Ui.dp(c, 6), Ui.dp(c, 18), Ui.dp(c, 6)); setOnClickListener { f() }
                        })
                    a("Copy") { Ui.copy(c, out) }
                    a("Share") { share(out) }
                    if (onReplace != null) a("Replace") { onReplace.invoke(out) }
                    a("Continue") { ChatActivity.open(c, convId) }
                }.onFailure { e ->
                    body.setTextColor(Ui.BAD); body.text = e.message ?: e.toString()
                }
            }
        }.start()
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

    fun refresh() { drawAis() }

    companion object {
        fun confirm(c: Context, msg: String, ok: String, f: () -> Unit) {
            AlertDialog.Builder(c).setMessage(msg).setPositiveButton(ok) { _, _ -> f() }
                .setNegativeButton("Cancel", null).show()
        }
    }
}
