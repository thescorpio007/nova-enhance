package com.nova.enhance

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.LinkedBlockingQueue
import kotlin.math.sqrt

/**
 * AI live voice, phrase by phrase: records continuously (no restarts, no lost words),
 * cuts at natural pauses, and Gemini writes out each phrase in order while you keep talking.
 *
 * [onText] gets (all text so far, phrases still being written out).
 * [onState] gets "listening" / "paused" / "finishing", or null when done.
 * [onDone] gets the full text after Stop.
 */
class PhraseVoice(
    private val c: Context,
    private val onText: (String, Int) -> Unit,
    private val onState: (String?) -> Unit,
    private val onDone: (String) -> Unit
) {
    companion object {
        private const val RATE = 16000
        private const val FRAME_MS = 20
        private const val FRAME = RATE * FRAME_MS / 1000          // samples per frame
        private const val MIN_PHRASE_MS = 1500
        private const val PAUSE_MS = 650                           // silence that ends a phrase
        private const val MAX_PHRASE_MS = 14000
    }

    private val main = Handler(Looper.getMainLooper())
    private var rec: AudioRecord? = null
    private var reader: Thread? = null
    private val queue = LinkedBlockingQueue<ByteArray>()
    private var worker: Thread? = null
    private val text = StringBuilder()
    private val pendingN = java.util.concurrent.atomic.AtomicInteger(0)
    @Volatile private var cancelled = false
    @Volatile private var running = false
    @Volatile var paused = false; private set
    @Volatile var active = false; private set
    @Volatile private var stopping = false
    private var started = 0L
    private var pausedAt = 0L
    private var pausedTotal = 0L
    private var failures = 0

    val seconds: Long get() {
        val now = if (paused) pausedAt else System.currentTimeMillis()
        return ((now - started - pausedTotal) / 1000).coerceAtLeast(0)
    }

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        val minBuf = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val r = try {
            AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT, maxOf(minBuf, FRAME * 2 * 10))
        } catch (e: Exception) { ErrorLog.add(c, "AI voice", message = "Recorder: $e"); return false }
        if (r.state != AudioRecord.STATE_INITIALIZED) { r.release(); ErrorLog.add(c, "AI voice", message = "Recorder not ready"); return false }
        rec = r
        active = true; stopping = false; paused = false; cancelled = false; text.clear(); pendingN.set(0); failures = 0
        started = System.currentTimeMillis(); pausedTotal = 0
        startWorker()
        startReading()
        onState("listening")
        return true
    }

    fun pause() {
        if (!active || paused) return
        paused = true; pausedAt = System.currentTimeMillis()
        running = false
        onState("paused")
    }

    fun resume() {
        if (!active || !paused) return
        paused = false; pausedTotal += System.currentTimeMillis() - pausedAt
        startReading()
        onState("listening")
    }

    fun stop() {
        if (!active || stopping) return
        stopping = true
        running = false
        onState("finishing")
        // The reader flushes its last phrase; the worker finishes the queue, then reports done.
        Thread {
            reader?.join(1500)
            queue.put(ByteArray(0))   // end marker
        }.start()
    }

    fun cancel() {
        cancelled = true
        active = false; running = false; stopping = true
        queue.clear(); queue.put(ByteArray(0))
        try { rec?.stop() } catch (_: Exception) { }
        try { rec?.release() } catch (_: Exception) { }
        rec = null
        onState(null)
    }

    // ------------------------------------------------------------------ recording + pause detection

    private fun startReading() {
        val r = rec ?: return
        running = true
        reader = Thread {
            try { r.startRecording() } catch (e: Exception) {
                ErrorLog.add(c, "AI voice", message = "startRecording: $e"); main.post { cancel(); Ui.toast(c, "The microphone couldn't be opened") }
                return@Thread
            }
            val frame = ShortArray(FRAME)
            val chunk = ByteArrayOutputStream()
            var chunkMs = 0; var silentMs = 0; var speechMs = 0
            var noise = 300.0          // adaptive noise floor
            fun flush() {
                if (speechMs >= 300 && chunkMs >= 500) { pendingN.incrementAndGet(); queue.put(chunk.toByteArray()); post() }
                chunk.reset(); chunkMs = 0; silentMs = 0; speechMs = 0
            }
            while (running) {
                val n = r.read(frame, 0, FRAME)
                if (n <= 0) continue
                var sum = 0.0
                for (i in 0 until n) { val v = frame[i].toDouble(); sum += v * v }
                val rms = sqrt(sum / n)
                val speaking = rms > maxOf(noise * 2.5, 450.0)
                if (!speaking) noise = noise * 0.97 + rms * 0.03
                for (i in 0 until n) { chunk.write(frame[i].toInt() and 0xff); chunk.write((frame[i].toInt() shr 8) and 0xff) }
                chunkMs += FRAME_MS
                if (speaking) { speechMs += FRAME_MS; silentMs = 0 } else silentMs += FRAME_MS
                // Keep only a little silence before speech starts.
                if (speechMs == 0 && chunkMs > 600) { chunk.reset(); chunkMs = 0; silentMs = 0 }
                val endOfPhrase = speechMs > 0 && silentMs >= PAUSE_MS && chunkMs >= MIN_PHRASE_MS
                val tooLong = chunkMs >= MAX_PHRASE_MS && (silentMs >= 120 || chunkMs >= MAX_PHRASE_MS + 3000)
                if (endOfPhrase || tooLong) flush()
            }
            flush()
            try { r.stop() } catch (_: Exception) { }
        }.also { it.start() }
    }

    // ------------------------------------------------------------------ Gemini, one phrase at a time, in order

    private fun startWorker() {
        worker = Thread {
            while (true) {
                val pcm = queue.take()
                if (pcm.isEmpty()) break
                val prev = synchronized(text) { text.toString().takeLast(300) }
                val r = runCatching { transcribe(pcm, prev) }
                pendingN.decrementAndGet()
                r.onSuccess { t ->
                    failures = 0
                    if (t.isNotBlank()) synchronized(text) {
                        if (text.isNotEmpty() && !text.endsWith("\n")) text.append(' ')
                        text.append(t.trim())
                    }
                }.onFailure { e ->
                    failures++
                    ErrorLog.add(c, "AI voice", "Gemini", AiVoice.fastModel(c), (e as? AiException)?.code ?: 0, e.message ?: e.toString())
                    if (failures == 1) main.post { Ui.toast(c, "A phrase couldn't be written out: ${e.message}") }
                }
                post()
            }
            val all = synchronized(text) { text.toString().trim() }
            main.post {
                if (cancelled) return@post
                active = false
                try { rec?.release() } catch (_: Exception) { }
                rec = null
                onState(null)
                onDone(all)
            }
        }.also { it.start() }
    }

    private fun post() {
        val t = synchronized(text) { text.toString() }
        val p = pendingN.get()
        main.post { onText(t, p) }
    }

    private fun transcribe(pcm: ByteArray, previous: String): String {
        val s = Store(c); val p = AiVoice.gemini()
        val prompt = buildString {
            append("Transcribe this short voice recording exactly as spoken. The speaker may mix languages (Arabic, English, ")
            append("French, German): write each language in its own script. Add normal punctuation and capital letters. ")
            append("Do not translate, summarise, answer or comment. Output only the transcription. If there is no speech, output nothing.")
            if (previous.isNotBlank()) append("\nThe text just before this part (for context only, do not repeat it): \"").append(previous).append("\"")
            val note = Profile.note(s)
            if (note.isNotBlank()) append("\nUse the correct spelling of technical terms for this speaker.").append(note)
        }
        val audio = Base64.encodeToString(wav(pcm), Base64.NO_WRAP)
        val body = JSONObject().put("contents", JSONArray().put(JSONObject().put("role", "user").put("parts",
            JSONArray().put(JSONObject().put("text", prompt))
                .put(JSONObject().put("inline_data", JSONObject().put("mime_type", "audio/wav").put("data", audio))))))
        var last: Exception? = null
        for (attempt in 0..2) {
            try {
                return Providers.rawGemini(s.key(p), AiVoice.fastModel(c), body, fast = true).trim()
            } catch (e: AiException) {
                last = e
                if (e.code !in setOf(429, 500, 502, 503, 504)) throw e
                Thread.sleep(longArrayOf(1000, 3000, 0)[attempt])
            } catch (e: java.io.IOException) {
                last = AiException("No connection to Gemini — check your internet or VPN.", Providers.NO_CONNECTION)
                Thread.sleep(1000)
            }
        }
        throw last ?: AiException("Gemini didn't answer")
    }

    /** 16 kHz mono 16-bit PCM → WAV file bytes. */
    private fun wav(pcm: ByteArray): ByteArray {
        val out = ByteArrayOutputStream(pcm.size + 44)
        fun i32(v: Int) { out.write(v and 0xff); out.write((v shr 8) and 0xff); out.write((v shr 16) and 0xff); out.write((v shr 24) and 0xff) }
        fun i16(v: Int) { out.write(v and 0xff); out.write((v shr 8) and 0xff) }
        out.write("RIFF".toByteArray()); i32(36 + pcm.size); out.write("WAVE".toByteArray())
        out.write("fmt ".toByteArray()); i32(16); i16(1); i16(1); i32(RATE); i32(RATE * 2); i16(2); i16(16)
        out.write("data".toByteArray()); i32(pcm.size); out.write(pcm)
        return out.toByteArray()
    }
}
