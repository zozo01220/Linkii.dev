package ch.linkii.player

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.net.ConnectivityManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.StatFs
import android.provider.Settings
import android.text.InputType
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.ArrayAdapter
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast

/**
 * Coquille native autour du player web de Linkii.
 *
 * Le player (appairage par code, diffusion, cache hors ligne via service worker) est celui du navigateur ;
 * l'app ajoute : démarrage automatique, plein écran permanent, écran toujours allumé, relance en cas de panne
 * (page injoignable, processus WebView tué) et un menu technicien protégé par un code PIN.
 */
class MainActivity : Activity() {
    private lateinit var prefs: Prefs
    private lateinit var root: FrameLayout
    private var web: WebView? = null
    private val handler = Handler(Looper.getMainLooper())

    // Menu technicien : 5 appuis sur OK (télécommande) ou 5 touchers dans le coin supérieur droit, en moins de 3 s
    private val gestureWindowMs = 3000L
    private var okPresses = 0
    private var cornerTaps = 0
    private var gestureStart = 0L
    private var menuOpen = false

    private val retry = Runnable { web?.loadUrl(prefs.playerUrl()) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        window.addFlags(
            WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
                WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON or
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED
        )
        root = FrameLayout(this).apply { setBackgroundColor(NAVY) }
        setContentView(root)
        createWebView()
    }

    override fun onResume() {
        super.onResume()
        hideSystemBars()
        // Verrouillage de l'app (kiosque) seulement si l'appareil l'autorise sans fenêtre de confirmation
        val dpm = getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager
        if (dpm.isLockTaskPermitted(packageName)) try { startLockTask() } catch (_: Exception) {}
        web?.onResume()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    override fun onDestroy() {
        handler.removeCallbacks(retry)
        web?.destroy()
        web = null
        super.onDestroy()
    }

    // Le bouton Retour ne quitte pas le player
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {}

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(android.view.WindowInsets.Type.systemBars())
                it.systemBarsBehavior = android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        }
    }

    /* ---------- WebView ---------- */

    @SuppressLint("SetJavaScriptEnabled")
    private fun createWebView() {
        val wv = WebView(this)
        wv.setBackgroundColor(NAVY)
        wv.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true            // localStorage : jeton de l'écran, dernière liste de lecture
            databaseEnabled = true
            mediaPlaybackRequiresUserGesture = false   // vidéos lancées sans toucher l'écran
            cacheMode = WebSettings.LOAD_DEFAULT
            allowFileAccess = false             // la page de secours est lue depuis les ressources (file:///android_asset)
            allowContentAccess = false
            setSupportMultipleWindows(false)
            userAgentString = userAgentString + " LinkiiPlayer/" + BuildConfig.VERSION_NAME + " Android"
        }
        wv.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                // Reste sur le serveur Linkii et sur la page de secours ; tout le reste est ignoré
                val host = Uri.parse(prefs.playerUrl()).host
                return !(request.url.host == host || request.url.scheme == "file")
            }

            override fun onPageFinished(view: WebView, url: String) {
                if (!url.startsWith("file:")) handler.removeCallbacks(retry)
            }

            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (!request.isForMainFrame) return
                // Ni réseau ni cache du service worker : page de secours, puis nouvelle tentative
                view.loadUrl("file:///android_asset/offline.html")
                handler.removeCallbacks(retry)
                handler.postDelayed(retry, RETRY_MS)
            }

            // Le processus de rendu a été tué (mémoire) : on recrée la vue et on recharge le player
            override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                root.removeView(view)
                view.destroy()
                createWebView()
                return true
            }
        }
        web?.let { root.removeView(it); it.destroy() }
        web = wv
        root.addView(wv, FrameLayout.LayoutParams(-1, -1))
        wv.requestFocus()
        wv.loadUrl(prefs.playerUrl())
    }

    /* ---------- Gestes du menu technicien ---------- */

    override fun dispatchKeyEvent(e: KeyEvent): Boolean {
        val ok = e.keyCode == KeyEvent.KEYCODE_DPAD_CENTER || e.keyCode == KeyEvent.KEYCODE_ENTER
        if (ok && e.action == KeyEvent.ACTION_UP && !menuOpen) {
            val now = System.currentTimeMillis()
            if (now - gestureStart > gestureWindowMs) { gestureStart = now; okPresses = 0 }
            if (++okPresses >= 5) { okPresses = 0; openMenu() }
        }
        return super.dispatchKeyEvent(e)
    }

    override fun dispatchTouchEvent(e: MotionEvent): Boolean {
        if (e.action == MotionEvent.ACTION_DOWN && !menuOpen) {
            val zone = 140 * resources.displayMetrics.density
            if (e.x > root.width - zone && e.y < zone) {
                val now = System.currentTimeMillis()
                if (now - gestureStart > gestureWindowMs) { gestureStart = now; cornerTaps = 0 }
                if (++cornerTaps >= 5) { cornerTaps = 0; openMenu() }
            }
        }
        return super.dispatchTouchEvent(e)
    }

    /* ---------- Menu technicien ---------- */

    private fun openMenu() {
        menuOpen = true
        if (prefs.pin.isEmpty()) showMenu() else askPin()
    }

    private fun askPin() {
        val input = EditText(this).apply { inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD }
        AlertDialog.Builder(this)
            .setTitle("Menu technicien")
            .setMessage("Code PIN")
            .setView(input)
            .setPositiveButton("OK") { _, _ ->
                if (input.text.toString() == prefs.pin) showMenu()
                else { Toast.makeText(this, "Code PIN incorrect", Toast.LENGTH_SHORT).show(); menuOpen = false }
            }
            .setNegativeButton("Annuler") { _, _ -> menuOpen = false }
            .setOnCancelListener { menuOpen = false }
            .show()
    }

    private fun showMenu() {
        val actions = listOf(
            "Reprendre la diffusion" to { },
            "Recharger le player" to { web?.loadUrl(prefs.playerUrl()) },
            "Changer de serveur" to { askServer() },
            "Définir le code PIN" to { askNewPin() },
            "Autoriser le démarrage automatique" to { requestOverlayPermission() },
            "Désappairer cet écran" to { confirmUnpair() },
            "Quitter l'application" to { quit() }
        )
        // Un AlertDialog n'affiche pas ensemble un message et une liste : le diagnostic et les actions sont dans une vue commune
        val pad = (24 * resources.displayMetrics.density).toInt()
        val info = TextView(this).apply { text = diagnostics(); setPadding(pad, pad / 2, pad, pad / 2) }
        val list = ListView(this).apply {
            adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_list_item_1, actions.map { it.first })
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(info)
            addView(list)
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle("Menu technicien")
            .setView(content)
            .setOnDismissListener { menuOpen = false; hideSystemBars() }
            .create()
        list.setOnItemClickListener { _, _, i, _ -> dialog.dismiss(); actions[i].second() }
        dialog.show()
    }

    private fun diagnostics(): String {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val online = cm.activeNetworkInfo?.isConnected == true
        val st = StatFs(Environment.getDataDirectory().path)
        val freeGb = st.availableBytes / 1_073_741_824.0
        return "Serveur : ${prefs.serverUrl}\n" +
            "Version : ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})\n" +
            "Réseau : ${if (online) "connecté" else "hors ligne"}\n" +
            "Stockage libre : ${"%.1f".format(freeGb)} Go\n" +
            "PIN : ${if (prefs.pin.isEmpty()) "non défini" else "défini"}"
    }

    private fun askServer() {
        val input = EditText(this).apply {
            setText(prefs.serverUrl)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        }
        AlertDialog.Builder(this)
            .setTitle("Adresse du serveur Linkii")
            .setView(input)
            .setPositiveButton("OK") { _, _ ->
                val v = input.text.toString().trim()
                if (v.startsWith("https://") || v.startsWith("http://")) { prefs.serverUrl = v; web?.loadUrl(prefs.playerUrl()) }
                else Toast.makeText(this, "L'adresse doit commencer par https://", Toast.LENGTH_LONG).show()
            }
            .setNegativeButton("Annuler", null)
            .show()
    }

    private fun askNewPin() {
        val input = EditText(this).apply { inputType = InputType.TYPE_CLASS_NUMBER; hint = "4 chiffres minimum, vide pour retirer" }
        AlertDialog.Builder(this)
            .setTitle("Définir le code PIN")
            .setView(input)
            .setPositiveButton("OK") { _, _ ->
                val v = input.text.toString()
                if (v.isEmpty() || v.length >= 4) prefs.pin = v
                else Toast.makeText(this, "4 chiffres minimum", Toast.LENGTH_SHORT).show()
            }
            .setNegativeButton("Annuler", null)
            .show()
    }

    private fun confirmUnpair() {
        AlertDialog.Builder(this)
            .setTitle("Désappairer cet écran")
            .setMessage("Cet écran affichera de nouveau un code d'appairage. Sa liste de lecture n'est pas supprimée dans le back-office.")
            .setPositiveButton("Désappairer") { _, _ ->
                // Mêmes clés que la fonction unpair() du player web
                web?.evaluateJavascript(
                    "['lk_token','lk_screen','lk_playlist','lk_acked','lk_brand'].forEach(function(k){localStorage.removeItem(k)});location.reload()",
                    null
                )
            }
            .setNegativeButton("Annuler", null)
            .show()
    }

    private fun requestOverlayPermission() {
        if (Build.VERSION.SDK_INT >= 23 && !Settings.canDrawOverlays(this)) {
            try {
                startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
            } catch (_: Exception) {
                Toast.makeText(this, "Réglage indisponible sur cet appareil : définissez Linkii comme écran d'accueil.", Toast.LENGTH_LONG).show()
            }
        } else {
            Toast.makeText(this, "Démarrage automatique déjà autorisé.", Toast.LENGTH_SHORT).show()
        }
    }

    private fun quit() {
        try { stopLockTask() } catch (_: Exception) {}
        finishAndRemoveTask()
    }

    private companion object {
        val NAVY = Color.parseColor("#0B1F3A")
        const val RETRY_MS = 10_000L
    }
}
