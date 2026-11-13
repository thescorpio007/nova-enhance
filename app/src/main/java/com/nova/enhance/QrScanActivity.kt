package com.nova.enhance

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.widget.FrameLayout
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.atomic.AtomicBoolean

/** 📷 Scans the pairing QR code on your PC's screen (Nova → Settings → 📱 Phone link) — inside Nova, no other app needed. */
class QrScanActivity : Activity() {
    companion object { const val RESULT_TEXT = "qr_text" }

    private lateinit var view: TextureView
    private var thread: HandlerThread? = null
    private var bg: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private val decoding = AtomicBoolean(false)
    private val done = AtomicBoolean(false)
    private val zx = MultiFormatReader().apply {
        setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        val root = FrameLayout(c).apply { setBackgroundColor(Ui.BG) }
        view = TextureView(c)
        root.addView(view, FrameLayout.LayoutParams(-1, -1))
        root.addView(Ui.text(c, "📷 Point at the QR code on your PC\n(Nova → Settings → 📱 Phone link)", 15f, Ui.TEXT, true).apply {
            gravity = Gravity.CENTER; setBackgroundColor(0xAA000000.toInt()); setPadding(Ui.dp(c, 14), Ui.dp(c, 14), Ui.dp(c, 14), Ui.dp(c, 14))
        }, FrameLayout.LayoutParams(-1, -2, Gravity.TOP))
        root.addView(Ui.button(c, "✕ Cancel") { finish() }, FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL).apply { bottomMargin = Ui.dp(c, 40) })
        setContentView(root)
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) requestPermissions(arrayOf(Manifest.permission.CAMERA), 9)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) start()
        else { Ui.toast(this, "Nova needs the camera to scan the QR code"); finish() }
    }

    override fun onResume() {
        super.onResume()
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) start()
    }

    override fun onPause() { stop(); super.onPause() }

    private fun start() {
        if (camera != null) return
        if (!view.isAvailable) {
            view.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                override fun onSurfaceTextureAvailable(s: SurfaceTexture, w: Int, h: Int) = start()
                override fun onSurfaceTextureSizeChanged(s: SurfaceTexture, w: Int, h: Int) {}
                override fun onSurfaceTextureDestroyed(s: SurfaceTexture) = true
                override fun onSurfaceTextureUpdated(s: SurfaceTexture) {}
            }
            return
        }
        try {
            thread = HandlerThread("nova-qr").also { it.start() }
            bg = Handler(thread!!.looper)
            val cm = getSystemService(Context.CAMERA_SERVICE) as CameraManager
            val id = cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK }
                ?: cm.cameraIdList.first()
            val map = cm.getCameraCharacteristics(id).get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)!!
            val size = map.getOutputSizes(ImageFormat.YUV_420_888).filter { it.width <= 1920 && it.height <= 1080 }.maxByOrNull { it.width * it.height }
                ?: map.getOutputSizes(ImageFormat.YUV_420_888).first()
            reader = ImageReader.newInstance(size.width, size.height, ImageFormat.YUV_420_888, 2).apply {
                setOnImageAvailableListener({ r -> decode(r) }, bg)
            }
            cm.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(cd: CameraDevice) {
                    camera = cd
                    val tex = view.surfaceTexture!!.apply { setDefaultBufferSize(size.width, size.height) }
                    val preview = Surface(tex)
                    @Suppress("DEPRECATION")
                    cd.createCaptureSession(listOf(preview, reader!!.surface), object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(s: CameraCaptureSession) {
                            session = s
                            val req = cd.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                                addTarget(preview); addTarget(reader!!.surface)
                                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                            }
                            try { s.setRepeatingRequest(req.build(), null, bg) } catch (_: Exception) { }
                        }
                        override fun onConfigureFailed(s: CameraCaptureSession) { runOnUiThread { Ui.toast(this@QrScanActivity, "Camera not available"); finish() } }
                    }, bg)
                }
                override fun onDisconnected(cd: CameraDevice) { cd.close(); camera = null }
                override fun onError(cd: CameraDevice, error: Int) { cd.close(); camera = null }
            }, bg)
        } catch (e: SecurityException) { finish() } catch (e: Exception) { Ui.toast(this, "Camera: ${e.message}"); finish() }
    }

    private fun decode(r: ImageReader) {
        val img = try { r.acquireLatestImage() } catch (_: Exception) { null } ?: return
        try {
            if (done.get() || !decoding.compareAndSet(false, true)) return
            val p = img.planes[0]; val w = img.width; val h = img.height
            val rowStride = p.rowStride; val buf = p.buffer
            val y = ByteArray(w * h)
            if (rowStride == w) buf.get(y, 0, minOf(y.size, buf.remaining()))
            else for (row in 0 until h) { buf.position(row * rowStride); buf.get(y, row * w, w) }
            val text = try {
                zx.decodeWithState(BinaryBitmap(HybridBinarizer(PlanarYUVLuminanceSource(y, w, h, 0, 0, w, h, false)))).text
            } catch (_: Exception) { null } finally { zx.reset() }
            if (text != null && done.compareAndSet(false, true)) runOnUiThread {
                setResult(RESULT_OK, Intent().putExtra(RESULT_TEXT, text)); finish()
            }
        } finally { decoding.set(false); img.close() }
    }

    private fun stop() {
        try { session?.close() } catch (_: Exception) { }; session = null
        try { camera?.close() } catch (_: Exception) { }; camera = null
        try { reader?.close() } catch (_: Exception) { }; reader = null
        thread?.quitSafely(); thread = null; bg = null
    }
}
