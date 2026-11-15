package com.nova.enhance

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.IBinder

/**
 * ☁ Keeps Nova awake while you sign in to Google in the browser (Android puts a background app to sleep, and then Google's
 * answer never arrives). A small notification shows while it waits; it stops by itself. Designed by A.HAMROUNI
 */
class GdSignInService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26) nm.createNotificationChannel(NotificationChannel(CH, "Google sign-in", NotificationManager.IMPORTANCE_LOW))
        @Suppress("DEPRECATION")
        val n = (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CH) else Notification.Builder(this))
            .setSmallIcon(R.drawable.ic_tile).setContentTitle("Nova Enhance · signing in to Google…")
            .setContentText("Finish in the browser, then come back to Nova").setOngoing(true).build()
        try {
            if (Build.VERSION.SDK_INT >= 34) startForeground(7, n, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE) else startForeground(7, n)
        } catch (_: Exception) { }
        if (GDrive.pending) return START_NOT_STICKY
        GDrive.pending = true
        Thread {
            try {
                GDrive.signIn(applicationContext) { url ->
                    try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) { }
                }
            } catch (_: Exception) { }   // the reason is kept by GDrive (lastError)
            finally {
                GDrive.pending = false
                try { if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else @Suppress("DEPRECATION") stopForeground(true) } catch (_: Exception) { }
                stopSelf()
            }
        }.start()
        return START_NOT_STICKY
    }

    companion object {
        private const val CH = "gdsignin"
        fun start(c: Context) {
            val i = Intent(c, GdSignInService::class.java)
            if (Build.VERSION.SDK_INT >= 26) c.startForegroundService(i) else c.startService(i)
        }
    }
}
