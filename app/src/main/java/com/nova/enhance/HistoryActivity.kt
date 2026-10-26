package com.nova.enhance

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.widget.LinearLayout
import android.widget.ScrollView
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** The "Enhancement" project: every conversation, per AI, searchable, exportable. */
class HistoryActivity : Activity() {
    private val db by lazy { Db.get(this) }
    private var filter: String? = null
    private lateinit var list: LinearLayout
    private lateinit var chips: LinearLayout
    private lateinit var search: android.widget.EditText
    private val fmt = SimpleDateFormat("d MMM yyyy · HH:mm", Locale.US)

    companion object { const val EXPORT = 41 }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        val root = Ui.col(c, 18)
        val head = Ui.row(c)
        val t = Ui.col(c)
        t.addView(Ui.title(c, Db.PROJECT))
        t.addView(Ui.text(c, "All saved conversations", 13f, Ui.MUTED))
        head.addView(t, Ui.lp(0, weight = 1f))
        head.addView(Ui.button(c, "Export") { export() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(head)

        search = Ui.input(c, "Search conversations", false)
        root.addView(search, Ui.lp(top = Ui.dp(c, 12)))
        search.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, d: Int) {}
            override fun afterTextChanged(s: Editable?) { load() }
        })

        val cr = Ui.chipRow(c); chips = cr.second
        root.addView(cr.first, Ui.lp(top = Ui.dp(c, 12)))

        list = Ui.col(c)
        root.addView(list, Ui.lp(top = Ui.dp(c, 6)))
        root.addView(Ui.signature(c))
        setContentView(ScrollView(c).apply { addView(root) })
    }

    override fun onResume() { super.onResume(); drawChips(); load() }

    private fun drawChips() {
        chips.removeAllViews()
        val opts = listOf<Pair<String?, String>>(null to "All") + Providers.all.map { it.id to it.label }
        for ((id, label) in opts) {
            val n = db.list(id, "").size
            chips.addView(Ui.chip(this, "$label  $n", filter == id) { filter = id; drawChips(); load() },
                Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(this, 8)))
        }
    }

    private fun load() {
        val c = this
        list.removeAllViews()
        val items = db.list(filter, search.text.toString().trim())
        if (items.isEmpty()) {
            list.addView(Ui.text(c, "Nothing here yet. Enhanced texts appear here automatically.", 14f, Ui.MUTED)
                .apply { setPadding(0, Ui.dp(c, 20), 0, 0) })
        }
        for (cv in items) {
            val p = Providers.byId(cv.provider)
            val card = Ui.card(c)
            card.addView(Ui.text(c, "${p?.label ?: cv.provider} / ${Db.PROJECT}", 12f, Ui.ACCENT, true))
            card.addView(Ui.text(c, cv.title, 15f, Ui.TEXT).apply { maxLines = 2 })
            val via = if (cv.route == "account") " · sent to my account" else ""
            card.addView(Ui.text(c, "${Modes.byId(cv.mode).label} · ${fmt.format(Date(cv.updated))}$via", 12f, Ui.MUTED))
            card.setOnClickListener { ChatActivity.open(c, cv.id) }
            card.setOnLongClickListener {
                EnhancePanel.confirm(c, "Delete this conversation?", "Delete") { db.delete(cv.id); drawChips(); load() }
                true
            }
            list.addView(card, Ui.lp(top = Ui.dp(c, 10)))
        }
    }

    /** Save the whole project as a Markdown file — pick Google Drive, Downloads or any folder. */
    private fun export() {
        val name = "Nova-Enhance-Enhancement-" + SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date()) + ".md"
        startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE); type = "text/markdown"
            putExtra(Intent.EXTRA_TITLE, name)
        }, EXPORT)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val uri = data?.data
        if (requestCode == EXPORT && resultCode == RESULT_OK && uri != null) {
            try {
                contentResolver.openOutputStream(uri)?.use { it.write(db.exportMarkdown().toByteArray()) }
                Ui.toast(this, "Exported")
            } catch (e: Exception) { Ui.toast(this, "Export failed: ${e.message}") }
        }
    }
}
