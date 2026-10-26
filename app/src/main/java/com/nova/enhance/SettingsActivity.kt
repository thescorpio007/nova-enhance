package com.nova.enhance

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.app.StatusBarManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class SettingsActivity : BaseActivity() {
    private lateinit var store: Store
    private lateinit var root: LinearLayout
    private val statusViews = mutableMapOf<String, TextView>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        root = Ui.col(this, 18)
        setContentView(ScrollView(this).apply { addView(root) })
    }

    override fun onResume() { super.onResume(); draw() }

    private fun draw() {
        val c = this
        root.removeAllViews(); statusViews.clear()
        root.addView(Ui.title(c, "Settings"))

        // ---------------------------------------------------------------- API keys
        root.addView(Ui.section(c, "1 · API keys (main engine)"))
        root.addView(Ui.text(c, "Each answer is saved in the app's \"${Db.PROJECT}\" project, one thread per AI. " +
            "Keys stay on this phone and are only sent to their own AI.", 13f, Ui.MUTED))
        for (p in Providers.all) root.addView(keyCard(p), Ui.lp(top = Ui.dp(c, 10)))

        // ---------------------------------------------------------------- accounts
        root.addView(Ui.section(c, "2 · My accounts (signed in)"))
        root.addView(Ui.text(c, "Sign in once inside the app. Create a project named \"${Db.PROJECT}\" in that AI " +
            "(the name is copied for you), open it, then tap \"Use this page\". Texts you send through " +
            "\"My account\" land there, in your real account.", 13f, Ui.MUTED))
        for (p in Providers.all) root.addView(accountCard(p), Ui.lp(top = Ui.dp(c, 10)))

        // ---------------------------------------------------------------- capture
        root.addView(Ui.section(c, "3 · Capture text from anywhere"))
        val cap = Ui.card(c)
        cap.addView(Ui.text(c, "Share menu & text selection", 15f, Ui.TEXT, true))
        cap.addView(Ui.text(c, "Already on: select text in any app ▸ Share or ⋮ ▸ Nova Enhance.", 13f, Ui.MUTED))

        cap.addView(Ui.text(c, "Floating bubble", 15f, Ui.TEXT, true).apply { setPadding(0, Ui.dp(c, 14), 0, 0) })
        cap.addView(Ui.text(c, "Copy text, then tap the bubble.", 13f, Ui.MUTED))
        cap.addView(Ui.button(c, if (store.bubbleOn) "Bubble is ON — turn off" else "Turn bubble ON", !store.bubbleOn) {
            toggleBubble()
        }, Ui.lp(top = Ui.dp(c, 8)))

        cap.addView(Ui.text(c, "Quick-settings tile", 15f, Ui.TEXT, true).apply { setPadding(0, Ui.dp(c, 14), 0, 0) })
        cap.addView(Ui.text(c, "Copy text, swipe down, tap \"Enhance copied text\".", 13f, Ui.MUTED))
        cap.addView(Ui.button(c, "Add the tile") { addTile() }, Ui.lp(top = Ui.dp(c, 8)))
        root.addView(cap, Ui.lp(top = Ui.dp(c, 6)))

        // ---------------------------------------------------------------- backup
        root.addView(Ui.section(c, "4 · Backup"))
        root.addView(Ui.button(c, "Export the Enhancement project (Google Drive or any folder)") {
            startActivity(Intent(c, HistoryActivity::class.java))
            Ui.toast(c, "Tap Export at the top")
        })

        root.addView(Ui.signature(c))
    }

    // -------------------------------------------------------------------- key card

    private fun keyCard(p: Provider): LinearLayout {
        val c = this
        val card = Ui.card(c)
        val top = Ui.row(c)
        top.addView(Ui.text(c, p.label, 16f, Ui.TEXT, true), Ui.lp(0, weight = 1f))
        top.addView(Ui.text(c, "Get a key ↗", 13f, Ui.ACCENT, true).apply {
            setOnClickListener { openUrl(p.keyPage) }
        })
        card.addView(top)
        val status = Ui.text(c, statusLine(p), 13f, if (store.hasKey(p)) Ui.GOOD else Ui.MUTED)
        statusViews[p.id] = status
        card.addView(status)

        val box = Ui.input(c, "Paste API key", false).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setText(store.key(p))
        }
        card.addView(box, Ui.lp(top = Ui.dp(c, 8)))

        val row = Ui.row(c)
        row.addView(Ui.button(c, "Save & test", true) {
            store.setKey(p, box.text.toString())
            if (!store.hasKey(p)) { status.text = statusLine(p); status.setTextColor(Ui.MUTED); return@button }
            status.text = "Checking key and choosing a model…"; status.setTextColor(Ui.MUTED)
            Thread {
                val r = runCatching {
                    val ids = Providers.listModels(p, store.key(p))
                    val m = Providers.pickModel(p, ids)
                    store.setModel(p, m)
                    Providers.chat(p, store.key(p), m, Modes.SYSTEM, listOf(Msg("user", "Instruction: Fix grammar.\n\nText:\nhello world")))
                    m
                }
                runOnUiThread {
                    r.onSuccess { status.text = "✓ Connected · model $it"; status.setTextColor(Ui.GOOD) }
                        .onFailure { status.text = it.message ?: it.toString(); status.setTextColor(Ui.BAD) }
                }
            }.start()
        }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        row.addView(Ui.button(c, "Model") { chooseModel(p, status) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        card.addView(row, Ui.lp(top = Ui.dp(c, 8)))
        card.addView(Ui.button(c, "⭐  Compare ${p.label} models") {
            if (store.hasKey(p)) CompareActivity.open(c, p) else Ui.toast(c, "Save a key first")
        }, Ui.lp(top = Ui.dp(c, 8)))
        return card
    }

    private fun statusLine(p: Provider) =
        if (store.hasKey(p)) "Key saved · model ${store.model(p)}" else "No key"

    private fun chooseModel(p: Provider, status: TextView) {
        if (!store.hasKey(p)) { Ui.toast(this, "Save a key first"); return }
        status.text = "Loading models…"
        Thread {
            val r = runCatching { Providers.listModels(p, store.key(p)) }
            runOnUiThread {
                r.onSuccess { ids ->
                    if (ids.isEmpty()) { status.text = "No models found"; return@onSuccess }
                    val arr = ids.toTypedArray()
                    AlertDialog.Builder(this).setTitle("${p.label} model")
                        .setSingleChoiceItems(arr, ids.indexOf(store.model(p))) { d, w ->
                            store.setModel(p, arr[w]); status.text = statusLine(p); status.setTextColor(Ui.GOOD); d.dismiss()
                        }.setNegativeButton("Cancel") { _, _ -> status.text = statusLine(p) }.show()
                }.onFailure { status.text = it.message; status.setTextColor(Ui.BAD) }
            }
        }.start()
    }

    // -------------------------------------------------------------------- account card

    private fun accountCard(p: Provider): LinearLayout {
        val c = this
        val card = Ui.card(c)
        card.addView(Ui.text(c, "My ${p.label} account", 16f, Ui.TEXT, true))
        val web = store.webProject(p)
        card.addView(Ui.text(c,
            if (web.isNotBlank()) "✓ Enhancement page set" else "Not set up yet",
            13f, if (web.isNotBlank()) Ui.GOOD else Ui.MUTED))
        if (p.id == "gemini") card.addView(Ui.text(c,
            "Google may block signing in inside other apps. If it does, use the API key for Gemini.", 12f, Ui.MUTED))
        if (p.id == "deepseek") card.addView(Ui.text(c,
            "DeepSeek has no projects: chats go to your normal chat list.", 12f, Ui.MUTED))
        val row = Ui.row(c)
        row.addView(Ui.button(c, if (web.isBlank()) "Sign in & set up" else "Open", web.isBlank()) {
            WebAccountActivity.setup(c, p)
        }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        if (web.isNotBlank()) row.addView(Ui.button(c, "Reset") {
            store.setWebProject(p, ""); draw()
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        card.addView(row, Ui.lp(top = Ui.dp(c, 8)))
        return card
    }

    // -------------------------------------------------------------------- capture

    private fun toggleBubble() {
        if (store.bubbleOn) {
            store.bubbleOn = false
            stopService(Intent(this, BubbleService::class.java)); draw(); return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && !Settings.canDrawOverlays(this)) {
            AlertDialog.Builder(this)
                .setMessage("Allow \"Display over other apps\" for Nova Enhance, then come back and tap again.")
                .setPositiveButton("Open setting") { _, _ ->
                    startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
                }.setNegativeButton("Cancel", null).show()
            return
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 7)
        }
        store.bubbleOn = true
        BubbleService.start(this); draw()
    }

    private fun addTile() {
        if (Build.VERSION.SDK_INT >= 33) {
            val sbm = getSystemService(StatusBarManager::class.java)
            sbm.requestAddTileService(ComponentName(this, EnhanceTile::class.java), "Enhance copied text",
                Icon.createWithResource(this, R.drawable.ic_tile), mainExecutor) { }
        } else {
            AlertDialog.Builder(this).setMessage(
                "Swipe down twice from the top ▸ tap the pencil (Edit) ▸ drag \"Enhance copied text\" into your tiles.")
                .setPositiveButton("OK", null).show()
        }
    }

    private fun openUrl(u: String) {
        try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(u))) } catch (_: Exception) { Ui.toast(this, u) }
    }
}
