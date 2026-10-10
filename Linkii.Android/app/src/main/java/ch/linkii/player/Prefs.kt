package ch.linkii.player

import android.content.Context

/** Réglages locaux de l'appareil (adresse du serveur, code PIN du menu technicien). */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("linkii", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = sp.getString("server", null) ?: BuildConfig.DEFAULT_SERVER_URL
        set(v) = sp.edit().putString("server", v.trim().trimEnd('/')).apply()

    /** Code par défaut 9999 tant qu'aucun autre n'est défini ; vide = menu sans code. */
    var pin: String
        get() = sp.getString("pin", DEFAULT_PIN) ?: DEFAULT_PIN
        set(v) = sp.edit().putString("pin", v.trim()).apply()

    /** Dernière liste de médias à garder (une adresse par ligne) et jeton de l'écran : le téléchargement reprend dès le démarrage. */
    var mediaList: String
        get() = sp.getString("media_list", "") ?: ""
        set(v) = sp.edit().putString("media_list", v).apply()

    var mediaToken: String
        get() = sp.getString("media_token", "") ?: ""
        set(v) = sp.edit().putString("media_token", v).apply()

    /** Adresse complète du player : l'adresse du serveur, suivie de /player/ sauf si elle le contient déjà. */
    fun playerUrl(): String {
        val base = serverUrl
        return if (base.contains("/player")) base else "$base/player/"
    }
}

private const val DEFAULT_PIN = "9999"
