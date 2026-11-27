package com.nova.enhance

import android.content.Context
import android.graphics.*
import android.graphics.drawable.Drawable
import android.text.SpannableString
import android.text.Spanned
import android.text.style.ImageSpan
import android.widget.TextView

/**
 * 🟢 🟡 🔴 The same status lights as the PC (style C · raised button): green = working · yellow = something ongoing
 * (gently pulsing) · red = not working · orange = this week. Every Nova text that shows one of these gets the drawn light,
 * the word stays next to it. Designed by A.HAMROUNI
 */
object Lights {
    enum class Kind { GREEN, YELLOW, RED, ORANGE }
    private val marks = mapOf("🟢" to Kind.GREEN, "🟡" to Kind.YELLOW, "🔴" to Kind.RED, "🟠" to Kind.ORANGE)
    fun has(s: CharSequence?) = s != null && marks.keys.any { s.contains(it) }

    private fun colours(k: Kind) = when (k) {
        Kind.GREEN -> intArrayOf(0xFF7AF0AD.toInt(), 0xFF18A957.toInt(), 0xFF0C6B37.toInt())
        Kind.YELLOW -> intArrayOf(0xFFFFE58A.toInt(), 0xFFF0B000.toInt(), 0xFF9A6A00.toInt())
        Kind.RED -> intArrayOf(0xFFFF9A9A.toInt(), 0xFFE02A2A.toInt(), 0xFF8E1010.toInt())
        Kind.ORANGE -> intArrayOf(0xFFFFC080.toInt(), 0xFFF07A10.toInt(), 0xFF964600.toInt())
    }

    fun pulse(): Float = (0.5 + 0.5 * Math.sin(System.currentTimeMillis() / 1600.0 * 2 * Math.PI)).toFloat()

    class Light(val k: Kind, private val px: Int) : Drawable() {
        private val p = Paint(Paint.ANTI_ALIAS_FLAG)
        init { setBounds(0, 0, px, px) }
        override fun draw(cv: Canvas) {
            val b = bounds; val w = b.width().toFloat(); val cx = b.exactCenterX(); val cy = b.exactCenterY()
            val col = colours(k)
            val f = if (k == Kind.YELLOW) 0.72f + 0.28f * pulse() else 1f
            val hi = dim(col[0], f); val mid = dim(col[1], f); val lo = dim(col[2], f)
            val ring = maxOf(1f, w / 14f)
            p.shader = null; p.style = Paint.Style.STROKE; p.strokeWidth = ring; p.color = Color.argb(70, 255, 255, 255)
            cv.drawCircle(cx, cy, w / 2 - ring / 2, p)
            val r = w / 2 - ring * 2
            p.color = 0xFF0B0D12.toInt(); cv.drawCircle(cx, cy, r + ring / 2, p)
            p.style = Paint.Style.FILL
            p.shader = LinearGradient(cx - r * 0.4f, cy - r, cx + r * 0.4f, cy + r, intArrayOf(hi, mid, lo), floatArrayOf(0f, 0.55f, 1f), Shader.TileMode.CLAMP)
            cv.drawCircle(cx, cy, r, p)
            p.shader = null; p.style = Paint.Style.STROKE
            p.strokeWidth = maxOf(1f, r / 5f); p.color = Color.argb(95, 255, 255, 255)
            val ri = r * 0.78f
            cv.drawArc(cx - ri, cy - ri, cx + ri, cy + ri, 200f, 140f, false, p)
            p.strokeWidth = maxOf(1f, r / 4.5f); p.color = Color.argb(110, 0, 0, 0)
            val rb = r * 0.84f
            cv.drawArc(cx - rb, cy - rb, cx + rb, cy + rb, 20f, 140f, false, p)
        }
        private fun dim(c: Int, f: Float) = if (f >= 1f) c else Color.rgb((Color.red(c) * f).toInt(), (Color.green(c) * f).toInt(), (Color.blue(c) * f).toInt())
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(cf: ColorFilter?) {}
        @Deprecated("Deprecated in Java") override fun getOpacity() = PixelFormat.TRANSLUCENT
    }

    /** The text with every 🟢🟡🔴🟠 drawn as a light of the text's size. */
    fun apply(s: CharSequence, textPx: Float): CharSequence {
        if (!has(s)) return s
        val sp = SpannableString(s)
        val size = (textPx * 1.05f).toInt().coerceAtLeast(8)
        for ((m, k) in marks) {
            var i = s.indexOf(m.toString())
            while (i >= 0) { sp.setSpan(ImageSpan(Light(k, size), ImageSpan.ALIGN_BASELINE), i, i + m.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE); i = s.indexOf(m.toString(), i + m.length) }
        }
        return sp
    }

    fun hasYellow(s: CharSequence?) = s?.contains("🟡") == true
}

/** A Nova text view: status lights are drawn (yellow breathes while it shows). */
class LightText(c: Context) : TextView(c) {
    private var self = false
    private val beat = object : Runnable { override fun run() { if (isAttachedToWindow && Lights.hasYellow(text)) { invalidate(); postDelayed(this, 110) } } }
    override fun setText(t: CharSequence?, type: BufferType?) {
        if (self || t == null || !Lights.has(t)) { super.setText(t, type); return }
        self = true
        try { super.setText(Lights.apply(t, textSize), type) } finally { self = false }
        if (Lights.hasYellow(t)) { removeCallbacks(beat); postDelayed(beat, 110) }
    }
    override fun onAttachedToWindow() { super.onAttachedToWindow(); if (Lights.hasYellow(text)) post(beat) }
    override fun setTextSize(size: Float) { super.setTextSize(size); if (Lights.has(text)) text = text.toString() }
}
