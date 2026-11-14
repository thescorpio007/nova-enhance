package com.nova.enhance

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.speech.RecognizerIntent
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.lang.ref.WeakReference

/**
 * "About me": the user's field, a short description and terms to keep as written.
 * It is added to every enhancement so the AI uses the correct terminology of that field.
 */
object Profile {
    const val VOICE_REQ = 78

    val FIELDS = listOf(
        "General", "Architecture", "Structural", "MEP", "Construction / Site", "Project management", "Interior design", "Civil",
        "Engineering", "Medical", "Legal", "Finance", "IT", "Education", "Student", "Business & Sales", "Government", "Marketing"
    )

    /** Construction words, codes (SSM, SDS, RFI…) and document disciplines are used only for these fields. */
    private val CONSTRUCTION = Regex("archit|structur|\\bmep\\b|construct|interior|civil engineer|civil works|landscape", RegexOption.IGNORE_CASE)
    /** "a person working in Architecture, MEP" — for AI instructions (never a fixed job). */
    fun who(s: Store): String {
        val f = fields(s).filter { it != "General" }
        return if (f.isEmpty()) "the user" else "a person working in " + f.joinToString(", ")
    }

    fun isConstruction(s: Store) = CONSTRUCTION.containsMatchIn(s.profileFields + " " + s.profileAbout)

    private var voiceBox: WeakReference<EditText>? = null

    fun fields(s: Store): List<String> = s.profileFields.split("|").filter { it.isNotBlank() }

    fun isSet(s: Store) = fields(s).isNotEmpty() || s.profileAbout.isNotBlank()

    /** One line for the front bar. */
    fun summary(s: Store): String {
        if (!isSet(s)) return "Tell us about yourself — for correct technical terms"
        val f = fields(s).joinToString(", ")
        val a = s.profileAbout.replace('\n', ' ').trim()
        return listOf(f, a).filter { it.isNotBlank() }.joinToString(" · ")
    }

    /** Context added to the AI's instructions. Empty if no profile. */
    fun note(s: Store): String {
        if (!isSet(s) && s.profileTerms.isBlank()) return ""
        val sb = StringBuilder("\n\nAbout the writer (use this to choose terminology):")
        if (fields(s).isNotEmpty()) sb.append("\n- Field: ").append(fields(s).joinToString(", "))
        if (s.profileAbout.isNotBlank()) sb.append("\n- In their words: ").append(s.profileAbout.trim())
        sb.append("\nUse the correct, standard technical terminology of this field. Fix technical terms that are ")
        sb.append("misspelled or misused (for example a wrong but similar word), but never change the meaning or add facts.")
        if (s.profileTerms.isNotBlank())
            sb.append("\nKeep these terms, abbreviations and names exactly as written: ").append(s.profileTerms.trim())
        return sb.toString()
    }

    // ------------------------------------------------------------------ edit window

    fun edit(act: Activity, onSaved: () -> Unit) {
        val s = Store(act)
        val c = act
        val chosen = fields(s).toMutableSet()
        val first = !s.profileAsked
        val root = Ui.col(c, 18)
        root.addView(Ui.text(c, if (first) "✦ Nova works for anyone, in any field. Tell it about you — every correction, search and draft will follow your field. Already using Nova? Restore your backup instead."
            else "Your field and role. Every enhancement will use the correct technical terms of your field.", 13f, Ui.MUTED))

        root.addView(Ui.section(c, "My field  · pick one or more"))
        val rows = FlowLayout(c, Ui.dp(c, 8), Ui.dp(c, 8))
        fun drawChips() {
            rows.removeAllViews()
            val all = FIELDS + chosen.filter { it !in FIELDS }
            for (f in all) {
                val on = f in chosen
                rows.addView(Ui.chip(c, (if (on) "✓ " else "") + f, on) {
                    if (f in chosen) chosen.remove(f) else chosen.add(f)
                    drawChips()
                })
            }
            rows.addView(Ui.chip(c, "+ Other", false) {
                val box = Ui.input(c, "Your field", false)
                AlertDialog.Builder(c).setTitle("Other field").setView(box)
                    .setPositiveButton("Add") { _, _ ->
                        box.text.toString().trim().takeIf { it.isNotBlank() }?.let { chosen.add(it.replace("|", " ")); drawChips() }
                    }.setNegativeButton("Cancel", null).show()
            }.apply { background = Ui.round(Ui.CARD2, Ui.dp(c, 20).toFloat(), Ui.dp(c, 1), 0x66FFFFFF) })
        }
        drawChips()
        root.addView(rows, Ui.lp(top = Ui.dp(c, 2)))

        val aboutHead = Ui.row(c)
        aboutHead.addView(Ui.section(c, "About me"), Ui.lp(0, weight = 1f))
        val about = Ui.input(c, "e.g. Pharmacist in a hospital · Math teacher · Sales manager · Site engineer…", true, 4)
            .apply { setText(s.profileAbout); textSize = 15f }
        aboutHead.addView(Ui.text(c, "🎤", 13f, android.graphics.Color.WHITE).apply {
            gravity = Gravity.CENTER; includeFontPadding = false
            background = Ui.round(Ui.ACCENT, Ui.dp(c, 15).toFloat())
            setOnClickListener { listen(act, about) }
        }, LinearLayout.LayoutParams(Ui.dp(c, 30), Ui.dp(c, 30)).apply { topMargin = Ui.dp(c, 8) })
        root.addView(aboutHead, Ui.lp(top = Ui.dp(c, 4)))
        root.addView(about, Ui.lp(top = Ui.dp(c, 6)))

        root.addView(Ui.section(c, "My terms  · kept exactly as written"))
        val terms = Ui.input(c, "e.g. names, product names, abbreviations you use", true, 2).apply { setText(s.profileTerms); textSize = 15f }
        root.addView(terms)

        // First start: the AI key (a free Gemini key works well).
        val gem = Providers.byId("gemini")
        var keyBox: EditText? = null
        if (first && gem != null && !s.hasKey(gem)) {
            root.addView(Ui.section(c, "Your AI  · a free Gemini key works well"))
            keyBox = Ui.input(c, "Paste your Gemini API key (optional now)", false).apply {
                textSize = 15f; inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
            }
            root.addView(keyBox)
            root.addView(Ui.text(c, "🔑 Get a free key (aistudio.google.com)", 13f, Ui.ACCENT, true).apply {
                setPadding(0, Ui.dp(c, 8), 0, 0)
                setOnClickListener { try { act.startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse("https://aistudio.google.com/apikey"))) } catch (_: Exception) { } }
            })
        }

        val dlg = AlertDialog.Builder(c)
            .setTitle(if (first) "✦  Welcome to Nova Enhance" else "👤  Tell us about yourself")
            .setView(ScrollView(c).apply { addView(root) })
            .setPositiveButton("Save") { _, _ ->
                s.profileFields = chosen.joinToString("|")
                s.profileAbout = about.text.toString().trim()
                s.profileTerms = terms.text.toString().trim()
                s.profileAsked = true
                keyBox?.text?.toString()?.trim()?.takeIf { it.length > 10 && gem != null }?.let { s.setKey(gem!!, it) }
                Ui.toast(c, "Saved — enhancements will use your field's terms")
                onSaved()
            }
            .setNegativeButton(if (isSet(s)) "Cancel" else "Later") { _, _ -> s.profileAsked = true }
        if (first) dlg.setNeutralButton("📂 Restore backup") { _, _ -> s.profileAsked = true; SettingsActivity.open(act, SettingsActivity.S_BACKUP) }
        dlg.show()
    }

    private fun listen(act: Activity, box: EditText) {
        voiceBox = WeakReference(box)
        val i = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_PROMPT, "Tell us about yourself")
        }
        try { act.startActivityForResult(i, VOICE_REQ) }
        catch (_: Exception) { Ui.toast(act, "Voice typing isn't available — install or enable the Google app") }
    }

    /** Screens forward the speech result here. */
    fun onVoice(data: Intent?) {
        val said = data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull() ?: return
        val box = voiceBox?.get() ?: return
        val cur = box.text.toString().trim()
        box.setText(if (cur.isEmpty()) said else "$cur $said")
        box.setSelection(box.text.length)
    }

    // ------------------------------------------------------------------ front bar

    /** Slim "👤 Me: …  Edit" bar. */
    fun bar(act: Activity, onSaved: () -> Unit): Pair<View, () -> Unit> {
        val c = act
        val s = Store(c)
        val row = Ui.row(c).apply {
            background = Ui.round(Ui.CARD, Ui.dp(c, 12).toFloat(), 1, 0x1AFFFFFF)
            setPadding(Ui.dp(c, 10), Ui.dp(c, 7), Ui.dp(c, 10), Ui.dp(c, 7))
        }
        row.addView(Ui.text(c, "👤", 13f), Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        val line = Ui.text(c, "", 13f, Ui.MUTED).apply { maxLines = 1; isSingleLine = true; ellipsize = android.text.TextUtils.TruncateAt.END }
        row.addView(line, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        val edit = Ui.text(c, "Edit", 13f, Ui.ACCENT, true)
        row.addView(edit)
        val refresh = {
            val set = isSet(s)
            line.text = android.text.Html.fromHtml(
                (if (set) "<b><font color='#ECEEF3'>Me:</font></b> " else "") + android.text.TextUtils.htmlEncode(summary(s)), 0)
            edit.text = if (set) "Edit" else "Set up"
        }
        row.setOnClickListener { edit(act) { refresh(); onSaved() } }
        refresh()
        return row to refresh
    }

    @Suppress("unused")
    private fun label(c: Activity, s: String): TextView = Ui.text(c, s)
}
