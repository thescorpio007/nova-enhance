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
 * Compare models: the same sample (English + Arabic, with typical mistakes) goes to the main text models
 * of one AI. Each result shows ✓/✗ and time; a working model scores them 1–10 (⭐ = best quality).
 * The best balance — the fastest model within 1 point of the top score — is set as the default
 * automatically (⚡). "Use this model" lets the user choose another one afterwards.
 */
class CompareActivity : BaseActivity() {
    companion object {
        fun open(c: Context, p: Provider) =
            c.startActivity(Intent(c, CompareActivity::class.java).putExtra("p", p.id))

        const val SAMPLE =
            "hi ahmed, i hope your doing well. we was reviewed the drawings yesterday and their is some clashes " +
            "between the ducts and the beams in level 3, please can you check it and revert back asap.\n\n" +
            "مرحبا، لقد ارسلنا المخططات امس ولكن لم نستلم اي رد منكم حتى الان، نرجو المراجعة والرد باسرع وقت."

        /** Main models only: previews, experiments and special-purpose variants are left out to save quota. */
        private const val NOT_MAIN = "preview|exp|customtools|gemma|latest|nano|search|-\\d{4}-\\d{2}-\\d{2}$|-\\d{3,4}$"
        private const val MAX_MODELS = 10
    }

    private class Res(val model: String) {
        var ok = false
        var notInPlan = false
        var done = false
        var text = ""
        var ms = 0L
        var score: Int? = null
        var why = ""
        lateinit var card: LinearLayout
        lateinit var head: TextView
        lateinit var body: TextView
        lateinit var extra: TextView
        var useBtn: View? = null
    }

    private lateinit var p: Provider
    private lateinit var store: Store
    private lateinit var list: LinearLayout
    private lateinit var status: TextView
    private lateinit var current: TextView
    private lateinit var startBtn: android.widget.Button
    private val results = mutableListOf<Res>()
    private var bestQuality: Res? = null
    private var autoPick: Res? = null
    @Volatile private var stop = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        store = Store(c)
        p = Providers.byId(intent.getStringExtra("p")) ?: run { finish(); return }

        val root = Ui.col(c, 18)
        root.addView(Ui.title(c, "Compare ${p.label} models"))
        current = Ui.text(c, "", 13f, Ui.ACCENT, true)
        root.addView(current); showCurrent()
        root.addView(Ui.section(c, "Test text (fixed sample)"))
        root.addView(Ui.text(c, SAMPLE, 14f, Ui.MUTED).apply {
            background = Ui.round(Ui.CARD2, Ui.dp(c, 14).toFloat())
            setPadding(Ui.dp(c, 14), Ui.dp(c, 12), Ui.dp(c, 14), Ui.dp(c, 12))
        })
        root.addView(Ui.text(c, "Mode: Fix grammar. Main models only, one by one with a short pause. " +
            "At the end the best balance of quality and speed becomes the default automatically (⚡). " +
            "You can still pick another with \"Use this model\".", 12f, Ui.MUTED)
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

    private fun showCurrent() { current.text = "Current default: ${store.model(p)}" }

    private fun start() {
        startBtn.isEnabled = false
        list.removeAllViews(); results.clear(); bestQuality = null; autoPick = null
        status.text = "Loading your models…"
        val key = store.key(p)
        Thread {
            val ids = runCatching { Providers.listModels(p, key) }
            val models = ids.getOrNull()?.let { mainModels(it) } ?: emptyList()
            if (ids.isFailure || models.isEmpty()) {
                runOnUiThread {
                    status.text = ids.exceptionOrNull()?.message ?: "No text models found"
                    startBtn.isEnabled = true
                }
                return@Thread
            }
            // Build the list first, then test strictly one model at a time (fixes "waiting…" with nothing tested).
            val rs = models.map { Res(it) }
            runOnUiThread {
                status.text = "Testing ${models.size} main models, one by one…"
                results.addAll(rs); rs.forEach { addCard(it) }
            }
            val first = Modes.firstMessage(Modes.byId("grammar"), "", SAMPLE)
            var safeFound = false
            for ((i, res) in rs.withIndex()) {
                if (stop) return@Thread
                runOnUiThread { status.text = "Testing ${i + 1}/${rs.size}: ${res.model}" }
                var r = attempt(key, res.model, first, res)
                // A per-minute limit (not a plan/quota limit): wait, then try once more.
                if (r?.code == 429 && !res.notInPlan) {
                    runOnUiThread { status.text = "Per-minute limit reached — waiting 15 s, then retrying ${res.model}…" }
                    Thread.sleep(15000)
                    r = attempt(key, res.model, first, res)
                }
                if (!res.ok && !res.notInPlan) ErrorLog.add(this, "Compare models", p.label, res.model, r?.code ?: 0, res.text)
                res.done = true
                // As soon as one model works, make sure there is a usable default.
                if (res.ok && !safeFound) {
                    safeFound = true
                    val defaultOk = rs.any { it.model == store.model(p) && it.ok }
                    if (!defaultOk) {
                        store.setModel(p, res.model)
                        runOnUiThread { showCurrent(); Ui.toast(this, "✓ Working model found: ${res.model} (set as default for now)") }
                    }
                }
                runOnUiThread { drawCard(res) }
                if (!res.notInPlan && i < rs.size - 1) Thread.sleep(2000)
            }
            score()
        }.start()
    }

    /** Runs one model; returns the error (if any). */
    private fun attempt(key: String, m: String, first: String, res: Res): AiException? {
        val t0 = System.currentTimeMillis()
        return try {
            val out = Providers.chat(p, key, m, Modes.SYSTEM, listOf(Msg("user", first)))
            res.ms = System.currentTimeMillis() - t0
            res.ok = out.isNotBlank(); res.text = out.ifBlank { "Empty answer" }
            null
        } catch (e: AiException) {
            val msg = e.message ?: ""
            res.ok = false; res.text = msg
            res.notInPlan = e.code == 402 || e.code == 403 ||
                (e.code == 429 && Regex("quota|billing|plan|limit: 0", RegexOption.IGNORE_CASE).containsMatchIn(msg))
            e
        } catch (e: Exception) {
            res.ok = false; res.text = "No connection — ${e.message ?: e}"
            null
        }
    }

    /** Main models, best-known first. */
    private fun mainModels(ids: List<String>): List<String> {
        val skip = Regex(p.skip, RegexOption.IGNORE_CASE)
        val notMain = Regex(NOT_MAIN, RegexOption.IGNORE_CASE)
        val usable = ids.filter { !skip.containsMatchIn(it) && !notMain.containsMatchIn(it) }
            .ifEmpty { ids.filter { !skip.containsMatchIn(it) } }
        val ordered = LinkedHashSet<String>()
        if (store.model(p) in usable) ordered += store.model(p)
        for (pat in p.prefer) {
            val re = Regex(pat)
            usable.filter { re.containsMatchIn(it) }.sortedDescending().forEach { ordered += it }
        }
        ordered += usable
        return ordered.take(MAX_MODELS)
    }

    /** A working model scores the results; then the best balance is chosen and set automatically. */
    private fun score() {
        val ok = results.filter { it.ok }
        if (ok.isEmpty()) { runOnUiThread { status.text = "No model answered."; startBtn.isEnabled = true }; return }
        // Judge with the current default if it worked in this test, otherwise the first model that worked.
        val judge = ok.firstOrNull { it.model == store.model(p) }?.model ?: ok.first().model
        runOnUiThread { status.text = "Scoring the results with $judge…" }
        val prompt = buildString {
            append("You are judging text-enhancement results. Original text:\n<<<\n$SAMPLE\n>>>\n")
            append("Task given to each model: correct all grammar, spelling and punctuation mistakes, keeping the meaning, tone and both languages (English and Arabic).\n\n")
            ok.forEachIndexed { i, r -> append("Result ${i + 1}:\n<<<\n${r.text}\n>>>\n\n") }
            append("Score each result from 1 to 10 for correctness (all mistakes fixed, including Arabic hamza), ")
            append("faithfulness (nothing added or lost, both languages kept) and natural wording. ")
            append("Reply with JSON only: {\"scores\":[{\"n\":1,\"score\":8,\"why\":\"max 12 words\"}]}")
        }
        val r = runCatching {
            Providers.chatSmart(p, store.key(p), judge, "You are a strict, fair evaluator. Reply with JSON only.",
                listOf(Msg("user", prompt))) { msg -> runOnUiThread { status.text = msg } }.text
        }
        var scored = false
        r.getOrNull()?.let { txt ->
            try {
                val json = JSONObject(txt.substring(txt.indexOf('{'), txt.lastIndexOf('}') + 1))
                val arr = json.getJSONArray("scores")
                for (j in 0 until arr.length()) {
                    val o = arr.getJSONObject(j)
                    val res = ok.getOrNull(o.getInt("n") - 1) ?: continue
                    res.score = o.optInt("score"); res.why = o.optString("why")
                    scored = true
                }
            } catch (_: Exception) { }
        }

        val pick: Res
        val reason: String
        if (scored) {
            val withScore = ok.filter { it.score != null }
            val top = withScore.maxOf { it.score!! }
            bestQuality = withScore.filter { it.score == top }.minByOrNull { it.ms }
            pick = withScore.filter { it.score!! >= top - 1 }.minByOrNull { it.ms }!!
            reason = "best balance: ${pick.score}/10 in ${fmt(pick.ms)}"
        } else {
            pick = ok.minByOrNull { it.ms }!!
            reason = "scores unavailable, so the fastest model that worked"
        }
        autoPick = pick
        store.setModel(p, pick.model)

        runOnUiThread {
            startBtn.isEnabled = true; startBtn.text = "Test again"
            showCurrent()
            list.removeAllViews()
            results.sortedWith(compareByDescending<Res> { if (it === autoPick) 1 else 0 }
                .thenByDescending { it.score ?: -1 }
                .thenBy { if (it.ok) it.ms else Long.MAX_VALUE })
                .forEach { drawCard(it); list.addView(it.card, Ui.lp(top = Ui.dp(this, 10))) }
            status.text = "⚡ Auto-selected ${pick.model} as the ${p.label} default ($reason)." +
                (bestQuality?.takeIf { it !== pick }?.let { "\n⭐ Highest quality: ${it.model} · ${it.score}/10 · ${fmt(it.ms)}" } ?: "") +
                (if (!scored) "\nScoring failed: ${r.exceptionOrNull()?.message ?: "couldn't read the scores"}" else "")
            status.setTextColor(Ui.GOOD)
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

    private fun drawCard(r: Res) {
        val c = this
        val isDefault = r.model == store.model(p)
        val auto = r === autoPick
        val best = r === bestQuality
        r.head.text = (if (auto) "⚡ " else "") + (if (best) "⭐ " else "") + r.model + (if (isDefault) "  (default)" else "")
        r.head.setTextColor(if (auto) 0xFFFFD86B.toInt() else Ui.TEXT)
        r.extra.text = when {
            r.ok -> "✓  ${fmt(r.ms)}" + (r.score?.let { "  ·  $it/10" } ?: "") + (if (r.why.isNotBlank()) "  ·  ${r.why}" else "") +
                (if (auto) "\nAuto-selected (best balance)" else "")
            r.notInPlan -> "✗  not in your plan (quota/billing)"
            else -> "✗  failed"
        }
        r.extra.setTextColor(when { r.ok -> Ui.GOOD; r.notInPlan -> Ui.MUTED; else -> Ui.BAD })
        r.body.visibility = if (r.notInPlan) View.GONE else View.VISIBLE
        r.body.text = r.text
        r.body.setTextColor(if (r.ok) Ui.TEXT else Ui.BAD)
        r.body.maxLines = if (r.ok) 40 else 3
        if (r.ok && r.useBtn == null) {
            val b = Ui.button(c, "Use this model") {
                store.setModel(p, r.model)
                Ui.toast(c, "${r.model} is now the default for ${p.label}")
                showCurrent(); results.forEach { drawCard(it) }
            }
            r.useBtn = b
            r.card.addView(b, Ui.lp(top = Ui.dp(c, 10)))
        }
        r.useBtn?.visibility = if (isDefault) View.GONE else View.VISIBLE
        r.card.background = if (auto) Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat(), Ui.dp(c, 2), 0xFFFFD86B.toInt())
        else Ui.round(Ui.CARD, Ui.dp(c, 16).toFloat())
    }

    private fun fmt(ms: Long) = String.format("%.1f s", ms / 1000.0)
}
