package com.nova.enhance

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONObject

/**
 * Compare models: the same sample (English + Arabic, with typical mistakes) goes to every text model
 * of one AI. Each result shows ✓/✗, time and the text; then the current default model scores them 1–10
 * and the best one gets a ⭐. "Use this model" makes it the default for that AI.
 */
class CompareActivity : BaseActivity() {
    companion object {
        fun open(c: Context, p: Provider) =
            c.startActivity(Intent(c, CompareActivity::class.java).putExtra("p", p.id))

        const val SAMPLE =
            "hi mark, i hope your doing well. we was reviewed the drawings yesterday and their is some clashes " +
            "between the ducts and the beams in level 3, please can you check it and revert back asap.\n\n" +
            "مرحبا، لقد ارسلنا المخططات امس ولكن لم نستلم اي رد منكم حتى الان، نرجو المراجعة والرد باسرع وقت."
    }

    private class Res(val model: String) {
        var ok = false
        var text = ""
        var ms = 0L
        var score: Int? = null
        var why = ""
        lateinit var card: LinearLayout
        lateinit var head: TextView
        lateinit var body: TextView
        lateinit var extra: TextView
    }

    private lateinit var p: Provider
    private lateinit var store: Store
    private lateinit var list: LinearLayout
    private lateinit var status: TextView
    private lateinit var startBtn: android.widget.Button
    private val results = mutableListOf<Res>()
    @Volatile private var stop = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        store = Store(c)
        p = Providers.byId(intent.getStringExtra("p")) ?: run { finish(); return }

        val root = Ui.col(c, 18)
        root.addView(Ui.title(c, "Compare ${p.label} models"))
        root.addView(Ui.text(c, "Current default: ${store.model(p)}", 13f, Ui.ACCENT, true))
        root.addView(Ui.section(c, "Test text (fixed sample)"))
        root.addView(Ui.text(c, SAMPLE, 14f, Ui.MUTED).apply {
            background = Ui.round(Ui.CARD2, Ui.dp(c, 14).toFloat())
            setPadding(Ui.dp(c, 14), Ui.dp(c, 12), Ui.dp(c, 14), Ui.dp(c, 12))
        })
        root.addView(Ui.text(c, "Mode: Fix grammar. Models are tested one by one with a short pause " +
            "(free plans limit requests per minute). Costs a few cents at most.", 12f, Ui.MUTED)
            .apply { setPadding(0, Ui.dp(c, 8), 0, 0) })

        startBtn = Ui.button(c, "Start test", true) { start() }
        root.addView(startBtn, Ui.lp(top = Ui.dp(c, 14)))
        status = Ui.text(c, "", 13f, Ui.MUTED)
        root.addView(status, Ui.lp(top = Ui.dp(c, 8)))
        list = Ui.col(c)
        root.addView(list, Ui.lp(top = Ui.dp(c, 4)))
        root.addView(Ui.signature(c))
        setContentView(ScrollView(c).apply { addView(root) })

        if (!store.hasKey(p)) { status.text = "Add a ${p.label} API key in Settings first."; startBtn.isEnabled = false }
    }

    override fun onDestroy() { stop = true; super.onDestroy() }

    private fun start() {
        startBtn.isEnabled = false
        list.removeAllViews(); results.clear()
        status.text = "Loading your models…"
        val key = store.key(p)
        Thread {
            val ids = runCatching { Providers.listModels(p, key) }
            val models = ids.getOrNull()?.let { usable(it) } ?: emptyList()
            if (ids.isFailure || models.isEmpty()) {
                runOnUiThread {
                    status.text = ids.exceptionOrNull()?.message ?: "No text models found"
                    startBtn.isEnabled = true
                }
                return@Thread
            }
            runOnUiThread {
                status.text = "Testing ${models.size} models…"
                models.forEach { m -> results += Res(m).also { addCard(it) } }
            }
            val first = Modes.firstMessage(Modes.byId("grammar"), "", SAMPLE)
            models.forEachIndexed { i, m ->
                if (stop) return@Thread
                runOnUiThread { status.text = "Testing ${i + 1}/${models.size}: $m" }
                val t0 = System.currentTimeMillis()
                val r = runCatching { Providers.chat(p, key, m, Modes.SYSTEM, listOf(Msg("user", first))) }
                val res = results.getOrNull(i) ?: return@forEachIndexed
                res.ms = System.currentTimeMillis() - t0
                res.ok = r.isSuccess && !r.getOrNull().isNullOrBlank()
                res.text = r.getOrNull()?.takeIf { it.isNotBlank() } ?: (r.exceptionOrNull()?.message ?: "Empty answer")
                runOnUiThread { drawCard(res) }
                Thread.sleep(1500)
            }
            score()
        }.start()
    }

    private fun usable(ids: List<String>): List<String> {
        val skip = Regex(p.skip, RegexOption.IGNORE_CASE)
        return ids.filter { !skip.containsMatchIn(it) }.take(25)
    }

    /** The current default model rates every successful result from 1 to 10. */
    private fun score() {
        val ok = results.filter { it.ok }
        if (ok.isEmpty()) { runOnUiThread { status.text = "No model answered."; startBtn.isEnabled = true }; return }
        runOnUiThread { status.text = "Scoring the results with ${store.model(p)}…" }
        val prompt = buildString {
            append("You are judging text-enhancement results. Original text:\n<<<\n$SAMPLE\n>>>\n")
            append("Task given to each model: correct all grammar, spelling and punctuation mistakes, keeping the meaning, tone and both languages (English and Arabic).\n\n")
            ok.forEachIndexed { i, r -> append("Result ${i + 1}:\n<<<\n${r.text}\n>>>\n\n") }
            append("Score each result from 1 to 10 for correctness (all mistakes fixed, including Arabic hamza), ")
            append("faithfulness (nothing added or lost, both languages kept) and natural wording. ")
            append("Reply with JSON only: {\"scores\":[{\"n\":1,\"score\":8,\"why\":\"max 12 words\"}]}")
        }
        val r = runCatching {
            Providers.chatSmart(p, store.key(p), store.model(p), "You are a strict, fair evaluator. Reply with JSON only.",
                listOf(Msg("user", prompt))) { msg -> runOnUiThread { status.text = msg } }.text
        }
        runOnUiThread {
            startBtn.isEnabled = true; startBtn.text = "Test again"
            val txt = r.getOrNull()
            if (txt == null) { status.text = "Scoring failed: ${r.exceptionOrNull()?.message}. Compare the texts yourself."; return@runOnUiThread }
            try {
                val json = JSONObject(txt.substring(txt.indexOf('{'), txt.lastIndexOf('}') + 1))
                val arr = json.getJSONArray("scores")
                for (j in 0 until arr.length()) {
                    val o = arr.getJSONObject(j)
                    val res = ok.getOrNull(o.getInt("n") - 1) ?: continue
                    res.score = o.optInt("score"); res.why = o.optString("why")
                }
            } catch (_: Exception) {
                status.text = "Couldn't read the scores. Compare the texts yourself."; return@runOnUiThread
            }
            val best = ok.filter { it.score != null }
                .sortedWith(compareByDescending<Res> { it.score }.thenBy { it.ms }).firstOrNull()
            results.forEach { drawCard(it, it === best) }
            // Best first.
            list.removeAllViews()
            results.sortedWith(compareByDescending<Res> { it.score ?: -1 }.thenBy { if (it.ok) it.ms else Long.MAX_VALUE })
                .forEach { list.addView(it.card, Ui.lp(top = Ui.dp(this, 10))) }
            status.text = if (best != null) "⭐ Best: ${best.model} · ${best.score}/10 · ${fmt(best.ms)}" else "Done"
        }
    }

    private fun addCard(r: Res) {
        val c = this
        r.card = Ui.card(c)
        r.head = Ui.text(c, r.model, 15f, Ui.TEXT, true)
        r.extra = Ui.text(c, "waiting…", 12f, Ui.MUTED)
        r.body = Ui.text(c, "", 14f, Ui.TEXT).apply { setTextIsSelectable(true); visibility = View.GONE }
        r.card.addView(r.head); r.card.addView(r.extra)
        r.card.addView(r.body, Ui.lp(top = Ui.dp(c, 8)))
        list.addView(r.card, Ui.lp(top = Ui.dp(c, 10)))
    }

    private fun drawCard(r: Res, best: Boolean = false) {
        val c = this
        val isDefault = r.model == store.model(p)
        r.head.text = (if (best) "⭐ " else "") + r.model + (if (isDefault) "  (default)" else "")
        r.head.setTextColor(if (best) 0xFFFFD86B.toInt() else Ui.TEXT)
        r.extra.text = (if (r.ok) "✓  ${fmt(r.ms)}" else "✗  failed") +
            (r.score?.let { "  ·  $it/10" } ?: "") + (if (r.why.isNotBlank()) "  ·  ${r.why}" else "")
        r.extra.setTextColor(if (r.ok) Ui.GOOD else Ui.BAD)
        r.body.visibility = View.VISIBLE
        r.body.text = r.text
        r.body.setTextColor(if (r.ok) Ui.TEXT else Ui.BAD)
        r.body.maxLines = if (r.ok) 40 else 3
        if (r.ok && r.card.childCount < 4) {
            r.card.addView(Ui.button(c, "Use this model") {
                store.setModel(p, r.model)
                Ui.toast(c, "${r.model} is now the default for ${p.label}")
                results.forEach { x -> drawCard(x, x.head.text.startsWith("⭐")) }
            }, Ui.lp(top = Ui.dp(c, 10)))
        }
        r.card.background = if (best) Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat(), Ui.dp(c, 2), 0xFFFFD86B.toInt())
        else Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat())
    }

    private fun fmt(ms: Long) = String.format("%.1f s", ms / 1000.0)
}
