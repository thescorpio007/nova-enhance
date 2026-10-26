package com.nova.enhance

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService

/** Quick-settings tile: copy text, swipe down, tap "Enhance copied text". */
class EnhanceTile : TileService() {
    override fun onStartListening() {
        qsTile?.apply { state = Tile.STATE_ACTIVE; label = "Enhance copied text"; updateTile() }
    }

    @SuppressLint("StartActivityAndCollapseDeprecated")
    override fun onClick() {
        val i = Intent(this, CaptureActivity::class.java)
            .setAction(CaptureActivity.ACTION_CLIPBOARD)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, i,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(i)
        }
    }
}
