package com.nova.enhance

import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer

/**
 * Android's own live recognition: words appear while speaking, keeps listening through pauses,
 * can be paused and resumed, and Stop finishes.
 *
 * [onText] gets (confirmed text, words still being recognised).
 * [onState] gets a status line ("listening" / "paused"), or null when finished.
 * [onDone] gets the final text after Stop.
 * [onFatal] is called if the speech service keeps failing (e.g. error 11), with the text heard so far.
 */
class VoiceInput(
    private val c: Context,
    private val langTag: String,
    private val onText: (String, String) -> Unit,
    private val onState: (String?) -> Unit,
    private val onDone: (String) -> Unit,
    private val onFatal: ((String) -> Unit)? = null
) {
    private val main = Handler(Looper.getMainLooper())
    private var rec: SpeechRecognizer? = null
    private val finalText = StringBuilder()
    private var stopping = false
    private var emptyRetries = 0
    private var serviceErrors = 0
    private var onDevice = false
    private var started = 0L
    private var pausedAt = 0L
    private var pausedTotal = 0L
    var active = false; private set
    var paused = false; private set

    companion object {
        fun available(c: Context) = SpeechRecognizer.isRecognitionAvailable(c) ||
            (Build.VERSION.SDK_INT >= 31 && SpeechRecognizer.isOnDeviceRecognitionAvailable(c))
    }

    val seconds: Long get() {
        val now = if (paused) pausedAt else System.currentTimeMillis()
        return ((now - started - pausedTotal) / 1000).coerceAtLeast(0)
    }

    fun start() {
        active = true; stopping = false; paused = false; emptyRetries = 0; serviceErrors = 0
        finalText.clear(); started = System.currentTimeMillis(); pausedTotal = 0
        // The phone's on-device recognizer (Android 12+) doesn't drop the connection like the online service.
        onDevice = Build.VERSION.SDK_INT >= 31 && SpeechRecognizer.isOnDeviceRecognitionAvailable(c)
        create()
        onState("listening")
        listen()
    }

    fun pause() {
        if (!active || paused) return
        paused = true; pausedAt = System.currentTimeMillis()
        try { rec?.stopListening() } catch (_: Exception) { }
        onState("paused")
    }

    fun resume() {
        if (!active || !paused) return
        paused = false; pausedTotal += System.currentTimeMillis() - pausedAt
        onState("listening")
        main.postDelayed({ if (active && !paused) listen() }, 150)
    }

    /** User tapped Stop: finish with what was heard. */
    fun stop() {
        if (!active) return
        stopping = true
        if (paused) { finish(); return }
        try { rec?.stopListening() } catch (_: Exception) { }
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

    private fun create() {
        release()
        val r = if (onDevice && Build.VERSION.SDK_INT >= 31) SpeechRecognizer.createOnDeviceSpeechRecognizer(c)
            else SpeechRecognizer.createSpeechRecognizer(c)
        rec = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) {}
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(rmsdB: Float) {}
            override fun onBufferReceived(buffer: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onEvent(eventType: Int, params: Bundle?) {}

            override fun onPartialResults(b: Bundle?) {
                val p = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull() ?: return
                onText(finalText.toString(), p)
            }

            override fun onResults(b: Bundle?) {
                val t = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull().orEmpty()
                if (t.isNotBlank()) {
                    if (finalText.isNotEmpty()) finalText.append(' ')
                    finalText.append(t.trim())
                    emptyRetries = 0
                }
                serviceErrors = 0
                onText(finalText.toString(), "")
                when {
                    stopping -> finish()
                    paused -> {}
                    else -> again()   // keep listening through pauses
                }
            }

            override fun onError(error: Int) {
                if (!active) return
                if (stopping) { finish(); return }
                if (paused) return
                when (error) {
                    SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT -> {
                        if (finalText.isNotEmpty() || emptyRetries < 2) { emptyRetries++; again(); return }
                        fatal("No sound — check Bluetooth headset or microphone")
                    }
                    SpeechRecognizer.ERROR_RECOGNIZER_BUSY -> main.postDelayed({ if (active && !paused) { create(); listen() } }, 600)
                    SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> fatal("Microphone permission is off for Nova Enhance")
                    else -> {
                        // 11 = service disconnected, 12/13 = language not available on the device, etc.
                        ErrorLog.add(c, "Voice", message = "Android speech error $error (on-device=$onDevice)")
                        serviceErrors++
                        if (serviceErrors <= 2) {
                            if (error == 12 || error == 13 || serviceErrors == 2) onDevice = !onDevice   // try the other engine
                            main.postDelayed({ if (active && !paused) { create(); listen() } }, 500)
                        } else fatal("Android voice keeps failing (error $error)")
                    }
                }
            }
        })
    }

    /** Restart gently between phrases (too-fast restarts cause "error 11"). */
    private fun again() { main.postDelayed({ if (active && !paused && !stopping) listen() }, 250) }

    private fun listen() {
        val r = rec ?: run { create(); rec } ?: return
        val i = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE, langTag)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, langTag)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
            putExtra(RecognizerIntent.EXTRA_SPEECH_INPUT_COMPLETE_SILENCE_LENGTH_MILLIS, 3000L)
            putExtra(RecognizerIntent.EXTRA_SPEECH_INPUT_POSSIBLY_COMPLETE_SILENCE_LENGTH_MILLIS, 3000L)
        }
        try { r.startListening(i) } catch (e: Exception) {
            serviceErrors++
            if (serviceErrors <= 2) main.postDelayed({ if (active) { onDevice = !onDevice; create(); listen() } }, 500)
            else fatal("Voice typing couldn't start: ${e.message}")
        }
    }

    private fun fatal(msg: String) {
        ErrorLog.add(c, "Voice", message = msg)
        val heard = finalText.toString().trim()
        active = false
        release()
        onState(null)
        if (onFatal != null) onFatal.invoke(heard) else { Ui.toast(c, msg); if (heard.isNotBlank()) onDone(heard) }
    }
}
