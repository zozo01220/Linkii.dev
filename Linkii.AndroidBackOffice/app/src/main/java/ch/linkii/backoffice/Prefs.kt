package ch.linkii.backoffice

import android.content.Context

/** Réglages de l'app : seule l'adresse du serveur est gardée ; la session reste dans les cookies du WebView. */
object Prefs {
    private fun sp(c: Context) = c.getSharedPreferences("linkii-backoffice", Context.MODE_PRIVATE)

    fun serverUrl(c: Context): String =
        sp(c).getString("server", null) ?: BuildConfig.DEFAULT_SERVER_URL

    fun setServerUrl(c: Context, url: String) = sp(c).edit().putString("server", url).apply()

    fun pushToken(c: Context): String = sp(c).getString("pushToken", "") ?: ""
    fun setPushToken(c: Context, t: String) = sp(c).edit().putString("pushToken", t).apply()

    /** Verrouillage biométrique (voir BiometricGate) : désactivé par défaut. */
    fun biometric(c: Context): Boolean = sp(c).getBoolean("biometric", false)
    fun setBiometric(c: Context, on: Boolean) = sp(c).edit().putBoolean("biometric", on).apply()

    /** Normalise une saisie : https obligatoire (http admis seulement pour localhost / 10.0.2.2), sans « / » final. */
    fun normalize(input: String): String? {
        var s = input.trim().trimEnd('/')
        if (s.isEmpty()) return null
        if (!s.contains("://")) s = "https://$s"
        val uri = android.net.Uri.parse(s)
        val host = uri.host ?: return null
        val local = host == "localhost" || host == "10.0.2.2"
        if (uri.scheme != "https" && !(local && uri.scheme == "http")) return null
        return s
    }
}
