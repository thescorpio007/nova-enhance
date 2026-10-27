package com.nova.enhance

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer

/**
 * The app's own listening: keeps listening through pauses until stop() is called,
 * shows words live, and retries once when nothing is heard.
 *
 * [onText] receives the full text so far (final + live partial).
 * [onState] receives a short status line, or null when listening ended.
 * [onDone] receives the final text when the user stops (or after a failure, with what was heard).
 */
class VoiceInput(
    private val c: Context,
    private val langTag: String,
    private val onText: (String) -> Unit,
    private val onState: (String?) -> Unit,
    private val onDone: (String) -> Unit
) {
    private val main = Handler(Looper.getMainLooper())
    private var rec: SpeechRecognizer? = null
    private val finalText = StringBuilder()
    private var stopping = false
    private var emptyRetries = 0
    var active = false; private set

    companion object {
        fun available(c: Context) = SpeechRecognizer.isRecognitionAvailable(c)
    }

    fun start() {
        active = true; stopping = false; emptyRetries = 0; finalText.clear()
        onState("Listening…")
        listenOnce()
    }

    /** User tapped Stop: finish with what was heard. */
    fun stop() {
        if (!active) return
        stopping = true
        try { rec?.stopListening() } catch (_: Exception) { }
        // If no final result arrives quickly, finish anyway.
        main.postDelayed({ finish() }, 1200)
    }

    fun cancel() { active = false; release(); onState(null) }

    private fun finish() {
        if (!active) return
        active = false
        release()
        onState(null)
        onDone(finalText.toString().trim())
    }

    private fun release() {
        try { rec?.destroy() } catch (_: Exception) { }
        rec = null
    }

    private fun listenOnce() {
        release()
        val r = SpeechRecognizer.createSpeechRecognizer(c)
        rec = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) { onState("Listening…") }
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(rmsdB: Float) {}
            override fun onBufferReceived(buffer: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onEvent(eventType: Int, params: Bundle?) {}

            override fun onPartialResults(b: Bundle?) {
                val p = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull() ?: return
                onText(join(finalText.toString(), p))
            }

            override fun onResults(b: Bundle?) {
                val t = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull().orEmpty()
                if (t.isNotBlank()) {
                    if (finalText.isNotEmpty()) finalText.append(' ')
                    finalText.append(t.trim())
                    emptyRetries = 0
                }
                onText(finalText.toString())
                if (stopping) finish() else main.post { if (active) listenOnce() }   // keep listening through pauses
            }

            override fun onError(error: Int) {
                if (!active) return
                if (stopping) { finish(); return }
                when (error) {
                    SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT -> {
                        if (finalText.isNotEmpty()) { main.post { if (active) listenOnce() }; return }
                        if (emptyRetries < 1) { emptyRetries++; onState("Didn't hear anything — listening again…"); main.postDelayed({ if (active) listenOnce() }, 300); return }
                        fail("No sound — check Bluetooth headset or microphone, then try again")
                    }
                    SpeechRecognizer.ERROR_RECOGNIZER_BUSY -> main.postDelayed({ if (active) listenOnce() }, 500)
                    SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> fail("Microphone permission is off for Nova Enhance")
                    SpeechRecognizer.ERROR_NETWORK, SpeechRecognizer.ERROR_NETWORK_TIMEOUT -> fail("No connection for voice typing — check internet")
                    SpeechRecognizer.ERROR_AUDIO -> fail("The microphone couldn't be opened — another app may be using it")
                    else -> fail("Voice typing error $error")
                }
            }
        })
        val i = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE, langTag)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, langTag)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
            putExtra(RecognizerIntent.EXTRA_SPEECH_INPUT_COMPLETE_SILENCE_LENGTH_MILLIS, 4000L)
            putExtra(RecognizerIntent.EXTRA_SPEECH_INPUT_POSSIBLY_COMPLETE_SILENCE_LENGTH_MILLIS, 4000L)
            putExtra(RecognizerIntent.EXTRA_SPEECH_INPUT_MINIMUM_LENGTH_MILLIS, 15000L)
        }
        try { r.startListening(i) } catch (e: Exception) { fail("Voice typing couldn't start: ${e.message}") }
    }

    private fun fail(msg: String) {
        ErrorLog.add(c, "Voice", message = msg)
        val heard = finalText.toString().trim()
        active = false
        release()
        onState(null)
        Ui.toast(c, msg)
        if (heard.isNotBlank()) onDone(heard)
    }

    private fun join(a: String, b: String) = if (a.isBlank()) b else "$a $b"
}
