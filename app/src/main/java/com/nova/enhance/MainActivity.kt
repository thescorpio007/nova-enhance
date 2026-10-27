package com.nova.enhance

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.widget.ScrollView

class MainActivity : BaseActivity() {
    private lateinit var panel: EnhancePanel
    private lateinit var setupCard: android.widget.LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        val root = Ui.col(c, 16).apply { setPadding(Ui.dp(c, 16), Ui.dp(c, 6), Ui.dp(c, 16), Ui.dp(c, 8)) }

        val head = Ui.row(c)
        val titles = Ui.col(c)
        titles.addView(Ui.title(c, "Nova Enhance").apply { textSize = 20f; maxLines = 1; isSingleLine = true; setPadding(0, 0, 0, 0) })
        titles.addView(Ui.text(c, "Project: ${Db.PROJECT}", 12f, Ui.MUTED))
        head.addView(titles, Ui.lp(0, weight = 1f))
        head.addView(Ui.smallButton(c, "History") { startActivity(Intent(c, HistoryActivity::class.java)) },
            Ui.lp(android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32), end = Ui.dp(c, 8)))
        head.addView(Ui.smallButton(c, "⚙") { startActivity(Intent(c, SettingsActivity::class.java)) },
            Ui.lp(android.widget.LinearLayout.LayoutParams.WRAP_CONTENT, Ui.dp(c, 32)))
        root.addView(head)

        setupCard = Ui.card(c)
        setupCard.addView(Ui.text(c, "Connect your AI", 16f, Ui.TEXT, true))
        setupCard.addView(Ui.text(c, "Add an API key for ChatGPT, Claude, Gemini, DeepSeek or Grok, and/or sign in to your accounts.", 14f, Ui.MUTED))
        setupCard.addView(Ui.button(c, "Open Settings", true) {
            startActivity(Intent(c, SettingsActivity::class.java))
        }, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(setupCard, Ui.lp(top = Ui.dp(c, 14)))

        panel = EnhancePanel(this)
        root.addView(panel)

        root.addView(Ui.signature(c).apply { text = "${Ui.SIGNATURE} · ${appVersion()}"; textSize = 11f })

        setContentView(ScrollView(c).apply { isFillViewport = true; addView(root) })
        intent?.getStringExtra("text")?.let { panel.setText(it) }
        // First start: ask about the user once.
        if (!Store(c).profileAsked) root.post { Profile.edit(this) { panel.refresh() } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra("text")?.let { panel.setText(it) }
    }

    override fun onResume() {
        super.onResume()
        val s = Store(this)
        val ready = Providers.all.any { s.hasKey(it) || AccountSend.installed(this, it) }
        setupCard.visibility = if (ready) android.view.View.GONE else android.view.View.VISIBLE
        panel.refresh()
    }

    private fun appVersion(): String = try {
        packageManager.getPackageInfo(packageName, 0).versionName ?: ""
    } catch (_: Exception) { "" }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == EnhancePanel.VOICE_REQ && resultCode == RESULT_OK) panel.onVoice(data)
        if (requestCode == Profile.VOICE_REQ && resultCode == RESULT_OK) Profile.onVoice(data)
    }
}
