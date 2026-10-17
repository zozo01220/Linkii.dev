package ch.linkii.player

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.PixelCopy
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URL

/**
 * Aperçu du direct demandé depuis le back-office : capture de ce que l'écran affiche (PixelCopy sur la fenêtre : voit aussi la vidéo,
 * contrairement à View.draw), réduite à 960 px de large, JPEG 70 % (environ 40 Ko), envoyée au serveur.
 */
object Capture {
    private const val WIDTH = 960
    private const val QUALITY = 70

    /** Rend le JPEG, ou null avec la raison de l'échec (affichée dans le back-office). */
    fun grab(activity: Activity, done: (ByteArray?, String?) -> Unit) {
        if (Build.VERSION.SDK_INT < 26) { done(null, "Capture impossible avant Android 8"); return }
        val view = activity.window.decorView
        val w = view.width
        val h = view.height
        if (w <= 0 || h <= 0) { done(null, "Écran indisponible"); return }
        val bmp = Bitmap.createBitmap(WIDTH, (WIDTH.toLong() * h / w).toInt().coerceAtLeast(1), Bitmap.Config.ARGB_8888)
        try {
            PixelCopy.request(activity.window, Rect(0, 0, w, h), bmp, { code ->
                if (code != PixelCopy.SUCCESS) { done(null, "Capture refusée par l'appareil (code $code)"); return@request }
                val out = ByteArrayOutputStream()
                bmp.compress(Bitmap.CompressFormat.JPEG, QUALITY, out)
                done(out.toByteArray(), null)
            }, Handler(Looper.getMainLooper()))
        } catch (e: Exception) { done(null, e.message ?: "Capture impossible") }
    }

    /** Envoie l'image au serveur (hors du fil principal). Retourne null si elle est partie, sinon la raison de l'échec. */
    fun upload(url: String, token: String, jpeg: ByteArray): String? = try {
        val c = URL(url).openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.connectTimeout = 8000
        c.readTimeout = 10000
        c.doOutput = true
        c.setRequestProperty("X-Token", token)
        c.setRequestProperty("Content-Type", "image/jpeg")
        c.setFixedLengthStreamingMode(jpeg.size)
        c.outputStream.use { it.write(jpeg) }
        val code = c.responseCode
        c.disconnect()
        if (code in 200..299) null else "Envoi refusé par le serveur ($code)"
    } catch (e: Exception) { "Envoi impossible : ${e.message}" }
}
