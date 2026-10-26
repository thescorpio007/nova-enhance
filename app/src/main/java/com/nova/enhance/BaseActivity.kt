package com.nova.enhance

import android.app.Activity
import android.os.Build
import android.view.View
import android.view.WindowInsets
import kotlin.math.max

/**
 * Android 15+ draws every app edge-to-edge (under the clock and the navigation buttons).
 * This keeps each screen inside the safe area and above the keyboard.
 */
open class BaseActivity : Activity() {
    override fun setContentView(view: View) {
        super.setContentView(view)
        if (Build.VERSION.SDK_INT < 35) return
        val l = view.paddingLeft; val t = view.paddingTop; val r = view.paddingRight; val b = view.paddingBottom
        view.setOnApplyWindowInsetsListener { v, insets ->
            val sys = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
            val ime = insets.getInsets(WindowInsets.Type.ime())
            v.setPadding(l + sys.left, t + sys.top, r + sys.right, b + max(sys.bottom, ime.bottom))
            WindowInsets.CONSUMED
        }
        view.requestApplyInsets()
    }
}
