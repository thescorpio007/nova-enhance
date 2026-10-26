package com.nova.enhance

import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

enum class Api { OPENAI_COMPAT, ANTHROPIC, GEMINI }

data class Provider(
    val id: String,
    val label: String,
    val api: Api,
    val baseUrl: String,
    val defaultModel: String,
    val keyPage: String,
    val webUrl: String,
    /** Regexes in order of preference, used to choose a model automatically from the account's model list. */
    val prefer: List<String>,
    val skip: String = "audio|realtime|transcri|tts|image|embed|search|moderation|instruct|vision|codex|dall|whisper|omni-mod|computer|deep-research|live|thinking|reasoner|exp"
)

data class Msg(val role: String, val content: String)   // role: "user" or "assistant"

class AiException(message: String, val code: Int = 0) : Exception(message)

/** An answer, and which model gave it (switched = a backup model answered because the usual one was busy). */
data class Answer(val text: String, val model: String, val switched: Boolean)

object Providers {
    val all = listOf(
        Provider("chatgpt", "ChatGPT", Api.OPENAI_COMPAT, "https://api.openai.com/v1", "gpt-4o-mini",
            "https://platform.openai.com/api-keys", "https://chatgpt.com/",
            listOf("^gpt-5(\\.\\d+)?-mini$", "^gpt-5(\\.\\d+)?$", "^gpt-4\\.1-mini$", "^gpt-4o-mini$", "^gpt-")),
        Provider("claude", "Claude", Api.ANTHROPIC, "https://api.anthropic.com/v1", "claude-sonnet-5-5",
            "https://console.anthropic.com/settings/keys", "https://claude.ai/new",
            listOf("^claude-sonnet-", "^claude-opus-", "^claude-haiku-", "^claude-")),
        Provider("gemini", "Gemini", Api.GEMINI, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash",
            "https://aistudio.google.com/app/apikey", "https://gemini.google.com/app",
            listOf("^gemini-[0-9.]+-flash$", "^gemini-[0-9.]+-pro$", "^gemini-[0-9.]+-flash", "^gemini-")),
        Provider("deepseek", "DeepSeek", Api.OPENAI_COMPAT, "https://api.deepseek.com", "deepseek-chat",
            "https://platform.deepseek.com/api_keys", "https://chat.deepseek.com/",
            listOf("^deepseek-chat$", "^deepseek-")),
        Provider("grok", "Grok", Api.OPENAI_COMPAT, "https://api.x.ai/v1", "grok-3-mini",
            "https://console.x.ai/", "https://grok.com/",
            listOf("^grok-[0-9.]+$", "^grok-[0-9.]+-fast", "^grok-[0-9.]+-mini$", "^grok-"))
    )

    fun byId(id: String?): Provider? = all.firstOrNull { it.id == id }

    // ---------------------------------------------------------------- chat

    fun chat(p: Provider, key: String, model: String, system: String, history: List<Msg>): String =
        when (p.api) {
            Api.OPENAI_COMPAT -> {
                val msgs = JSONArray().put(JSONObject().put("role", "system").put("content", system))
                history.forEach { msgs.put(JSONObject().put("role", it.role).put("content", it.content)) }
                val body = JSONObject().put("model", model).put("messages", msgs)
                val r = post("${p.baseUrl}/chat/completions", body, mapOf("Authorization" to "Bearer $key"))
                r.getJSONArray("choices").getJSONObject(0).getJSONObject("message").optString("content").trim()
            }
            Api.ANTHROPIC -> {
                val msgs = JSONArray()
                history.forEach { msgs.put(JSONObject().put("role", it.role).put("content", it.content)) }
                val body = JSONObject().put("model", model).put("max_tokens", 8000)
                    .put("system", system).put("messages", msgs)
                val r = post("${p.baseUrl}/messages", body,
                    mapOf("x-api-key" to key, "anthropic-version" to "2023-06-01"))
                val parts = r.getJSONArray("content")
                buildString {
                    for (i in 0 until parts.length()) {
                        val o = parts.getJSONObject(i)
                        if (o.optString("type") == "text") append(o.optString("text"))
                    }
                }.trim()
            }
            Api.GEMINI -> {
                val contents = JSONArray()
                history.forEach {
                    contents.put(JSONObject()
                        .put("role", if (it.role == "assistant") "model" else "user")
                        .put("parts", JSONArray().put(JSONObject().put("text", it.content))))
                }
                val body = JSONObject()
                    .put("systemInstruction", JSONObject().put("parts", JSONArray().put(JSONObject().put("text", system))))
                    .put("contents", contents)
                val r = post("${p.baseUrl}/models/$model:generateContent", body, mapOf("x-goog-api-key" to key))
                val cands = r.optJSONArray("candidates")
                if (cands == null || cands.length() == 0) {
                    val why = r.optJSONObject("promptFeedback")?.optString("blockReason") ?: "no answer"
                    throw AiException("Gemini returned no text ($why)")
                }
                val parts = cands.getJSONObject(0).optJSONObject("content")?.optJSONArray("parts") ?: JSONArray()
                buildString {
                    for (i in 0 until parts.length()) {
                        val o = parts.getJSONObject(i)
                        if (!o.optBoolean("thought")) append(o.optString("text"))
                    }
                }.trim()
            }
        }

    // ---------------------------------------------------------------- busy handling

    const val NO_CONNECTION = -1
    private val busyCodes = setOf(429, 500, 502, 503, 504, 529)
    private val waits = longArrayOf(2, 5, 10)

    /**
     * Like [chat], but when the AI is busy it retries 3 times (after 2, 5 and 10 s),
     * then tries other models of the same AI. [status] reports progress ("retrying…").
     */
    fun chatSmart(p: Provider, key: String, model: String, system: String, history: List<Msg>,
                  status: (String) -> Unit): Answer {
        fun once(m: String): String = try {
            chat(p, key, m, system, history).ifBlank { throw AiException("Empty answer") }
        } catch (e: java.io.IOException) {
            // No internet, VPN dropped, address blocked or timed out.
            throw AiException("No connection to ${p.label} — check your internet or VPN.", NO_CONNECTION)
        }
        try {
            for (i in 0..3) {
                try {
                    return Answer(once(model), model, false)
                } catch (e: AiException) {
                    if ((e.code !in busyCodes && e.code != NO_CONNECTION) || i == 3) throw e
                    status(if (e.code == NO_CONNECTION) "No connection — retrying (${i + 1}/3)…"
                           else "${p.label} is busy — retrying (${i + 1}/3)…")
                    Thread.sleep(waits[i] * 1000)
                }
            }
            throw AiException("No answer")
        } catch (e: AiException) {
            if (e.code !in busyCodes) throw e
            val alts = runCatching { alternatives(p, listModels(p, key), model) }.getOrDefault(emptyList())
            for (m in alts) {
                status("Still busy — trying ${p.label} model $m…")
                try {
                    return Answer(once(m), m, true)
                } catch (_: AiException) { }
            }
            throw e
        }
    }

    /** Other everyday models of the same AI, best first. */
    fun alternatives(p: Provider, ids: List<String>, exclude: String): List<String> {
        val skip = Regex(p.skip, RegexOption.IGNORE_CASE)
        val dated = Regex("-\\d{4}-\\d{2}-\\d{2}$")
        val usable = ids.filter { it != exclude && !skip.containsMatchIn(it) && !dated.containsMatchIn(it) }
        val out = LinkedHashSet<String>()
        for (pat in p.prefer) {
            val re = Regex(pat)
            usable.filter { re.containsMatchIn(it) }.sortedDescending().forEach { out += it }
        }
        return out.take(3)
    }

    // ---------------------------------------------------------------- models

    fun listModels(p: Provider, key: String): List<String> {
        val ids = mutableListOf<String>()
        when (p.api) {
            Api.OPENAI_COMPAT -> {
                val r = get("${p.baseUrl}/models", mapOf("Authorization" to "Bearer $key"))
                val d = r.getJSONArray("data")
                for (i in 0 until d.length()) ids += d.getJSONObject(i).getString("id")
            }
            Api.ANTHROPIC -> {
                val r = get("${p.baseUrl}/models?limit=100", mapOf("x-api-key" to key, "anthropic-version" to "2023-06-01"))
                val d = r.getJSONArray("data")
                for (i in 0 until d.length()) ids += d.getJSONObject(i).getString("id")
            }
            Api.GEMINI -> {
                val r = get("${p.baseUrl}/models?pageSize=200", mapOf("x-goog-api-key" to key))
                val d = r.optJSONArray("models") ?: JSONArray()
                for (i in 0 until d.length()) {
                    val o = d.getJSONObject(i)
                    val methods = o.optJSONArray("supportedGenerationMethods")?.toString() ?: ""
                    if (methods.contains("generateContent")) ids += o.getString("name").removePrefix("models/")
                }
            }
        }
        return ids.sorted()
    }

    /** Choose the best everyday model from what the account offers. */
    fun pickModel(p: Provider, ids: List<String>): String {
        val skip = Regex(p.skip, RegexOption.IGNORE_CASE)
        val usable = ids.filter { !skip.containsMatchIn(it) && !Regex("-\\d{4}-\\d{2}-\\d{2}$").containsMatchIn(it) }
            .ifEmpty { ids.filter { !skip.containsMatchIn(it) } }
        for (pat in p.prefer) {
            val re = Regex(pat)
            val hit = usable.filter { re.containsMatchIn(it) }.sortedDescending()
            if (hit.isNotEmpty()) return hit.first()
        }
        return usable.firstOrNull() ?: p.defaultModel
    }

    // ---------------------------------------------------------------- http

    private fun open(url: String, headers: Map<String, String>): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 20000; readTimeout = 120000
            setRequestProperty("Accept", "application/json")
            headers.forEach { (k, v) -> setRequestProperty(k, v) }
        }

    private fun post(url: String, body: JSONObject, headers: Map<String, String>): JSONObject {
        val c = open(url, headers)
        c.requestMethod = "POST"; c.doOutput = true
        c.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        c.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        return read(c)
    }

    private fun get(url: String, headers: Map<String, String>): JSONObject = read(open(url, headers))

    private fun read(c: HttpURLConnection): JSONObject {
        try {
            val code = c.responseCode
            val stream = if (code in 200..299) c.inputStream else c.errorStream
            val txt = stream?.let { s -> BufferedReader(InputStreamReader(s, Charsets.UTF_8)).use { it.readText() } } ?: ""
            if (code !in 200..299) throw AiException(errorText(code, txt), code)
            return JSONObject(txt)
        } finally {
            c.disconnect()
        }
    }

    private fun errorText(code: Int, body: String): String {
        val msg = try {
            val o = JSONObject(body)
            val e = o.opt("error")
            when (e) {
                is JSONObject -> e.optString("message", body)
                is String -> e
                else -> o.optString("message", body)
            }
        } catch (_: Exception) { body.take(300) }
        val hint = when (code) {
            401, 403 -> " — check the API key in Settings."
            402 -> " — the AI account has no credit left."
            404 -> " — the model may not exist; tap Detect model in Settings."
            429 -> " — too many requests or no credit; try again later."
            else -> ""
        }
        return "Error $code: ${msg.take(400)}$hint"
    }
}
