package com.nova.enhance

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.PixelFormat
import android.os.Build
import android.os.IBinder
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.WindowManager
import android.widget.TextView
import kotlin.math.abs

/** Floating bubble over all apps: copy text, tap the bubble, the quick sheet opens with it. */
class BubbleService : Service() {
    companion object {
        private const val CH = "bubble"
        private const val STOP = "com.nova.enhance.STOP_BUBBLE"

        fun start(c: Context) {
            val i = Intent(c, BubbleService::class.java)
            if (Build.VERSION.SDK_INT >= 26) c.startForegroundService(i) else c.startService(i)
        }
    }

    private var wm: WindowManager? = null
    private var view: TextView? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) {
            Store(this).bubbleOn = false
            stopSelf(); return START_NOT_STICKY
        }
        goForeground()
        if (view == null) showBubble()
        return START_STICKY
    }

    private fun goForeground() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(NotificationChannel(CH, "Floating bubble", NotificationManager.IMPORTANCE_MIN))
        }
        val stop = PendingIntent.getService(this, 1, Intent(this, BubbleService::class.java).setAction(STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val open = PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        @Suppress("DEPRECATION")
        val b = if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CH) else Notification.Builder(this)
        val n = b.setSmallIcon(R.drawable.ic_tile)
            .setContentTitle("Nova Enhance bubble is on")
            .setContentText("Copy text, then tap the bubble")
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null as android.graphics.drawable.Icon?, "Turn off", stop).build())
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) startForeground(3, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(3, n)
    }

    private fun showBubble() {
        if (Build.VERSION.SDK_INT >= 23 && !Settings.canDrawOverlays(this)) { stopSelf(); return }
        val w = getSystemService(WINDOW_SERVICE) as WindowManager
        val size = Ui.dp(this, 52)
        val v = TextView(this).apply {
            text = "✦"; textSize = 24f; gravity = Gravity.CENTER
            setTextColor(0xFFFFFFFF.toInt())
            background = Ui.round(Ui.ACCENT, size / 2f, Ui.dp(this@BubbleService, 2), 0x66FFFFFF)
            elevation = 8f
            alpha = 0.92f
        }
        @Suppress("DEPRECATION")
        val type = if (Build.VERSION.SDK_INT >= 26) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        else WindowManager.LayoutParams.TYPE_PHONE
        val lp = WindowManager.LayoutParams(size, size, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT).apply {
            gravity = Gravity.TOP or Gravity.START
            x = resources.displayMetrics.widthPixels - size - Ui.dp(this@BubbleService, 8)
            y = resources.displayMetrics.heightPixels / 3
        }
        var sx = 0; var sy = 0; var tx = 0f; var ty = 0f; var moved = false
        v.setOnTouchListener { _, e ->
            when (e.action) {
                MotionEvent.ACTION_DOWN -> { sx = lp.x; sy = lp.y; tx = e.rawX; ty = e.rawY; moved = false; true }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - tx; val dy = e.rawY - ty
                    if (abs(dx) > 12 || abs(dy) > 12) moved = true
                    if (moved) { lp.x = sx + dx.toInt(); lp.y = sy + dy.toInt(); w.updateViewLayout(v, lp) }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (!moved) openSheet()
                    else {   // snap to the nearest side
                        val half = resources.displayMetrics.widthPixels / 2
                        lp.x = if (lp.x + size / 2 < half) Ui.dp(this, 4)
                        else resources.displayMetrics.widthPixels - size - Ui.dp(this, 4)
                        w.updateViewLayout(v, lp)
                    }
                    true
                }
                else -> false
            }
        }
        w.addView(v, lp)
        wm = w; view = v
    }

    private fun openSheet() {
        startActivity(Intent(this, CaptureActivity::class.java)
            .setAction(CaptureActivity.ACTION_CLIPBOARD)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
    }

    override fun onDestroy() {
        view?.let { try { wm?.removeView(it) } catch (_: Exception) {} }
        view = null
        super.onDestroy()
    }
}
