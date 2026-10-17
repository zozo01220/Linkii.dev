package ch.linkii.backoffice

import android.app.Activity
import android.app.AlertDialog
import android.app.DownloadManager
import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.os.Bundle
import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Environment
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.URLUtil
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ProgressBar
import android.widget.Toast

/**
 * Coquille native autour du back-office web (responsive). Le serveur fournit toute l'interface ;
 * l'app garde la session, gère l'import de fichiers (médiathèque), les téléchargements, la navigation
 * arrière et un écran de secours quand le serveur est injoignable.
 */
class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var progress: ProgressBar
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private var failed = false

    // Domaines de connexion (Microsoft, Google) : ouverts dans l'app, sinon la session ne reviendrait pas.
    private val authHosts = setOf("login.microsoftonline.com", "login.live.com", "accounts.google.com")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = FrameLayout(this)
        web = WebView(this)
        progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100; visibility = View.GONE
            layoutParams = FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, (3 * resources.displayMetrics.density).toInt())
        }
        root.addView(web, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(progress)
        setContentView(root)
        setupFullScreen(root)
        setupWebView()
        if (savedInstanceState != null) web.restoreState(savedInstanceState) else load(intent.getStringExtra(EXTRA_PATH))
        askNotifications()
    }

    /**
     * Plein écran : barres système masquées (un balayage depuis le bord les fait apparaître un instant) et page dessinée
     * jusqu'aux bords. Le contenu est décalé de l'encoche, des barres si elles sont visibles et du clavier : rien ne passe dessous.
     */
    private fun setupFullScreen(root: View) {
        if (Build.VERSION.SDK_INT >= 30) window.setDecorFitsSystemWindows(false)
        else @Suppress("DEPRECATION") root.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        if (Build.VERSION.SDK_INT >= 28) window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = android.view.WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        root.setOnApplyWindowInsetsListener { v, ins ->
            var l: Int; var t: Int; var r: Int; var b: Int
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = ins.getInsets(android.view.WindowInsets.Type.systemBars() or android.view.WindowInsets.Type.displayCutout())
                val ime = ins.getInsets(android.view.WindowInsets.Type.ime())
                l = bars.left; t = bars.top; r = bars.right; b = maxOf(bars.bottom, ime.bottom)
            } else @Suppress("DEPRECATION") {
                l = ins.systemWindowInsetLeft; t = ins.systemWindowInsetTop; r = ins.systemWindowInsetRight; b = ins.systemWindowInsetBottom
            }
            v.setPadding(l, t, r, b)
            ins
        }
        root.requestApplyInsets()
    }

    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= 30) window.insetsController?.apply {
            hide(android.view.WindowInsets.Type.systemBars())
            systemBarsBehavior = android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        } else @Suppress("DEPRECATION") {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()   // après le clavier, une boîte de dialogue ou le sélecteur de fichiers
    }
    /** Ouvre le back-office, sur une page précise quand on vient d'une notification (chemin relatif au serveur uniquement). */
    private fun load(path: String? = null) {
        failed = false
        val safe = path?.takeIf { it.startsWith("/") && !it.startsWith("//") && it != "/" } ?: ""
        web.loadUrl(Prefs.serverUrl(this) + safe)
    }

    override fun onNewIntent(i: Intent) {
        super.onNewIntent(i)
        i.getStringExtra(EXTRA_PATH)?.let { load(it) }
    }

    private fun askNotifications() {
        if (Build.VERSION.SDK_INT >= 33 && App.pushConfigured && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIF)
    }

    private fun serverHost(): String? = Uri.parse(Prefs.serverUrl(this)).host

    private fun setupWebView() {
        CookieManager.getInstance().apply { setAcceptCookie(true); setAcceptThirdPartyCookies(web, true) }
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = true
            mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            setSupportMultipleWindows(false)
            // Google refuse la connexion dans un WebView reconnaissable (« ; wv ») : on se présente comme Chrome mobile.
            userAgentString = userAgentString.replace("; wv", "") + " LinkiiBackOffice/" + BuildConfig.VERSION_NAME
        }
        web.addJavascriptInterface(Bridge(), "LinkiiAdmin")
        web.setDownloadListener { url, ua, disposition, mime, _ ->
            val req = DownloadManager.Request(Uri.parse(url))
                .setMimeType(mime)
                .addRequestHeader("User-Agent", ua)
                .addRequestHeader("Cookie", CookieManager.getInstance().getCookie(url) ?: "")
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, URLUtil.guessFileName(url, disposition, mime))
            (getSystemService(DOWNLOAD_SERVICE) as DownloadManager).enqueue(req)
            Toast.makeText(this, "Téléchargement…", Toast.LENGTH_SHORT).show()
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(v: WebView, r: WebResourceRequest): Boolean {
                val u = r.url
                if (u.scheme == "file" || u.scheme == "about") return false
                val host = u.host
                if (host != null && (host == serverHost() || host in authHosts) && (u.scheme == "https" || u.scheme == "http")) return false
                try { startActivity(Intent(Intent.ACTION_VIEW, u)) } catch (_: ActivityNotFoundException) {}
                return true
            }
            override fun onPageStarted(v: WebView, url: String?, f: Bitmap?) { progress.visibility = View.VISIBLE }
            override fun onPageFinished(v: WebView, url: String?) { progress.visibility = View.GONE; CookieManager.getInstance().flush() }
            override fun onReceivedError(v: WebView, r: WebResourceRequest, e: WebResourceError) {
                if (r.isForMainFrame && !failed) {
                    failed = true
                    v.loadUrl("file:///android_asset/offline.html")
                }
            }
        }
        web.webChromeClient = object : WebChromeClient() {
            override fun onProgressChanged(v: WebView, p: Int) { progress.progress = p }
            override fun onShowFileChooser(v: WebView, cb: ValueCallback<Array<Uri>>, p: FileChooserParams): Boolean {
                fileCallback?.onReceiveValue(null)
                fileCallback = cb
                return try {
                    startActivityForResult(p.createIntent(), REQ_FILE); true
                } catch (_: ActivityNotFoundException) {
                    fileCallback = null; false
                }
            }
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode == REQ_FILE) {
            fileCallback?.onReceiveValue(WebChromeClient.FileChooserParams.parseResult(resultCode, data))
            fileCallback = null
        } else super.onActivityResult(requestCode, resultCode, data)
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (web.canGoBack() && !failed) web.goBack() else super.onBackPressed()
    }

    override fun onSaveInstanceState(out: Bundle) { super.onSaveInstanceState(out); web.saveState(out) }
    override fun onPause() { CookieManager.getInstance().flush(); BiometricGate.onLeave(); super.onPause() }

    override fun onResume() {
        super.onResume()
        // Verrouillage biométrique (désactivé par défaut) : le contenu reste masqué tant que la personne n'est pas reconnue.
        if (Prefs.biometric(this)) web.visibility = View.INVISIBLE
        BiometricGate.check(this) { ok -> if (ok) web.visibility = View.VISIBLE else finish() }
    }
    override fun onDestroy() { web.destroy(); super.onDestroy() }

    private fun askServer() {
        val input = EditText(this).apply {
            setText(Prefs.serverUrl(this@MainActivity)); setSingleLine()
            hint = getString(R.string.server_hint)
            inputType = android.text.InputType.TYPE_TEXT_VARIATION_URI
        }
        AlertDialog.Builder(this).setTitle(R.string.server_title).setView(input)
            .setPositiveButton(R.string.ok) { _, _ ->
                val url = Prefs.normalize(input.text.toString())
                if (url == null) Toast.makeText(this, R.string.server_invalid, Toast.LENGTH_LONG).show()
                else { Prefs.setServerUrl(this, url); CookieManager.getInstance().removeAllCookies(null); load() }
            }
            .setNegativeButton(R.string.cancel, null).show()
    }

    /** Pont `window.LinkiiAdmin` : utilisé par l'écran de secours, et par le back-office pour savoir qu'il tourne dans l'app. */
    inner class Bridge {
        @JavascriptInterface fun retry() { runOnUiThread { load() } }
        @JavascriptInterface fun changeServer() { runOnUiThread { askServer() } }
        @JavascriptInterface fun serverUrl(): String = Prefs.serverUrl(this@MainActivity)
        @JavascriptInterface fun version(): String = BuildConfig.VERSION_NAME
        /** Jeton de notification de l'appareil (vide tant que Firebase n'a pas répondu, ou si les notifications ne sont pas configurées). */
        @JavascriptInterface fun pushToken(): String = Prefs.pushToken(this@MainActivity)
        @JavascriptInterface fun deviceName(): String = (Build.MANUFACTURER + " " + Build.MODEL).trim()
        /** Verrouillage biométrique : disponible sur l'appareil, activé, et réglage. */
        @JavascriptInterface fun biometricAvailable(): Boolean = BiometricGate.available(this@MainActivity)
        @JavascriptInterface fun biometricEnabled(): Boolean = Prefs.biometric(this@MainActivity)
        @JavascriptInterface fun setBiometric(on: Boolean) { Prefs.setBiometric(this@MainActivity, on && BiometricGate.available(this@MainActivity)) }
    }

    companion object {
        const val EXTRA_PATH = "path"
        private const val REQ_FILE = 1
        private const val REQ_NOTIF = 2
    }
}
