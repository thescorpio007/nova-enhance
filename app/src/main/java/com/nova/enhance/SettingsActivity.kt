package com.nova.enhance

import android.Manifest
import android.app.AlertDialog
import android.app.StatusBarManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Settings, split into sections. With no "section" extra it shows the menu;
 * each row opens the same screen with that section.
 */
class SettingsActivity : BaseActivity() {
    companion object {
        const val S_AI = "ai"
        const val S_ACCOUNTS = "accounts"
        const val S_CAPTURE = "capture"
        const val S_ENHANCE = "enhance"
        const val S_BACKUP = "backup"
        const val S_ERRORS = "errors"
        const val S_ABOUT = "about"
        private const val REQ_BACKUP = 51
        private const val REQ_RESTORE = 52

        fun open(c: Context, section: String? = null) =
            c.startActivity(Intent(c, SettingsActivity::class.java).putExtra("section", section))
    }

    private lateinit var store: Store
    private lateinit var root: LinearLayout
    private var section: String? = null
    private var pendingPassword = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = Store(this)
        section = intent.getStringExtra("section")
        root = Ui.col(this, 18)
        setContentView(ScrollView(this).apply { addView(root) })
    }

    override fun onResume() { super.onResume(); draw() }

    private fun draw() {
        root.removeAllViews()
        when (section) {
            null -> menu()
            S_AI -> aiSection()
            S_ACCOUNTS -> accountsSection()
            S_CAPTURE -> captureSection()
            S_ENHANCE -> enhanceSection()
            S_BACKUP -> backupSection()
            S_ERRORS -> errorsSection()
            S_ABOUT -> aboutSection()
        }
        root.addView(Ui.signature(this))
    }

    // ==================================================================== menu

    private fun menu() {
        val c = this
        root.addView(Ui.title(c, "Settings"))
        val keyed = Providers.all.count { store.hasKey(it) }
        val apps = Providers.all.count { AccountSend.installed(c, it) }
        val errs = ErrorLog.count(c)
        row("🔑", "AI & API keys", "$keyed of ${Providers.all.size} connected · models · compare", S_AI)
        row("👤", "Accounts", "Send to the official AI apps · $apps installed", S_ACCOUNTS)
        row("✂", "Capture", "Share menu, text selection, bubble, tile", S_CAPTURE)
        row("✦", "Enhancement", "About me, default route, speaking language, custom instruction", S_ENHANCE)
        row("💾", "Backup & restore", "All settings, history and password-protected keys", S_BACKUP)
        row("⚠", "Error log", if (errs == 0) "No errors recorded" else "$errs record(s) · share with support", S_ERRORS)
        row("ⓘ", "About", "Version ${version()}", S_ABOUT)
    }

    private fun row(icon: String, title: String, sub: String, target: String) {
        val c = this
        val card = Ui.row(c).apply {
            background = Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat())
            setPadding(Ui.dp(c, 14), Ui.dp(c, 14), Ui.dp(c, 14), Ui.dp(c, 14))
            setOnClickListener { open(c, target) }
        }
        card.addView(Ui.text(c, icon, 18f, Ui.ACCENT, true).apply {
            gravity = Gravity.CENTER
            background = Ui.round(Ui.CARD2, Ui.dp(c, 12).toFloat())
        }, LinearLayout.LayoutParams(Ui.dp(c, 42), Ui.dp(c, 42)).apply { marginEnd = Ui.dp(c, 14) })
        val t = Ui.col(c)
        t.addView(Ui.text(c, title, 16f, Ui.TEXT, true))
        t.addView(Ui.text(c, sub, 13f, Ui.MUTED))
        card.addView(t, Ui.lp(0, weight = 1f))
        card.addView(Ui.text(c, "›", 22f, Ui.MUTED))
        root.addView(card, Ui.lp(top = Ui.dp(c, 10)))
    }

    private fun header(title: String, sub: String) {
        val c = this
        val h = Ui.row(c)
        h.addView(Ui.text(c, "←", 22f, Ui.MUTED).apply {
            setPadding(0, 0, Ui.dp(c, 14), 0); setOnClickListener { finish() }
        })
        h.addView(Ui.title(c, title))
        root.addView(h)
        root.addView(Ui.text(c, sub, 13f, Ui.MUTED).apply { setPadding(0, Ui.dp(c, 2), 0, Ui.dp(c, 6)) })
    }

    // ==================================================================== AI & API keys

    private fun aiSection() {
        header("AI & API keys", "The main engine. Answers are saved in the app's \"${Db.PROJECT}\" project, " +
            "one thread per AI. Keys stay on this phone and are only sent to their own AI.")
        for (p in Providers.all) root.addView(keyCard(p), Ui.lp(top = Ui.dp(this, 10)))
    }

    private fun keyCard(p: Provider): LinearLayout {
        val c = this
        val card = Ui.card(c)
        val top = Ui.row(c)
        top.addView(Ui.text(c, p.label, 16f, Ui.TEXT, true), Ui.lp(0, weight = 1f))
        top.addView(Ui.text(c, "Get a key ↗", 13f, Ui.ACCENT, true).apply { setOnClickListener { openUrl(p.keyPage) } })
        card.addView(top)
        val status = Ui.text(c, statusLine(p), 13f, if (store.hasKey(p)) Ui.GOOD else Ui.MUTED)
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
                r.exceptionOrNull()?.let {
                    ErrorLog.add(c, "Save & test", p.label, store.model(p), (it as? AiException)?.code ?: 0, it.message ?: it.toString())
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
                }.onFailure {
                    ErrorLog.add(this, "Model list", p.label, code = (it as? AiException)?.code ?: 0, message = it.message ?: "")
                    status.text = it.message; status.setTextColor(Ui.BAD)
                }
            }
        }.start()
    }

    // ==================================================================== accounts

    private fun accountsSection() {
        val c = this
        header("Accounts", "\"My account\" hands your text to the official AI app, so the chat lands in your own " +
            "signed-in account (your Pro plan). Nova Enhance never asks for your passwords.")
        val tip = Ui.card(c)
        tip.addView(Ui.text(c, "One-time setup in each app", 15f, Ui.TEXT, true))
        tip.addView(Ui.text(c, "Create a project named \"${Db.PROJECT}\" in the AI app (Gemini: a Gem; DeepSeek has no projects) " +
            "and open it before sharing, so the chats are kept there.", 13f, Ui.MUTED))
        root.addView(tip, Ui.lp(top = Ui.dp(c, 6)))
        for (p in Providers.all) {
            val card = Ui.card(c)
            val inst = AccountSend.installed(c, p)
            card.addView(Ui.text(c, "My ${p.label}", 16f, Ui.TEXT, true))
            card.addView(Ui.text(c, if (inst) "✓ ${p.label} app installed" else "App not installed — the browser will be used",
                13f, if (inst) Ui.GOOD else Ui.MUTED))
            val row = Ui.row(c)
            row.addView(Ui.button(c, if (inst) "Open ${p.label} app" else "Install ${p.label} app", !inst) {
                if (inst) AccountSend.openApp(c, p) else AccountSend.openStore(c, p)
            }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
            row.addView(Ui.button(c, "Copy \"${Db.PROJECT}\"") { Ui.copy(c, Db.PROJECT) },
                Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
            card.addView(row, Ui.lp(top = Ui.dp(c, 8)))
            root.addView(card, Ui.lp(top = Ui.dp(c, 10)))
        }
    }

    // ==================================================================== capture

    private fun captureSection() {
        val c = this
        header("Capture", "Ways to send text from any app to Nova Enhance.")
        val cap = Ui.card(c)
        cap.addView(Ui.text(c, "Text selection & Share menu", 15f, Ui.TEXT, true))
        cap.addView(Ui.text(c, "Always on: select text ▸ ⋮ ▸ ✦ Enhance (enhances at once, then Replace), " +
            "or Share ▸ Nova Enhance. Tip: long-press Nova Enhance in the Share list ▸ Pin.", 13f, Ui.MUTED))

        cap.addView(Ui.text(c, "Floating bubble", 15f, Ui.TEXT, true).apply { setPadding(0, Ui.dp(c, 16), 0, 0) })
        cap.addView(Ui.text(c, "Copy text, then tap the bubble.", 13f, Ui.MUTED))
        cap.addView(Ui.button(c, if (store.bubbleOn) "Bubble is ON — turn off" else "Turn bubble ON", !store.bubbleOn) {
            toggleBubble()
        }, Ui.lp(top = Ui.dp(c, 8)))

        cap.addView(Ui.text(c, "Quick-settings tile", 15f, Ui.TEXT, true).apply { setPadding(0, Ui.dp(c, 16), 0, 0) })
        cap.addView(Ui.text(c, "Copy text, swipe down, tap \"Enhance copied text\".", 13f, Ui.MUTED))
        cap.addView(Ui.button(c, "Add the tile") { addTile() }, Ui.lp(top = Ui.dp(c, 8)))
        root.addView(cap, Ui.lp(top = Ui.dp(c, 6)))
    }

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

    // ==================================================================== enhancement defaults

    private fun enhanceSection() {
        val c = this
        header("Enhancement", "Defaults used on the main screen and the quick sheet.")

        root.addView(Ui.section(c, "About me"))
        val me = Ui.card(c)
        me.addView(Ui.text(c, Profile.summary(store), 14f, if (Profile.isSet(store)) Ui.TEXT else Ui.MUTED))
        if (store.profileTerms.isNotBlank()) me.addView(Ui.text(c, "Keep as written: ${store.profileTerms}", 12f, Ui.MUTED)
            .apply { setPadding(0, Ui.dp(c, 6), 0, 0) })
        me.addView(Ui.button(c, if (Profile.isSet(store)) "Edit About me" else "Set up About me", !Profile.isSet(store)) {
            Profile.edit(c) { draw() }
        }, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(me)

        root.addView(Ui.section(c, "Send through"))
        val (rs, rr) = Ui.chipRow(c)
        for ((id, label) in listOf("api" to "API key", "account" to "My account", "both" to "Both")) {
            rr.addView(Ui.chip(c, label, store.route == id) { store.route = id; draw() },
                Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        }
        root.addView(rs)

        root.addView(Ui.section(c, "Speaking language (🎤)"))
        val (ls, lr) = Ui.chipRow(c)
        for ((id, label) in listOf("phone" to "Phone default", "en" to "English", "ar" to "العربية", "fr" to "Français", "de" to "Deutsch")) {
            lr.addView(Ui.chip(c, label, store.voiceLang == id) { store.voiceLang = id; draw() },
                Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        }
        root.addView(ls)

        root.addView(Ui.section(c, "Custom instruction"))
        val box = Ui.input(c, "e.g. \"Make it friendlier and add a polite closing\"", true, 2).apply { setText(store.custom) }
        root.addView(box)
        root.addView(Ui.button(c, "Save instruction", true) { store.custom = box.text.toString(); Ui.toast(c, "Saved") },
            Ui.lp(top = Ui.dp(c, 8)))
    }

    // ==================================================================== backup & restore

    private fun backupSection() {
        val c = this
        header("Backup & restore", "One file with all your settings, models, the ${Db.PROJECT} history and your " +
            "API keys locked with a password. Save it to Google Drive or any folder.")
        val card = Ui.card(c)
        card.addView(Ui.text(c, "Back up everything", 15f, Ui.TEXT, true))
        card.addView(Ui.text(c, "You choose a password for the API keys. Without it, the keys can't be restored " +
            "(everything else still can).", 13f, Ui.MUTED))
        card.addView(Ui.button(c, "💾  Create backup file", true) {
            askPassword("Password for your API keys", "At least 6 characters. Keep it safe.", confirm = true) { pw ->
                pendingPassword = pw
                val name = "Nova-Enhance-backup-" + SimpleDateFormat("yyyy-MM-dd", Locale.US).format(Date()) + ".json"
                startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
                    addCategory(Intent.CATEGORY_OPENABLE); type = "application/json"; putExtra(Intent.EXTRA_TITLE, name)
                }, REQ_BACKUP)
            }
        }, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(card, Ui.lp(top = Ui.dp(c, 6)))

        val card2 = Ui.card(c)
        card2.addView(Ui.text(c, "Restore", 15f, Ui.TEXT, true))
        card2.addView(Ui.text(c, "Loads a backup file: settings are replaced, conversations are added " +
            "(ones already here are skipped).", 13f, Ui.MUTED))
        card2.addView(Ui.button(c, "Restore from file") {
            startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
                addCategory(Intent.CATEGORY_OPENABLE); type = "*/*"
            }, REQ_RESTORE)
        }, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(card2, Ui.lp(top = Ui.dp(c, 10)))

        val card3 = Ui.card(c)
        card3.addView(Ui.text(c, "Readable copy of the history", 15f, Ui.TEXT, true))
        card3.addView(Ui.text(c, "All conversations as a Markdown document (for reading, not for restoring).", 13f, Ui.MUTED))
        card3.addView(Ui.button(c, "Export history (Markdown)") {
            startActivity(Intent(c, HistoryActivity::class.java)); Ui.toast(c, "Tap Export at the top")
        }, Ui.lp(top = Ui.dp(c, 10)))
        root.addView(card3, Ui.lp(top = Ui.dp(c, 10)))
    }

    private fun askPassword(title: String, msg: String, confirm: Boolean, done: (String) -> Unit) {
        val c = this
        val box = Ui.col(c, 4)
        fun field(h: String) = EditText(c).apply {
            hint = h; inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        val p1 = field("Password"); box.addView(p1)
        val p2 = field("Repeat password"); if (confirm) box.addView(p2)
        AlertDialog.Builder(c).setTitle(title).setMessage(msg).setView(box)
            .setPositiveButton("OK") { _, _ ->
                val a = p1.text.toString()
                when {
                    confirm && a.length < 6 -> Ui.toast(c, "Use at least 6 characters")
                    confirm && a != p2.text.toString() -> Ui.toast(c, "The passwords don't match")
                    else -> done(a)
                }
            }.setNegativeButton("Cancel", null).show()
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == Profile.VOICE_REQ) { if (resultCode == RESULT_OK) Profile.onVoice(data); return }
        val uri = data?.data ?: return
        if (resultCode != RESULT_OK) return
        when (requestCode) {
            REQ_BACKUP -> {
                val pw = pendingPassword; pendingPassword = ""
                Thread {
                    val r = runCatching {
                        val txt = Backup.build(this, pw)
                        contentResolver.openOutputStream(uri)?.use { it.write(txt.toByteArray()) } ?: error("Couldn't write the file")
                    }
                    r.exceptionOrNull()?.let { ErrorLog.add(this, "Backup", message = it.toString()) }
                    runOnUiThread { Ui.toast(this, if (r.isSuccess) "Backup saved" else "Backup failed: ${r.exceptionOrNull()?.message}") }
                }.start()
            }
            REQ_RESTORE -> {
                val txt = try { contentResolver.openInputStream(uri)?.use { it.readBytes().toString(Charsets.UTF_8) } }
                    catch (e: Exception) { null }
                if (txt == null) { Ui.toast(this, "Couldn't read the file"); return }
                askPassword("Backup password", "Needed to restore the API keys. Leave empty to restore without keys.", confirm = false) { pw ->
                    val r = runCatching { Backup.restore(this, txt, pw) }
                    r.exceptionOrNull()?.let { ErrorLog.add(this, "Restore", message = it.message ?: it.toString()) }
                    AlertDialog.Builder(this).setTitle(if (r.isSuccess) "Restored" else "Restore failed")
                        .setMessage(r.getOrNull() ?: r.exceptionOrNull()?.message).setPositiveButton("OK", null).show()
                }
            }
        }
    }

    // ==================================================================== error log

    private fun errorsSection() {
        val c = this
        header("Error log", "Every error is recorded here (last 200), with the first 100 characters of the text. " +
            "API keys are never included. Check it contains nothing private before sharing.")
        val row = Ui.row(c)
        row.addView(Ui.button(c, "Share log", true) {
            startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"; putExtra(Intent.EXTRA_SUBJECT, "Nova Enhance error log")
                putExtra(Intent.EXTRA_TEXT, ErrorLog.report(c))
            }, "Share error log"))
        }, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        row.addView(Ui.button(c, "Copy") { Ui.copy(c, ErrorLog.report(c)) }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        row.addView(Ui.button(c, "Clear") {
            EnhancePanel.confirm(c, "Delete all error records?", "Clear") { ErrorLog.clear(c); draw() }
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(row, Ui.lp(top = Ui.dp(c, 6)))

        val arr = ErrorLog.read(c)
        if (arr.length() == 0) {
            root.addView(Ui.text(c, "No errors recorded. 👍", 14f, Ui.MUTED).apply { setPadding(0, Ui.dp(c, 20), 0, 0) })
            return
        }
        val fmt = SimpleDateFormat("d MMM · HH:mm:ss", Locale.US)
        for (i in arr.length() - 1 downTo 0) {
            val o = arr.getJSONObject(i)
            val card = Ui.card(c)
            val head = listOf(o.optString("where"), o.optString("ai"), o.optString("model"),
                if (o.optInt("code") != 0) "code ${o.optInt("code")}" else "").filter { it.isNotBlank() }.joinToString(" · ")
            card.addView(Ui.text(c, head, 14f, if (o.optString("where") == "CRASH") Ui.BAD else Ui.TEXT, true))
            card.addView(Ui.text(c, fmt.format(Date(o.optLong("t"))), 12f, Ui.MUTED))
            card.addView(Ui.text(c, o.optString("msg"), 13f, Ui.BAD).apply { setTextIsSelectable(true); maxLines = 8 },
                Ui.lp(top = Ui.dp(c, 6)))
            if (o.optString("text").isNotBlank())
                card.addView(Ui.text(c, "Text: \"${o.optString("text")}\"", 12f, Ui.MUTED), Ui.lp(top = Ui.dp(c, 4)))
            root.addView(card, Ui.lp(top = Ui.dp(c, 10)))
        }
    }

    // ==================================================================== about

    private fun aboutSection() {
        val c = this
        header("About", "")
        val card = Ui.card(c)
        card.addView(Ui.text(c, "Nova Enhance", 18f, Ui.TEXT, true))
        card.addView(Ui.text(c, "Version ${version()}", 14f, Ui.MUTED))
        card.addView(Ui.text(c, "AI text enhancement with Gemini, ChatGPT, Claude, DeepSeek and Grok. " +
            "Conversations are kept in the \"${Db.PROJECT}\" project on this phone.", 14f, Ui.TEXT)
            .apply { setPadding(0, Ui.dp(c, 10), 0, 0) })
        card.addView(Ui.text(c, "Source code: github.com/thescorpio007/nova-enhance", 13f, Ui.ACCENT).apply {
            setPadding(0, Ui.dp(c, 10), 0, 0); setOnClickListener { openUrl("https://github.com/thescorpio007/nova-enhance") }
        })
        root.addView(card, Ui.lp(top = Ui.dp(c, 6)))
    }

    private fun version(): String = try { packageManager.getPackageInfo(packageName, 0).versionName ?: "" } catch (_: Exception) { "" }

    private fun openUrl(u: String) {
        try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(u))) } catch (_: Exception) { Ui.toast(this, u) }
    }
}
