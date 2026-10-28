package com.nova.enhance

import android.app.Activity
import android.app.AlertDialog
import android.os.Bundle

/** Small window from the Nova panel: type any language to translate into. */
class LangActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val store = Store(this)
        val box = Ui.input(this, "e.g. Japanese, Dutch, Greek…", false).apply {
            if (Modes.MAIN_LANGS.none { it.first.equals(store.trLang, true) } &&
                Modes.OTHER_LANGS.none { it.first.equals(store.trLang, true) }) setText(store.trLang)
        }
        val wrap = Ui.col(this, 18).apply { addView(box) }
        AlertDialog.Builder(this)
            .setTitle("🌐 Translate to any language")
            .setMessage("Then go back to your message, switch to Nova with the 🌐 key and tap Translate.")
            .setView(wrap)
            .setPositiveButton("Save") { _, _ ->
                box.text.toString().trim().takeIf { it.isNotBlank() }?.let {
                    store.trLang = it
                    Ui.toast(this, "Translate → $it")
                }
            }
            .setNegativeButton("Cancel", null)
            .setOnDismissListener { finish() }
            .show()
    }
}
