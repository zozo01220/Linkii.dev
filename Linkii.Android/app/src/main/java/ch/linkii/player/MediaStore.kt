package ch.linkii.player

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import java.io.File
import java.io.FileInputStream
import java.io.FilterInputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.Executors

/**
 * Médiathèque locale de l'appareil : tous les fichiers d'une liste de lecture (images, vidéos, fichiers des apps…)
 * sont téléchargés dans le stockage privé de l'app, sans quota du WebView et sans purge par le système.
 *
 * Le player (JavaScript) donne la liste des adresses `/media/…` à garder ; les téléchargements se font un par un,
 * avec reprise après coupure, nouvelle tentative automatique et retrait des fichiers qui ne sont plus utilisés.
 * Les requêtes du WebView vers ces adresses sont servies depuis le disque (requêtes Range comprises) : hors ligne
 * comme en ligne, la lecture ne dépend pas du réseau dès que le fichier est là.
 */
class MediaStore(context: Context, private val prefs: Prefs) {
    /** État pour l'affichage : `active` = téléchargement en cours (l'icône de progression est visible). */
    data class Snapshot(val active: Boolean, val fraction: Float, val pending: Int, val ready: Int, val total: Int, val bytes: Long)

    @Volatile var onChange: ((Snapshot) -> Unit)? = null

    private val dir = File(context.filesDir, "media").apply { mkdirs() }
    private val exec = Executors.newSingleThreadExecutor { r -> Thread(r, "linkii-media").apply { isDaemon = true } }
    private val ui = Handler(Looper.getMainLooper())
    private val lock = java.lang.Object()

    private var wanted: List<String> = emptyList()
    private var token = ""
    private var generation = 0
    private var started = false

    @Volatile private var passActive = false   // un passage de téléchargement est en cours (le player attend sa fin)
    @Volatile private var visible = false      // des octets arrivent : l'icône s'affiche (pas pendant les essais sans réseau)
    @Volatile private var fraction = 0f

    /** Reprend la dernière liste connue (démarrage de l'app, avant même que la page ne soit chargée). */
    fun resume() {
        val list = prefs.mediaList
        if (list.isNotEmpty()) sync(list.split('\n').filter { it.isNotEmpty() }, prefs.mediaToken)
    }

    /** Remplace la liste à garder. Sans effet si elle est identique : relance seulement un essai en attente. */
    fun sync(paths: List<String>, tokenValue: String) {
        val clean = paths.mapNotNull(::clean).distinct()
        synchronized(lock) {
            val same = clean == wanted
            wanted = clean
            token = tokenValue
            if (!same) { generation++; prefs.mediaList = clean.joinToString("\n"); prefs.mediaToken = tokenValue }
            if (clean.none { !localExists(it) }) { passActive = false; if (!same) purge(clean); lock.notifyAll(); emit(); return }
            passActive = true
            if (!started) { started = true; exec.execute(::loop) } else lock.notifyAll()   // essai en attente : on le relance tout de suite
        }
    }

    /** Oublie tout (écran désappairé). */
    fun clear() {
        synchronized(lock) { wanted = emptyList(); generation++; prefs.mediaList = ""; prefs.mediaToken = ""; lock.notifyAll() }
        dir.listFiles()?.forEach { it.delete() }
        emit()
    }

    fun snapshot(): Snapshot {
        val list = synchronized(lock) { wanted }
        val ready = list.count { localExists(it) }
        val bytes = dir.listFiles()?.filter { !it.name.endsWith(".part") }?.sumOf { it.length() } ?: 0L
        return Snapshot(visible && passActive, fraction, list.size - ready, ready, list.size, bytes)
    }

    fun isBusy() = passActive

    /* ---------- Téléchargement ---------- */

    private fun loop() {
        while (true) {
            val gen: Int; val list: List<String>; val tk: String
            synchronized(lock) { gen = generation; list = wanted; tk = token }
            val missing = list.filter { !localExists(it) }
            var failed = 0
            var done = 0
            visible = false
            fraction = 0f
            for (p in missing) {
                if (gen != generationNow()) break
                if (download(p, tk, done, missing.size, gen)) done++ else failed++
            }
            if (gen != generationNow()) continue   // la liste a changé : on repart avec la nouvelle
            purge(list)
            synchronized(lock) {
                passActive = false; visible = false
                emit(); lock.notifyAll()
                if (gen == generation) {
                    if (failed > 0) lock.wait(RETRY_MS)   // réseau coupé ou serveur absent : nouvel essai plus tard (ou tout de suite si le player relance)
                    else lock.wait()                       // tout est là : on attend une nouvelle liste
                }
                if (wanted.any { !localExists(it) }) passActive = true
            }
        }
    }

    private fun generationNow() = synchronized(lock) { generation }

    private fun download(path: String, tk: String, doneBefore: Int, count: Int, gen: Int): Boolean {
        val target = fileFor(path)
        val part = File(dir, target.name + ".part")
        var conn: HttpURLConnection? = null
        try {
            var have = if (part.exists()) part.length() else 0L
            conn = (URL(prefs.serverUrl.trimEnd('/') + path).openConnection() as HttpURLConnection).apply {
                connectTimeout = 15_000
                readTimeout = 30_000
                setRequestProperty("X-Token", tk)
                if (have > 0) setRequestProperty("Range", "bytes=$have-")
            }
            val code = conn.responseCode
            if (code != 200 && code != 206) { if (code == 404 || code == 410) part.delete(); return false }
            if (code == 200) have = 0L   // le serveur ignore la reprise : on repart de zéro
            val total = if (code == 206) Regex("/(\\d+)$").find(conn.getHeaderField("Content-Range") ?: "")?.groupValues?.get(1)?.toLongOrNull() ?: -1L
                        else conn.contentLengthLong
            if (total > 0 && dir.usableSpace < total - have + RESERVE) { part.delete(); return false }   // pas assez de place
            visible = true
            var written = have
            conn.inputStream.use { input ->
                java.io.FileOutputStream(part, code == 206).use { out ->
                    val buf = ByteArray(64 * 1024)
                    var lastEmit = 0L
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        written += n
                        if (gen != generationNow()) return false
                        val now = System.currentTimeMillis()
                        if (now - lastEmit > 250) {
                            lastEmit = now
                            fraction = (doneBefore + (if (total > 0) written.toFloat() / total else 0.5f)) / count
                            emit()
                        }
                    }
                }
            }
            if (total > 0 && part.length() != total) return false   // coupure en cours de route : la reprise complétera
            if (!part.renameTo(target)) return false
            fraction = (doneBefore + 1f) / count
            emit()
            return true
        } catch (_: Exception) {
            return false
        } finally {
            conn?.disconnect()
        }
    }

    private fun purge(keep: List<String>) {
        val names = keep.map { fileFor(it).name }.toSet()
        dir.listFiles()?.forEach { f ->
            if (f.name !in names && f.name.removeSuffix(".part") !in names) f.delete()
        }
    }

    private fun emit() {
        val cb = onChange ?: return
        val s = snapshot()
        ui.post { cb(s) }
    }

    /* ---------- Noms de fichiers ---------- */

    private fun clean(url: String): String? {
        val path = Uri.parse(url).path ?: return null
        return if (path.startsWith("/") && !path.contains("..")) path else null
    }

    private fun fileFor(path: String): File {
        val h = MessageDigest.getInstance("SHA-1").digest(path.toByteArray()).joinToString("") { "%02x".format(it) }
        val ext = path.substringAfterLast('/').substringAfterLast('.', "").takeIf { it.length in 1..5 && it.all(Char::isLetterOrDigit) }
        return File(dir, if (ext != null) "$h.$ext" else h)
    }

    private fun localExists(path: String) = fileFor(path).isFile

    /* ---------- Lecture depuis le disque (WebView et service worker) ---------- */

    /** Réponse locale à une requête du WebView, ou null (le WebView va alors sur le réseau). */
    fun serve(req: WebResourceRequest): WebResourceResponse? {
        if (req.method != "GET") return null
        if (req.url.host != Uri.parse(prefs.playerUrl()).host) return null
        val path = req.url.path ?: return null
        val f = fileFor(path)
        if (!f.isFile) return null
        val size = f.length()
        val mime = mimeOf(path)
        val headers = HashMap<String, String>().apply {
            put("Accept-Ranges", "bytes")
            put("Access-Control-Allow-Origin", "*")
        }
        val range = req.requestHeaders.entries.firstOrNull { it.key.equals("Range", true) }?.value
        val m = range?.let { Regex("bytes=(\\d*)-(\\d*)").find(it) }
        if (m != null && size > 0) {
            val a = m.groupValues[1]; val b = m.groupValues[2]
            val start: Long; val end: Long
            if (a.isEmpty() && b.isNotEmpty()) { start = maxOf(0L, size - b.toLong()); end = size - 1 }   // « bytes=-N » : les N derniers octets
            else { start = a.toLongOrNull() ?: 0L; end = minOf(b.toLongOrNull() ?: (size - 1), size - 1) }
            if (start > end || start >= size) {
                headers["Content-Range"] = "bytes */$size"
                return WebResourceResponse(mime, null, 416, "Range Not Satisfiable", headers, java.io.ByteArrayInputStream(ByteArray(0)))
            }
            headers["Content-Range"] = "bytes $start-$end/$size"
            headers["Content-Length"] = (end - start + 1).toString()
            return WebResourceResponse(mime, null, 206, "Partial Content", headers, slice(f, start, end - start + 1))
        }
        headers["Content-Length"] = size.toString()
        return WebResourceResponse(mime, null, 200, "OK", headers, FileInputStream(f))
    }

    private fun slice(f: File, start: Long, length: Long): InputStream {
        val fis = FileInputStream(f)
        fis.channel.position(start)
        return object : FilterInputStream(fis) {
            private var left = length
            override fun read(): Int {
                if (left <= 0) return -1
                val r = super.read(); if (r >= 0) left--; return r
            }
            override fun read(b: ByteArray, off: Int, len: Int): Int {
                if (left <= 0) return -1
                val n = super.read(b, off, minOf(len.toLong(), left).toInt()); if (n > 0) left -= n; return n
            }
            override fun available(): Int = minOf(super.available().toLong(), left).toInt()
        }
    }

    private fun mimeOf(path: String): String = when (path.substringAfterLast('.', "").lowercase()) {
        "mp4", "m4v" -> "video/mp4"
        "webm" -> "video/webm"
        "png" -> "image/png"
        "jpg", "jpeg" -> "image/jpeg"
        "gif" -> "image/gif"
        "webp" -> "image/webp"
        "avif" -> "image/avif"
        "svg" -> "image/svg+xml"
        "pdf" -> "application/pdf"
        "html", "htm" -> "text/html"
        "js", "mjs" -> "text/javascript"
        "css" -> "text/css"
        "json" -> "application/json"
        "woff2" -> "font/woff2"
        "woff" -> "font/woff"
        "ttf" -> "font/ttf"
        "txt" -> "text/plain"
        else -> "application/octet-stream"
    }

    private companion object {
        const val RETRY_MS = 30_000L
        const val RESERVE = 200L * 1024 * 1024   // on garde 200 Mo libres pour le système
    }
}
