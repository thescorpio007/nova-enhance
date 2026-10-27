package com.nova.enhance

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView

/** One saved conversation; you can keep asking the same AI for changes. */
class ChatActivity : BaseActivity() {
    companion object {
        fun open(c: Context, id: Long) {
            if (id < 0) return
            c.startActivity(Intent(c, ChatActivity::class.java).putExtra("id", id)
                .addFlags(if (c is Activity) 0 else Intent.FLAG_ACTIVITY_NEW_TASK))
        }
    }

    private lateinit var list: LinearLayout
    private lateinit var scroll: ScrollView
    private lateinit var conv: Conv
    private lateinit var p: Provider
    private val db by lazy { Db.get(this) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        conv = db.conv(intent.getLongExtra("id", -1)) ?: run { finish(); return }
        p = Providers.byId(conv.provider) ?: Providers.all[0]

        val root = Ui.col(c, 16)
        val head = Ui.row(c)
        val t = Ui.col(c)
        t.addView(Ui.text(c, "${p.label} / ${Db.PROJECT}", 13f, Ui.ACCENT, true))
        t.addView(Ui.text(c, conv.title, 17f, Ui.TEXT, true).apply { maxLines = 2 })
        head.addView(t, Ui.lp(0, weight = 1f))
        head.addView(Ui.button(c, "My ${p.label}") {
            val last = db.messages(conv.id).lastOrNull { it.role == "user" }?.content ?: ""
            Ui.toast(c, AccountSend.send(c, p, last))
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(head)

        list = Ui.col(c)
        scroll = ScrollView(c).apply { addView(list) }
        root.addView(scroll, Ui.lp(h = 0, weight = 1f, top = Ui.dp(c, 10)))

        val box = Ui.input(c, "Ask for a change, e.g. \"shorter\" or \"more formal\"", true, 1)
        val send = Ui.button(c, "Send", true) {}
        val bottom = Ui.row(c)
        bottom.addView(box, Ui.lp(0, weight = 1f, end = Ui.dp(c, 8)))
        bottom.addView(send, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(bottom, Ui.lp(top = Ui.dp(c, 8)))
        setContentView(root)

        val store = Store(c)
        send.setOnClickListener {
            val q = box.text.toString().trim()
            if (q.isEmpty()) return@setOnClickListener
            if (!store.hasKey(p)) { Ui.toast(c, "Add a ${p.label} API key in Settings"); return@setOnClickListener }
            box.setText("")
            db.addMsg(conv.id, "user", q)
            bubble(Msg("user", q))
            val spin = ProgressBar(c)
            list.addView(spin, LinearLayout.LayoutParams(Ui.dp(c, 28), Ui.dp(c, 28)).apply { topMargin = Ui.dp(c, 8) })
            send.isEnabled = false
            val history = db.messages(conv.id)
            Thread {
                val r = runCatching {
                    Providers.chatSmart(p, store.key(p), store.model(p), Modes.system(store), history) { msg ->
                        runOnUiThread { Ui.toast(c, msg) }
                    }.text
                }
                r.onSuccess { db.addMsg(conv.id, "assistant", it) }
                runOnUiThread {
                    list.removeView(spin); send.isEnabled = true
                    r.onSuccess { bubble(Msg("assistant", it)) }
                        .onFailure {
                            ErrorLog.add(c, "Continue chat", p.label, store.model(p), (it as? AiException)?.code ?: 0,
                                it.message ?: it.toString(), q)
                            bubble(Msg("error", it.message ?: it.toString()))
                        }
                }
            }.start()
        }
        db.messages(conv.id).forEach { bubble(it) }
    }

    private fun bubble(m: Msg) {
        val c = this
        val mine = m.role == "user"
        val shown = if (mine && m.content.startsWith("Instruction: "))
            m.content.substringAfter("Text:\n", m.content) else m.content
        val card = Ui.col(c, 12)
        card.background = Ui.round(if (mine) Ui.CARD2 else Ui.CARD, Ui.dp(c, 14).toFloat())
        val label = when (m.role) { "user" -> "Me"; "error" -> "Error"; else -> p.label }
        card.addView(Ui.text(c, label, 12f, if (m.role == "error") Ui.BAD else Ui.MUTED, true))
        card.addView(Ui.text(c, shown, 15f).apply { setTextIsSelectable(true) })
        if (m.role == "assistant") {
            val row = Ui.row(c)
            row.addView(Ui.text(c, "Copy", 14f, Ui.ACCENT, true).apply {
                setPadding(0, Ui.dp(c, 8), Ui.dp(c, 18), 0); setOnClickListener { Ui.copy(c, m.content) }
            })
            row.addView(Ui.text(c, "Share", 14f, Ui.ACCENT, true).apply {
                setPadding(0, Ui.dp(c, 8), Ui.dp(c, 18), 0)
                setOnClickListener {
                    startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                        type = "text/plain"; putExtra(Intent.EXTRA_TEXT, m.content)
                    }, "Share"))
                }
            })
            card.addView(row)
        }
        val lp = Ui.lp(top = Ui.dp(c, 8)).apply {
            if (mine) marginStart = Ui.dp(c, 36) else marginEnd = Ui.dp(c, 18)
        }
        list.addView(card, lp)
        scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
    }
}
