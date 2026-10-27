package com.nova.enhance

import android.content.Context
import android.media.MediaRecorder
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * AI voice recognition: records with the phone's microphone until Stop (max 5 min),
 * then Gemini writes out what was said — mixed languages, technical terms from "About me".
 */
class AiVoice(
    private val c: Context,
    private val onState: (String?) -> Unit,
    private val onDone: (String) -> Unit,
    private val onFail: (String, File?) -> Unit
) {
    companion object {
        const val MAX_MS = 5 * 60 * 1000L

        fun gemini(): Provider = Providers.byId("gemini")!!

        /** Gemini writes out the recording. Throws AiException on failure. */
        fun transcribe(c: Context, file: File): String {
            val s = Store(c)
            val p = gemini()
            val key = s.key(p)
            if (key.isBlank()) throw AiException("No Gemini API key")
            val audio = Base64.encodeToString(file.readBytes(), Base64.NO_WRAP)
            val prompt = buildString {
                append("Transcribe this voice recording exactly as spoken. The speaker may mix languages (Arabic, English, ")
                append("French, German) in one sentence: write each language in its own script. Add normal punctuation. ")
                append("Do not translate, summarise, answer or comment. Output only the transcription. ")
                append("If there is no speech, output nothing.")
                val note = Profile.note(s)
                if (note.isNotBlank()) append("\nUse the correct spelling of technical terms for this speaker.").append(note)
            }
            val body = JSONObject().put("contents", JSONArray().put(JSONObject().put("role", "user").put("parts",
                JSONArray()
                    .put(JSONObject().put("text", prompt))
                    .put(JSONObject().put("inline_data", JSONObject().put("mime_type", "audio/aac").put("data", audio))))))
            var last: AiException? = null
            for (attempt in 0..2) {
                try {
                    return Providers.rawGemini(key, s.model(p), body).trim()
                } catch (e: AiException) {
                    last = e
                    if (e.code !in setOf(429, 500, 502, 503, 504)) throw e
                    Thread.sleep(longArrayOf(2000, 5000, 0)[attempt])
                } catch (e: java.io.IOException) {
                    throw AiException("No connection to Gemini — check your internet or VPN.", Providers.NO_CONNECTION)
                }
            }
            throw last ?: AiException("Gemini didn't answer")
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private var rec: MediaRecorder? = null
    private var file: File? = null
    private var started = 0L
    var active = false; private set

    private val tick = object : Runnable {
        override fun run() {
            if (!active) return
            val ms = System.currentTimeMillis() - started
            if (ms >= MAX_MS) { stop(); return }
            onState("🔴 Recording ${ms / 60000}:${String.format("%02d", (ms / 1000) % 60)}")
            main.postDelayed(this, 500)
        }
    }

    fun start(): Boolean {
        val f = File(c.cacheDir, "voice-${System.currentTimeMillis()}.aac")
        return try {
            @Suppress("DEPRECATION")
            val r = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(c) else MediaRecorder()
            r.setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
            r.setOutputFormat(MediaRecorder.OutputFormat.AAC_ADTS)
            r.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            r.setAudioChannels(1)
            r.setAudioSamplingRate(16000)
            r.setAudioEncodingBitRate(32000)
            r.setOutputFile(f.absolutePath)
            r.prepare(); r.start()
            rec = r; file = f; started = System.currentTimeMillis(); active = true
            main.post(tick)
            true
        } catch (e: Exception) {
            ErrorLog.add(c, "AI voice", "Gemini", message = "Recorder: $e")
            false
        }
    }

    fun stop() {
        if (!active) return
        active = false
        val ms = System.currentTimeMillis() - started
        try { rec?.stop() } catch (_: Exception) { }
        try { rec?.release() } catch (_: Exception) { }
        rec = null
        val f = file
        if (f == null || !f.exists() || f.length() < 1500 || ms < 700) {
            onState(null); f?.delete()
            onFail("Nothing was recorded — hold on a moment longer, or check the microphone / Bluetooth headset", null)
            return
        }
        onState("✍ Writing it out with Gemini…")
        Thread { recognize(f) }.start()
    }

    /** Also used by "Retry" after a failure, with the same recording. */
    fun recognize(f: File) {
        val r = runCatching { transcribe(c, f) }
        main.post {
            onState(null)
            r.onSuccess { t ->
                if (t.isBlank()) onFail("No speech was heard in the recording — check the microphone or Bluetooth headset", f)
                else { f.delete(); onDone(t) }
            }.onFailure { e ->
                ErrorLog.add(c, "AI voice", "Gemini", Store(c).model(gemini()), (e as? AiException)?.code ?: 0, e.message ?: e.toString())
                onFail(e.message ?: e.toString(), f)
            }
        }
    }

    fun cancel() {
        if (!active) return
        active = false
        try { rec?.stop() } catch (_: Exception) { }
        try { rec?.release() } catch (_: Exception) { }
        rec = null; file?.delete(); onState(null)
    }
}
