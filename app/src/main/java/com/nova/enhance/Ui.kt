package com.nova.enhance

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast

/** Small helpers so every screen is built in code with one consistent look. */
object Ui {
    const val BG = 0xFF0E1116.toInt()
    const val CARD = 0xFF181C24.toInt()
    const val CARD2 = 0xFF222836.toInt()
    const val ACCENT = 0xFF7C6CFF.toInt()
    const val TEXT = 0xFFECEEF3.toInt()
    const val MUTED = 0xFF9AA3B5.toInt()
    const val GOOD = 0xFF3DDC97.toInt()
    const val BAD = 0xFFFF6B6B.toInt()
    const val SIGNATURE = "Designed by A.HAMROUNI"

    fun dp(c: Context, v: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), c.resources.displayMetrics).toInt()

    fun round(color: Int, radius: Float, stroke: Int = 0, strokeColor: Int = 0): GradientDrawable =
        GradientDrawable().apply {
            setColor(color); cornerRadius = radius
            if (stroke > 0) setStroke(stroke, strokeColor)
        }

    fun col(c: Context, pad: Int = 0): LinearLayout = LinearLayout(c).apply {
        orientation = LinearLayout.VERTICAL
        val p = dp(c, pad); setPadding(p, p, p, p)
    }

    fun row(c: Context): LinearLayout = LinearLayout(c).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    /**
     * ⏱ A status line that shows Nova is working: while its text ends with “…” (“⏳ Asking your PC…”), a timer counts up in front
     * (“⏱ 0:35  ⏳ Asking your PC…”), the line turns amber after 30 s, and after 20 quiet seconds “still working, not stuck”. Designed by A.HAMROUNI
     */
    const val AMBER = 0xFFFFD27A.toInt()

    fun thinking(tv: TextView): TextView {
        val h = android.os.Handler(android.os.Looper.getMainLooper())
        val rx = Regex("^⏱ \\d+:\\d\\d  (.*?)(?: · (?:⏳|⚠)[^\\n]*?(?:not stuck|still working))?(.*)$", RegexOption.DOT_MATCHES_ALL)
        var base = ""; var self = false; var start = 0L; var changed = 0L; var on = false
        var color = tv.currentTextColor
        fun draw() {
            val now = System.currentTimeMillis()
            val el = (now - start) / 1000; val quiet = (now - changed) / 1000
            val w = if (quiet > 60) " · ⚠ taking longer than usual — still working" else if (quiet > 20) " · ⏳ still working, not stuck" else ""
            self = true
            try { tv.text = "⏱ ${el / 60}:${"%02d".format(el % 60)}  $base$w"; tv.setTextColor(if (el > 30) AMBER else color) } finally { self = false }
        }
        lateinit var tick: Runnable
        tick = Runnable { if (!on || (!tv.isAttachedToWindow && System.currentTimeMillis() - start > 3000)) { on = false; return@Runnable }; draw(); h.postDelayed(tick, 1000) }
        fun update(text: String) {
            var t = text
            rx.matchEntire(t)?.let { t = it.groupValues[1] + it.groupValues[2] }
            base = t; changed = System.currentTimeMillis()
            if (t.trimEnd().endsWith("…") || t.trimEnd().endsWith("...")) {
                if (!on) { on = true; start = changed; color = tv.currentTextColor; h.postDelayed(tick, 1000) }
                h.post { if (on) draw() }
            } else if (on) {
                on = false; h.removeCallbacks(tick)
                self = true; try { if (t != text) tv.text = t; tv.setTextColor(color) } finally { self = false }
            }
        }
        tv.addTextChangedListener(object : android.text.TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(s: android.text.Editable?) { if (!self) update(s?.toString() ?: "") }
        })
        update(tv.text?.toString() ?: "")
        return tv
    }

    fun text(c: Context, s: String, size: Float = 15f, color: Int = TEXT, bold: Boolean = false): TextView =
        LightText(c).apply {
            text = s; textSize = size; setTextColor(color)
            if (bold) setTypeface(typeface, Typeface.BOLD)
            setLineSpacing(0f, 1.15f)
        }

    fun title(c: Context, s: String): TextView = text(c, s, 22f, TEXT, true).apply {
        setPadding(0, 0, 0, dp(c, 4))
    }

    fun section(c: Context, s: String): TextView = text(c, s.uppercase(), 11f, MUTED, true).apply {
        letterSpacing = 0.08f
        setPadding(0, dp(c, 12), 0, dp(c, 6))
    }

    fun card(c: Context): LinearLayout = col(c, 14).apply {
        background = round(CARD, dp(c, 16).toFloat())
    }

    fun button(c: Context, s: String, primary: Boolean = false, onClick: () -> Unit): Button =
        Button(c).apply {
            text = s; isAllCaps = false; textSize = 15f
            setTextColor(if (primary) Color.WHITE else TEXT)
            background = round(if (primary) ACCENT else CARD2, dp(c, 12).toFloat())
            minHeight = dp(c, 46); minimumHeight = dp(c, 46)
            setPadding(dp(c, 16), 0, dp(c, 16), 0)
            stateListAnimator = null
            setOnClickListener { onClick() }
        }

    /** Slim button (32 dp high) for headers. */
    fun smallButton(c: Context, s: String, onClick: () -> Unit): TextView =
        text(c, s, 14f, TEXT).apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            setPadding(dp(c, 12), 0, dp(c, 12), 0)
            background = round(CARD2, dp(c, 10).toFloat())
            minHeight = dp(c, 32); height = dp(c, 32)
            setOnClickListener { onClick() }
        }

    fun chip(c: Context, s: String, selected: Boolean, onClick: () -> Unit): TextView =
        text(c, s, 13f, if (selected) Color.WHITE else TEXT).apply {
            gravity = Gravity.CENTER
            includeFontPadding = false
            setPadding(dp(c, 12), dp(c, 7), dp(c, 12), dp(c, 7))
            background = if (selected) round(ACCENT, dp(c, 20).toFloat())
            else round(CARD2, dp(c, 20).toFloat(), 1, 0x33FFFFFF)
            setOnClickListener { onClick() }
        }

    fun chipRow(c: Context): Pair<HorizontalScrollView, LinearLayout> {
        val inner = row(c)
        val sv = HorizontalScrollView(c).apply {
            isHorizontalScrollBarEnabled = false
            addView(inner)
        }
        return sv to inner
    }

    fun input(c: Context, hint: String, multi: Boolean = true, minLines: Int = 4): EditText =
        EditText(c).apply {
            this.hint = hint
            setHintTextColor(MUTED); setTextColor(TEXT); textSize = 16f
            background = round(CARD2, dp(c, 14).toFloat())
            setPadding(dp(c, 14), dp(c, 12), dp(c, 14), dp(c, 12))
            gravity = Gravity.TOP or Gravity.START
            if (multi) {
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or
                    InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
                this.minLines = minLines; maxLines = 14
            } else {
                inputType = InputType.TYPE_CLASS_TEXT
                isSingleLine = true
            }
        }

    /** Always-visible vertical scroll bar inside a text box, and scrolling the box (not the page) when touched. */
    @android.annotation.SuppressLint("ClickableViewAccessibility")
    fun scrollBox(v: android.widget.TextView) {
        v.isVerticalScrollBarEnabled = true
        v.scrollBarStyle = View.SCROLLBARS_INSIDE_INSET
        v.isScrollbarFadingEnabled = false
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            v.verticalScrollbarThumbDrawable = round(ACCENT, dp(v.context, 3).toFloat())
            v.verticalScrollbarTrackDrawable = round(0x22FFFFFF, dp(v.context, 3).toFloat())
        }
        if (v !is EditText) v.movementMethod = android.text.method.ScrollingMovementMethod()
        v.setOnTouchListener { view, e ->
            if (view.canScrollVertically(1) || view.canScrollVertically(-1)) {
                view.parent?.requestDisallowInterceptTouchEvent(e.action != android.view.MotionEvent.ACTION_UP &&
                    e.action != android.view.MotionEvent.ACTION_CANCEL)
            }
            false
        }
    }

    fun gap(c: Context, h: Int): View = View(c).apply {
        layoutParams = LinearLayout.LayoutParams(1, dp(c, h))
    }

    fun lp(w: Int = ViewGroup.LayoutParams.MATCH_PARENT, h: Int = ViewGroup.LayoutParams.WRAP_CONTENT,
           weight: Float = 0f, top: Int = 0, end: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(w, h, weight).apply { topMargin = top; marginEnd = end }

    fun signature(c: Context): TextView = text(c, SIGNATURE, 12f, MUTED).apply {
        gravity = Gravity.CENTER
        setPadding(0, dp(c, 12), 0, dp(c, 6))
    }

    fun copy(c: Context, s: String) {
        val cm = c.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Nova Enhance", s))
        toast(c, "Copied")
    }

    fun toast(c: Context, s: String) = Toast.makeText(c, s, Toast.LENGTH_SHORT).show()
}
