package com.nova.enhance

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.widget.FrameLayout
import android.widget.ScrollView

/**
 * Quick sheet over any app. Opened by:
 *  - Share  ▸ Nova Enhance            (ACTION_SEND)
 *  - text selection ▸ Nova Enhance    (ACTION_PROCESS_TEXT, can replace the text in place)
 *  - the floating bubble / the quick-settings tile (reads the copied text)
 */
class CaptureActivity : BaseActivity() {
    companion object { const val ACTION_CLIPBOARD = "com.nova.enhance.CLIPBOARD" }

    private lateinit var panel: EnhancePanel
    private var wantClipboard = false
    private var readOnly = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        val i = intent
        val isProcess = i?.action == Intent.ACTION_PROCESS_TEXT
        readOnly = !isProcess || i?.getBooleanExtra(Intent.EXTRA_PROCESS_TEXT_READONLY, false) == true

        panel = EnhancePanel(this, if (readOnly) null else { out: String ->
            setResult(RESULT_OK, Intent().putExtra(Intent.EXTRA_PROCESS_TEXT, out))
            finish()
        })

        val sheet = Ui.col(c, 18)
        sheet.background = android.graphics.drawable.GradientDrawable().apply {
            setColor(Ui.BG)
            val r = Ui.dp(c, 22).toFloat()
            cornerRadii = floatArrayOf(r, r, r, r, 0f, 0f, 0f, 0f)
        }
        sheet.isClickable = true

        val head = Ui.row(c)
        head.addView(Ui.text(c, "✦ Nova Enhance", 18f, Ui.TEXT, true), Ui.lp(0, weight = 1f))
        head.addView(Ui.text(c, "Open app", 14f, Ui.ACCENT, true).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 6), Ui.dp(c, 10), Ui.dp(c, 6))
            setOnClickListener {
                startActivity(Intent(c, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
                    .putExtra("text", panel.input.text.toString()))
                finish()
            }
        })
        head.addView(Ui.text(c, "✕", 20f, Ui.MUTED).apply {
            setPadding(Ui.dp(c, 10), Ui.dp(c, 4), Ui.dp(c, 4), Ui.dp(c, 4))
            setOnClickListener { finish() }
        })
        sheet.addView(head)
        sheet.addView(panel)
        sheet.addView(Ui.signature(c))

        val scroll = ScrollView(c).apply { addView(sheet) }
        val root = FrameLayout(c).apply { setOnClickListener { finish() } }
        root.addView(scroll, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM).apply {
            topMargin = Ui.dp(c, 48)
        })
        setContentView(root)

        when (i?.action) {
            Intent.ACTION_SEND -> i.getStringExtra(Intent.EXTRA_TEXT)?.let { panel.setText(it, auto = true) }
            // From the text-selection menu: enhance straight away with the last mode; Replace puts it back.
            Intent.ACTION_PROCESS_TEXT -> i.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.let {
                panel.setText(it.toString(), auto = true)
            }
            ACTION_CLIPBOARD -> wantClipboard = true
        }
    }

    // Android only lets an app read the clipboard while its window has focus.
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && wantClipboard) {
            wantClipboard = false
            panel.pasteClipboard()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == EnhancePanel.VOICE_REQ && resultCode == RESULT_OK) panel.onVoice(data)
        if (requestCode == Profile.VOICE_REQ && resultCode == RESULT_OK) Profile.onVoice(data)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == EnhancePanel.MIC_REQ)
            panel.onMicPermission(grantResults.isNotEmpty() && grantResults[0] == android.content.pm.PackageManager.PERMISSION_GRANTED)
    }

    override fun onPause() { super.onPause(); panel.stopVoice() }
}
