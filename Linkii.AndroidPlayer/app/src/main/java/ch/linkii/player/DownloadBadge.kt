package ch.linkii.player

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.view.View
import android.view.animation.LinearInterpolator

/**
 * Petite icône de progression en bas à droite, comme le téléchargement d'une application depuis un store :
 * un anneau qui se remplit autour d'une flèche, puis une coche avant de disparaître.
 * Discrète (44 dp, semi-transparente) et jamais interactive.
 */
class DownloadBadge(context: Context) : View(context) {
    private val d = resources.displayMetrics.density
    private val stroke = 3 * d

    private val disc = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(0xB8, 0x0B, 0x1F, 0x3A) }
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = stroke; color = Color.argb(0x4D, 255, 255, 255) }
    private val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = stroke; strokeCap = Paint.Cap.ROUND; color = ACCENT }
    private val glyph = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeWidth = 2.2f * d; strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND; color = Color.WHITE
    }
    private val oval = RectF()
    private val path = Path()

    private var progress = 0f          // 0..1 ; < 0 = indéterminé (anneau qui tourne)
    private var done = false
    private var spin = 0f
    private val spinner = ValueAnimator.ofFloat(0f, 360f).apply {
        duration = 1100; repeatCount = ValueAnimator.INFINITE; interpolator = LinearInterpolator()
        addUpdateListener { spin = it.animatedValue as Float; invalidate() }
    }
    private val hide = Runnable { animate().alpha(0f).setDuration(400).withEndAction { visibility = GONE }.start() }

    init {
        isClickable = false
        isFocusable = false
        visibility = GONE
        alpha = 0f
    }

    override fun onMeasure(w: Int, h: Int) = setMeasuredDimension((44 * d).toInt(), (44 * d).toInt())

    /** Téléchargement en cours ; `fraction` entre 0 et 1 (0 = on ne sait pas encore). */
    fun showProgress(fraction: Float) {
        removeCallbacks(hide)
        done = false
        progress = if (fraction <= 0.001f) -1f else fraction.coerceAtMost(1f)
        if (progress < 0 && !spinner.isRunning) spinner.start()
        if (progress >= 0 && spinner.isRunning) spinner.cancel()
        if (visibility != VISIBLE || alpha < 1f) { visibility = VISIBLE; animate().cancel(); animate().alpha(1f).setDuration(250).start() }
        invalidate()
    }

    /** Fin du téléchargement : coche, puis l'icône s'efface. */
    fun showDone() {
        if (visibility != VISIBLE) return
        spinner.cancel()
        done = true
        progress = 1f
        invalidate()
        removeCallbacks(hide)
        postDelayed(hide, 1500)
    }

    fun dismissNow() {
        removeCallbacks(hide); spinner.cancel(); animate().cancel(); alpha = 0f; visibility = GONE
    }

    override fun onDraw(c: Canvas) {
        val s = width.toFloat()
        val cx = s / 2f; val cy = s / 2f
        c.drawCircle(cx, cy, s / 2f, disc)
        val r = s / 2f - stroke * 1.2f
        oval.set(cx - r, cy - r, cx + r, cy + r)
        c.drawCircle(cx, cy, r, track)
        if (progress < 0) c.drawArc(oval, spin - 90f, 90f, false, arc)
        else c.drawArc(oval, -90f, 360f * progress, false, arc)

        val g = s * 0.17f
        path.reset()
        if (done) {   // coche
            path.moveTo(cx - g, cy + g * 0.05f); path.lineTo(cx - g * 0.3f, cy + g * 0.75f); path.lineTo(cx + g, cy - g * 0.7f)
        } else {      // flèche vers le bas
            path.moveTo(cx, cy - g); path.lineTo(cx, cy + g)
            path.moveTo(cx - g * 0.75f, cy + g * 0.2f); path.lineTo(cx, cy + g); path.lineTo(cx + g * 0.75f, cy + g * 0.2f)
        }
        c.drawPath(path, glyph)
    }

    override fun onDetachedFromWindow() {
        spinner.cancel(); removeCallbacks(hide)
        super.onDetachedFromWindow()
    }

    private companion object {
        val ACCENT = Color.parseColor("#4C9AFF")
    }
}
