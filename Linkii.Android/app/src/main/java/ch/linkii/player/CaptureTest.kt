package ch.linkii.player

import android.app.Activity
import android.app.AlertDialog
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.PixelCopy
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import java.io.ByteArrayOutputStream

/**
 * Test de faisabilité de l'aperçu du direct (menu technicien → « Test de capture d'écran »).
 *
 * Compare deux méthodes de capture sur ce qui est affiché, avec les mêmes mesures :
 *  - PixelCopy sur la fenêtre (voit la vidéo, la surface réelle) ;
 *  - View.draw sur la vue racine (ne voit pas toujours la vidéo : sort souvent noir).
 * Le résultat dit si l'image est noire, sa taille en Ko une fois réduite (640 px, JPEG 70 %) et le temps de capture.
 * Code de test : sera remplacé par la commande « capture » envoyée par le serveur.
 */
object CaptureTest {
    private const val TARGET_W = 640
    private const val JPEG_QUALITY = 70

    private class Result(val label: String, val bmp: Bitmap?, val jpegKb: Int, val darkPct: Int, val ms: Long, val error: String?)

    fun run(activity: Activity, root: View) {
        val handler = Handler(Looper.getMainLooper())
        val t0 = System.currentTimeMillis()
        // Laisse le temps au menu de se refermer et à la vidéo de reprendre
        handler.postDelayed({
            val viewDraw = captureByDraw(root, t0)
            captureByPixelCopy(activity, root, handler) { pc -> show(activity, pc, viewDraw) }
        }, 1500)
    }

    private fun captureByDraw(root: View, t0: Long): Result {
        val start = System.currentTimeMillis()
        return try {
            val full = Bitmap.createBitmap(root.width, root.height, Bitmap.Config.ARGB_8888)
            root.draw(Canvas(full))
            analyse("View.draw", full, System.currentTimeMillis() - start)
        } catch (e: Exception) { Result("View.draw", null, 0, 0, 0, e.message) }
    }

    private fun captureByPixelCopy(activity: Activity, root: View, handler: Handler, done: (Result) -> Unit) {
        if (Build.VERSION.SDK_INT < 26) { done(Result("PixelCopy", null, 0, 0, 0, "Android 8 minimum")); return }
        val start = System.currentTimeMillis()
        try {
            val full = Bitmap.createBitmap(root.width, root.height, Bitmap.Config.ARGB_8888)
            PixelCopy.request(activity.window, full, { code ->
                if (code == PixelCopy.SUCCESS) done(analyse("PixelCopy", full, System.currentTimeMillis() - start))
                else done(Result("PixelCopy", null, 0, 0, 0, "code $code"))
            }, handler)
        } catch (e: Exception) { done(Result("PixelCopy", null, 0, 0, 0, e.message)) }
    }

    /** Réduit à 640 px, compresse en JPEG et mesure la part de pixels quasi noirs (échantillon d'un pixel sur 8). */
    private fun analyse(label: String, full: Bitmap, ms: Long): Result {
        val h = (TARGET_W.toLong() * full.height / full.width).toInt().coerceAtLeast(1)
        val small = Bitmap.createScaledBitmap(full, TARGET_W, h, true)
        val out = ByteArrayOutputStream()
        small.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, out)
        var dark = 0; var n = 0
        for (y in 0 until h step 8) for (x in 0 until TARGET_W step 8) {
            val p = small.getPixel(x, y)
            val lum = (((p shr 16) and 255) * 3 + ((p shr 8) and 255) * 6 + (p and 255)) / 10
            if (lum < 24) dark++
            n++
        }
        return Result(label, small, out.size() / 1024, dark * 100 / n.coerceAtLeast(1), ms, null)
    }

    private fun line(r: Result) =
        if (r.error != null) "${r.label} : échec (${r.error})"
        else "${r.label} : ${r.jpegKb} Ko · ${r.darkPct} % de noir · ${r.ms} ms"

    private fun show(activity: Activity, pc: Result, draw: Result) {
        val pad = (16 * activity.resources.displayMetrics.density).toInt()
        val box = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad, pad, pad) }
        box.addView(TextView(activity).apply { text = line(pc) + "\n" + line(draw) + "\n\nImage PixelCopy ci-dessous. Une image noire = vidéo ou contenu non capturé." })
        pc.bmp?.let { b -> box.addView(ImageView(activity).apply { setImageBitmap(b); adjustViewBounds = true }, LinearLayout.LayoutParams(-1, -2)) }
        AlertDialog.Builder(activity).setTitle("Test de capture d'écran").setView(box).setPositiveButton("Fermer", null).show()
    }
}
