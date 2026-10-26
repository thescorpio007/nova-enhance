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
    const val SIGNATURE = "Designed by A.H"

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

    fun text(c: Context, s: String, size: Float = 15f, color: Int = TEXT, bold: Boolean = false): TextView =
        TextView(c).apply {
            text = s; textSize = size; setTextColor(color)
            if (bold) setTypeface(typeface, Typeface.BOLD)
            setLineSpacing(0f, 1.15f)
        }

    fun title(c: Context, s: String): TextView = text(c, s, 22f, TEXT, true).apply {
        setPadding(0, 0, 0, dp(c, 4))
    }

    fun section(c: Context, s: String): TextView = text(c, s.uppercase(), 12f, MUTED, true).apply {
        letterSpacing = 0.08f
        setPadding(0, dp(c, 18), 0, dp(c, 8))
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

    fun chip(c: Context, s: String, selected: Boolean, onClick: () -> Unit): TextView =
        text(c, s, 14f, if (selected) Color.WHITE else TEXT).apply {
            gravity = Gravity.CENTER
            setPadding(dp(c, 14), dp(c, 8), dp(c, 14), dp(c, 8))
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

    fun gap(c: Context, h: Int): View = View(c).apply {
        layoutParams = LinearLayout.LayoutParams(1, dp(c, h))
    }

    fun lp(w: Int = ViewGroup.LayoutParams.MATCH_PARENT, h: Int = ViewGroup.LayoutParams.WRAP_CONTENT,
           weight: Float = 0f, top: Int = 0, end: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(w, h, weight).apply { topMargin = top; marginEnd = end }

    fun signature(c: Context): TextView = text(c, SIGNATURE, 12f, MUTED).apply {
        gravity = Gravity.CENTER
        setPadding(0, dp(c, 20), 0, dp(c, 8))
    }

    fun copy(c: Context, s: String) {
        val cm = c.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Nova Enhance", s))
        toast(c, "Copied")
    }

    fun toast(c: Context, s: String) = Toast.makeText(c, s, Toast.LENGTH_SHORT).show()
}
